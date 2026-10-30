using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace H3Monitor.Core;

public sealed record InmetProvenance(
    string ArchiveUrl,
    string EntryName,
    string? ETag,
    DateTimeOffset? LastModified,
    string CsvSha256,
    string RawCsvPath,
    string OriginalTimezone,
    string QualityStatus,
    long ArchiveBytes,
    long CompressedEntryBytes,
    long CsvBytes,
    long BytesTransferred,
    double PayloadReductionPercent,
    DateTimeOffset RetrievedAtUtc);

public sealed record InmetCoverageSummary(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int DataRows,
    int ValidTimestampRows,
    int InvalidTimestampRows,
    int DistinctHourlySlots,
    int ExpectedHourlySlots,
    int DuplicateTimestamps,
    double CoveragePercent);

public sealed record InmetBandSummary(
    string Label,
    int Samples,
    double Percent);

public sealed record InmetVariableSummary(
    string Name,
    string Unit,
    int ValidSamples,
    int MissingSamples,
    double MissingPercent,
    double? Minimum,
    double? P05,
    double? P25,
    double? Median,
    double? P75,
    double? P95,
    double? Maximum,
    double? Average,
    IReadOnlyList<InmetBandSummary> Bands);

public sealed record InmetEnvelopeComparison(
    string Variable,
    string Unit,
    double RealP05,
    double RealMedian,
    double RealP95,
    double SyntheticP05,
    double SyntheticMedian,
    double SyntheticP95,
    double RealOutsideSyntheticP05P95Percent);

public sealed record InmetStudySummary(
    int Year,
    string StationCode,
    InmetProvenance Provenance,
    IReadOnlyDictionary<string, string> StationMetadata,
    string SourceEncoding,
    InmetCoverageSummary Coverage,
    IReadOnlyList<InmetVariableSummary> Variables,
    IReadOnlyList<InmetEnvelopeComparison> ExternalEnvelopeComparison,
    bool RawUnvalidated = true,
    string InterpretationLimit =
        "Clima externo real foi comparado apenas ao envelope das entradas externas sintéticas. Não foi calculado risco interno, falha ou causalidade.");

public sealed class InmetStudyService
{
    private static readonly Regex StationPattern = new(
        "^[A-Z0-9]{3,10}$",
        RegexOptions.CultureInvariant);
    private readonly RemoteZipRangeExtractor _extractor;
    private readonly Func<int, Uri> _archiveUrlFactory;

    public InmetStudyService(
        RemoteZipRangeExtractor extractor,
        Func<int, Uri>? archiveUrlFactory = null)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _archiveUrlFactory = archiveUrlFactory ??
            (year => new Uri($"https://portal.inmet.gov.br/uploads/dadoshistoricos/{year}.zip"));
    }

    public async Task<InmetStudySummary> CollectAsync(
        int year,
        string stationCode,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100)
        {
            throw new ArgumentOutOfRangeException(nameof(year));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        stationCode = stationCode.Trim().ToUpperInvariant();
        if (!StationPattern.IsMatch(stationCode))
        {
            throw new ArgumentException("Código de estação INMET inválido.", nameof(stationCode));
        }

        var archiveUrl = _archiveUrlFactory(year);
        var entry = await _extractor.ExtractAsync(
            archiveUrl,
            name => IsStationEntry(name, year, stationCode),
            cancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(entry.Content)).ToLowerInvariant();
        var rawDirectory = Path.Combine(outputDirectory, "raw");
        Directory.CreateDirectory(rawDirectory);
        var rawPath = Path.Combine(
            rawDirectory,
            $"{year}-{stationCode}-{sha256[..12]}.csv");
        await PreserveRawFileAsync(rawPath, entry.Content, cancellationToken);

        var dataset = new InmetCsvParser().Parse(entry.Content);
        var provenance = new InmetProvenance(
            entry.ArchiveUrl.ToString(),
            entry.EntryName,
            entry.ETag,
            entry.LastModified,
            sha256,
            Path.GetFullPath(rawPath),
            "UTC",
            "raw_unvalidated",
            entry.ArchiveBytes,
            entry.CompressedBytes,
            entry.ExpandedBytes,
            entry.BytesTransferred,
            Math.Round((1 - entry.BytesTransferred / (double)entry.ArchiveBytes) * 100, 3),
            DateTimeOffset.UtcNow);
        var summary = InmetStudyAnalyzer.Analyze(
            year,
            stationCode,
            provenance,
            dataset);
        await InmetStudyExporter.ExportAsync(
            outputDirectory,
            summary,
            cancellationToken);
        return summary;
    }

    private static bool IsStationEntry(string entryName, int year, string stationCode)
    {
        var normalized = entryName.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return normalized.StartsWith($"{year}/", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".CSV", StringComparison.OrdinalIgnoreCase) &&
               fileName.Contains($"_{stationCode}_", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PreserveRawFileAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await VerifyExistingRawFileAsync(path, content, cancellationToken);
            return;
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                await VerifyExistingRawFileAsync(path, content, cancellationToken);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task VerifyExistingRawFileAsync(
        string path,
        byte[] expectedContent,
        CancellationToken cancellationToken)
    {
        var existingHash = SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken));
        var incomingHash = SHA256.HashData(expectedContent);
        if (!existingHash.AsSpan().SequenceEqual(incomingHash))
        {
            throw new IOException("O caminho imutável do CSV bruto já contém outro conteúdo.");
        }
    }
}

public static class InmetStudyAnalyzer
{
    public static InmetStudySummary Analyze(
        int year,
        string stationCode,
        InmetProvenance provenance,
        InmetDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(dataset);
        var ordered = dataset.Observations.OrderBy(item => item.TimestampUtc).ToArray();
        var distinctTimestamps = ordered.Select(item => item.TimestampUtc).Distinct().Count();
        var expectedSlots = checked((int)(ordered[^1].TimestampUtc - ordered[0].TimestampUtc).TotalHours + 1);
        var coverage = new InmetCoverageSummary(
            ordered[0].TimestampUtc,
            ordered[^1].TimestampUtc,
            dataset.DataRows,
            ordered.Length,
            dataset.InvalidTimestampRows,
            distinctTimestamps,
            expectedSlots,
            ordered.Length - distinctTimestamps,
            Math.Round(distinctTimestamps * 100.0 / expectedSlots, 3));

        var variables = new[]
        {
            BuildVariable(
                "Temperatura do ar",
                "°C",
                ordered.Select(item => item.AirTemperatureC),
                [
                    ("< 24 °C", double.NegativeInfinity, 24.0),
                    ("24 a < 28 °C", 24.0, 28.0),
                    ("28 a < 32 °C", 28.0, 32.0),
                    ("≥ 32 °C", 32.0, double.PositiveInfinity)
                ]),
            BuildVariable(
                "Umidade relativa",
                "%",
                ordered.Select(item => item.RelativeHumidityPercent),
                [
                    ("< 60%", double.NegativeInfinity, 60.0),
                    ("60 a < 70%", 60.0, 70.0),
                    ("70 a < 80%", 70.0, 80.0),
                    ("80 a < 90%", 80.0, 90.0),
                    ("≥ 90%", 90.0, double.PositiveInfinity)
                ]),
            BuildVariable(
                "Ponto de orvalho externo",
                "°C",
                ordered.Select(item => item.DewPointC),
                [
                    ("< 20 °C", double.NegativeInfinity, 20.0),
                    ("20 a < 23 °C", 20.0, 23.0),
                    ("23 a < 26 °C", 23.0, 26.0),
                    ("≥ 26 °C", 26.0, double.PositiveInfinity)
                ])
        };

        var comparison = CompareExternalEnvelope(year, ordered);
        return new InmetStudySummary(
            year,
            stationCode,
            provenance,
            dataset.Metadata,
            dataset.SourceEncoding,
            coverage,
            variables,
            comparison);
    }

    private static InmetVariableSummary BuildVariable(
        string name,
        string unit,
        IEnumerable<double?> source,
        IReadOnlyList<(string Label, double Minimum, double Maximum)> bands)
    {
        var all = source.ToArray();
        var values = all.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        var missing = all.Length - values.Length;
        var summaries = bands.Select(band =>
        {
            var count = values.Count(value => value >= band.Minimum && value < band.Maximum);
            return new InmetBandSummary(
                band.Label,
                count,
                values.Length == 0 ? 0 : Math.Round(count * 100.0 / values.Length, 3));
        }).ToArray();

        return new InmetVariableSummary(
            name,
            unit,
            values.Length,
            missing,
            Math.Round(missing * 100.0 / all.Length, 3),
            values.Length == 0 ? null : values[0],
            Quantile(values, 0.05),
            Quantile(values, 0.25),
            Quantile(values, 0.50),
            Quantile(values, 0.75),
            Quantile(values, 0.95),
            values.Length == 0 ? null : values[^1],
            values.Length == 0 ? null : Math.Round(values.Average(), 3),
            summaries);
    }

    private static IReadOnlyList<InmetEnvelopeComparison> CompareExternalEnvelope(
        int year,
        IReadOnlyList<InmetObservation> real)
    {
        var days = DateTime.IsLeapYear(year) ? 366 : 365;
        var synthetic = new SyntheticScenarioGenerator().Generate(
            new SyntheticScenarioOptions(Days: days, IntervalMinutes: 60, Seed: 20261029));
        return
        [
            Compare(
                "Temperatura do ar externa",
                "°C",
                real.Where(item => item.AirTemperatureC.HasValue)
                    .Select(item => item.AirTemperatureC!.Value),
                synthetic.Select(item => item.OutdoorTemperatureC)),
            Compare(
                "Umidade relativa externa",
                "%",
                real.Where(item => item.RelativeHumidityPercent.HasValue)
                    .Select(item => item.RelativeHumidityPercent!.Value),
                synthetic.Select(item => item.OutdoorRelativeHumidityPercent)),
            Compare(
                "Ponto de orvalho externo",
                "°C",
                real.Where(item => item.DewPointC.HasValue)
                    .Select(item => item.DewPointC!.Value),
                synthetic.Select(item => DewPointCalculator.CalculateCelsius(
                    item.OutdoorTemperatureC,
                    item.OutdoorRelativeHumidityPercent)))
        ];
    }

    private static InmetEnvelopeComparison Compare(
        string variable,
        string unit,
        IEnumerable<double> realSource,
        IEnumerable<double> syntheticSource)
    {
        var real = realSource.Order().ToArray();
        var synthetic = syntheticSource.Order().ToArray();
        if (real.Length == 0 || synthetic.Length == 0)
        {
            throw new InvalidDataException("Não há dados suficientes para comparar envelopes externos.");
        }

        var syntheticP05 = Quantile(synthetic, 0.05)!.Value;
        var syntheticP95 = Quantile(synthetic, 0.95)!.Value;
        var outside = real.Count(value => value < syntheticP05 || value > syntheticP95);
        return new InmetEnvelopeComparison(
            variable,
            unit,
            Quantile(real, 0.05)!.Value,
            Quantile(real, 0.50)!.Value,
            Quantile(real, 0.95)!.Value,
            syntheticP05,
            Quantile(synthetic, 0.50)!.Value,
            syntheticP95,
            Math.Round(outside * 100.0 / real.Length, 3));
    }

    private static double? Quantile(IReadOnlyList<double> sortedValues, double probability)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        var position = (sortedValues.Count - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var value = lower == upper
            ? sortedValues[lower]
            : sortedValues[lower] +
              (sortedValues[upper] - sortedValues[lower]) * (position - lower);
        return Math.Round(value, 3);
    }
}

public static class InmetStudyExporter
{
    public static async Task ExportAsync(
        string outputDirectory,
        InmetStudySummary summary,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "inmet-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "RELATORIO_INMET.md"),
            BuildMarkdown(summary),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static string BuildMarkdown(InmetStudySummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Estudo climático INMET — {summary.StationCode}/{summary.Year}")
            .AppendLine()
            .AppendLine("> **DADO OFICIAL BRUTO/NÃO CONSISTIDO.** Ausências foram preservadas; nenhuma imputação foi feita.")
            .AppendLine()
            .AppendLine("## Proveniência e transferência")
            .AppendLine()
            .AppendLine($"- URL: `{summary.Provenance.ArchiveUrl}`")
            .AppendLine($"- Entrada: `{summary.Provenance.EntryName}`")
            .AppendLine($"- SHA-256 do CSV: `{summary.Provenance.CsvSha256}`")
            .AppendLine($"- CSV bruto preservado: `{summary.Provenance.RawCsvPath}`")
            .AppendLine($"- ETag: `{summary.Provenance.ETag ?? "não informado"}`")
            .AppendLine($"- Last-Modified: `{summary.Provenance.LastModified?.ToString("O") ?? "não informado"}`")
            .AppendLine($"- Fuso original: {summary.Provenance.OriginalTimezone}")
            .AppendLine($"- Payload transferido: {summary.Provenance.BytesTransferred:N0} de {summary.Provenance.ArchiveBytes:N0} bytes ({summary.Provenance.PayloadReductionPercent:0.000}% evitados)")
            .AppendLine()
            .AppendLine("## Cobertura")
            .AppendLine()
            .AppendLine($"- Período UTC: {summary.Coverage.StartUtc:O} a {summary.Coverage.EndUtc:O}")
            .AppendLine($"- Linhas de dados: {summary.Coverage.DataRows:N0}")
            .AppendLine($"- Instantes horários distintos: {summary.Coverage.DistinctHourlySlots:N0} de {summary.Coverage.ExpectedHourlySlots:N0} ({summary.Coverage.CoveragePercent:0.000}%)")
            .AppendLine($"- Timestamps inválidos: {summary.Coverage.InvalidTimestampRows:N0}; duplicados: {summary.Coverage.DuplicateTimestamps:N0}")
            .AppendLine()
            .AppendLine("## Estatísticas descritivas")
            .AppendLine()
            .AppendLine("| Variável | Válidos | Ausentes | Mín | P05 | P50 | P95 | Máx | Média |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var variable in summary.Variables)
        {
            builder.Append("| ").Append(variable.Name)
                .Append(" | ").Append(variable.ValidSamples)
                .Append(" | ").Append(variable.MissingSamples).Append(" (")
                .Append(Number(variable.MissingPercent)).Append("%)")
                .Append(" | ").Append(Number(variable.Minimum)).Append(' ').Append(variable.Unit)
                .Append(" | ").Append(Number(variable.P05))
                .Append(" | ").Append(Number(variable.Median))
                .Append(" | ").Append(Number(variable.P95))
                .Append(" | ").Append(Number(variable.Maximum))
                .Append(" | ").Append(Number(variable.Average)).AppendLine(" |");
        }

        builder.AppendLine()
            .AppendLine("## Faixas observadas")
            .AppendLine();
        foreach (var variable in summary.Variables)
        {
            builder.Append("- **").Append(variable.Name).Append(":** ")
                .AppendLine(string.Join(", ", variable.Bands.Select(
                    band => $"{band.Label} = {band.Samples:N0} ({band.Percent:0.###}%)")));
        }

        builder.AppendLine()
            .AppendLine("## Envelope externo real versus entrada sintética")
            .AppendLine()
            .AppendLine("| Variável | Real P05/P50/P95 | Sintético P05/P50/P95 | Real fora do P05–P95 sintético |")
            .AppendLine("|---|---:|---:|---:|");
        foreach (var comparison in summary.ExternalEnvelopeComparison)
        {
            builder.Append("| ").Append(comparison.Variable)
                .Append(" | ").Append(Number(comparison.RealP05)).Append(" / ")
                .Append(Number(comparison.RealMedian)).Append(" / ")
                .Append(Number(comparison.RealP95)).Append(' ').Append(comparison.Unit)
                .Append(" | ").Append(Number(comparison.SyntheticP05)).Append(" / ")
                .Append(Number(comparison.SyntheticMedian)).Append(" / ")
                .Append(Number(comparison.SyntheticP95)).Append(' ').Append(comparison.Unit)
                .Append(" | ").Append(Number(comparison.RealOutsideSyntheticP05P95Percent))
                .AppendLine("% |");
        }

        builder.AppendLine()
            .AppendLine("## Limite de interpretação")
            .AppendLine()
            .AppendLine(summary.InterpretationLimit)
            .AppendLine("A divergência entre envelopes serve para recalibrar as entradas externas do simulador. Ela não autoriza inferir condensação interna, falha de ativo ou efeito de manutenção.");
        return builder.ToString();
    }

    private static string Number(double? value) => value?.ToString(
        "0.###",
        CultureInfo.InvariantCulture) ?? "n/d";
}
