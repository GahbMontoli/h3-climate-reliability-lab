using System.Security.Cryptography;
using System.Text.Json;

namespace H3Monitor.Core;

public sealed record H3BiExportContext(
    int SchemaVersion,
    string ProjectId,
    string RunId,
    string SourceId,
    string EvidenceClass,
    string QualityStatus,
    DateTimeOffset ExportedAtUtc)
{
    public static H3BiExportContext CreateSynthetic(
        string runId,
        string sourceId,
        DateTimeOffset? exportedAtUtc = null) =>
        new(
            1,
            "h3-monitor",
            runId,
            sourceId,
            "synthetic",
            "valid",
            (exportedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());

    public void Validate()
    {
        if (SchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        }

        ValidateIdentifier(ProjectId, nameof(ProjectId));
        ValidateIdentifier(RunId, nameof(RunId));
        ValidateIdentifier(SourceId, nameof(SourceId));
        ValidateIdentifier(EvidenceClass, nameof(EvidenceClass));
        ValidateIdentifier(QualityStatus, nameof(QualityStatus));
        if (ExportedAtUtc == default)
        {
            throw new ArgumentException("O horário de exportação é obrigatório.", nameof(ExportedAtUtc));
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("Identificador BI inválido.", parameterName);
        }
    }
}

public sealed record H3BiTableManifest(
    string FileName,
    int Rows,
    string Grain,
    long Bytes = 0,
    string Sha256 = "");

public sealed record H3BiManifest(
    int SchemaVersion,
    string ProjectId,
    string RunId,
    string SourceId,
    string EvidenceClass,
    string QualityStatus,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<H3BiTableManifest> Tables,
    string InterpretationLimit =
        "Dados sintéticos validam o comportamento do software e não constituem evidência causal ou de campo.");

internal static class H3BiManifestWriter
{
    public static async Task WriteAsync(
        string path,
        H3BiExportContext context,
        IReadOnlyList<H3BiTableManifest> tables,
        CancellationToken cancellationToken)
    {
        context.Validate();
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("Manifesto sem diretório de saída.", nameof(path));
        var materializedTables = new List<H3BiTableManifest>(tables.Count);
        foreach (var table in tables)
        {
            if (string.IsNullOrWhiteSpace(table.FileName) ||
                table.FileName != Path.GetFileName(table.FileName) ||
                table.Rows < 0 ||
                string.IsNullOrWhiteSpace(table.Grain))
            {
                throw new ArgumentException("Tabela BI inválida.", nameof(tables));
            }

            var tablePath = Path.Combine(outputDirectory, table.FileName);
            if (!File.Exists(tablePath))
            {
                throw new FileNotFoundException(
                    "A tabela BI deve existir antes da criação do manifesto.",
                    tablePath);
            }

            string sha256;
            await using (var stream = new FileStream(
                             tablePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81_920,
                             useAsync: true))
            {
                sha256 = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
            }

            materializedTables.Add(table with
            {
                Bytes = new FileInfo(tablePath).Length,
                Sha256 = sha256
            });
        }

        var manifest = new H3BiManifest(
            context.SchemaVersion,
            context.ProjectId,
            context.RunId,
            context.SourceId,
            context.EvidenceClass,
            context.QualityStatus,
            context.ExportedAtUtc.ToUniversalTime(),
            materializedTables);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }),
            cancellationToken);
    }
}

public static class H3BiManifestVerifier
{
    public static async Task VerifyAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        await using var manifestStream = new FileStream(
            fullManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: true);
        var manifest = await JsonSerializer.DeserializeAsync<H3BiManifest>(
                manifestStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Manifesto BI vazio ou inválido.");

        new H3BiExportContext(
                manifest.SchemaVersion,
                manifest.ProjectId,
                manifest.RunId,
                manifest.SourceId,
                manifest.EvidenceClass,
                manifest.QualityStatus,
                manifest.ExportedAtUtc)
            .Validate();

        if (manifest.Tables is null || manifest.Tables.Count == 0)
        {
            throw new InvalidDataException("Manifesto BI sem tabelas.");
        }

        var outputDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("Manifesto BI sem diretório.");
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in manifest.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(table.FileName) ||
                table.FileName != Path.GetFileName(table.FileName) ||
                !fileNames.Add(table.FileName) ||
                table.Rows < 0 ||
                table.Bytes < 0 ||
                string.IsNullOrWhiteSpace(table.Grain) ||
                string.IsNullOrWhiteSpace(table.Sha256) ||
                table.Sha256.Length != 64 ||
                table.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException("Tabela inválida no manifesto BI.");
            }

            var tablePath = Path.Combine(outputDirectory, table.FileName);
            if (!File.Exists(tablePath))
            {
                throw new InvalidDataException($"Tabela BI ausente: {table.FileName}");
            }

            var fileInfo = new FileInfo(tablePath);
            if (fileInfo.Length != table.Bytes)
            {
                throw new InvalidDataException($"Tamanho divergente: {table.FileName}");
            }

            await using var tableStream = new FileStream(
                tablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                useAsync: true);
            var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(tableStream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!actualHash.Equals(table.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 divergente: {table.FileName}");
            }

            var actualRows = Math.Max(0, File.ReadLines(tablePath).Count() - 1);
            if (actualRows != table.Rows)
            {
                throw new InvalidDataException($"Quantidade de linhas divergente: {table.FileName}");
            }
        }
    }
}
