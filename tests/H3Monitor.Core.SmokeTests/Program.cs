using H3Monitor.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("Ponto de orvalho em saturação", DewPointAtSaturation),
    ("Ponto de orvalho conhecido", KnownDewPoint),
    ("Umidade inválida é rejeitada", InvalidHumidityIsRejected),
    ("Faixas de risco experimentais", RiskBands),
    ("Gerador é determinístico", GeneratorIsDeterministic),
    ("Quantidade de amostras", SampleCount),
    ("Estatísticas preservam aviso sintético", StatisticsAreSynthetic),
    ("Contrato BI rejeita identificador inseguro", BiContractRejectsUnsafeIdentifier),
    ("Contrato BI rejeita classe de evidência desconhecida", BiContractRejectsUnknownEvidenceClass),
    ("Manifesto BI aceita exportação íntegra", BiManifestAcceptsValidExport),
    ("Manifesto BI rejeita CSV alterado", BiManifestRejectsTamperedCsv),
    ("Manifesto BI rejeita caminho inseguro", BiManifestRejectsUnsafePath),
    ("Relatórios são gerados", ReportsAreGenerated),
    ("Experimento é determinístico e pareado", ExperimentIsDeterministicAndPaired),
    ("Relatório de pesquisa não depende de HTML", ExperimentReportsAreGenerated),
    ("INMET rejeita umidade fisicamente impossível", InmetRejectsImpossibleHumidity),
    ("INMET é extraído por faixas com fixture local", InmetRangeFixtureIsImported),
    ("INMET rejeita servidor que ignora Range", InmetRejectsFullDownload),
    ("INMET rejeita estrutura ZIP corrompida", InmetRejectsCorruptZip)
};

foreach (var test in tests)
{
    try
    {
        test.Run();
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FALHA: {test.Name}: {exception.Message}");
        return 1;
    }
}

Console.WriteLine($"{tests.Length} testes do H3 Monitor passaram.");
return 0;

static void DewPointAtSaturation()
{
    var result = DewPointCalculator.CalculateCelsius(25, 100);
    EqualClose(25, result, 0.001);
}

static void KnownDewPoint()
{
    var result = DewPointCalculator.CalculateCelsius(30, 80);
    EqualClose(26.17, result, 0.15);
}

static void InvalidHumidityIsRejected()
{
    try
    {
        _ = DewPointCalculator.CalculateCelsius(30, 0);
        throw new InvalidOperationException("A umidade zero foi aceita.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
}

static void RiskBands()
{
    EqualValue(H3RiskLevel.Condensation, H3RiskClassifier.Classify(-0.1));
    EqualValue(H3RiskLevel.Critical, H3RiskClassifier.Classify(0.8));
    EqualValue(H3RiskLevel.High, H3RiskClassifier.Classify(2.5));
    EqualValue(H3RiskLevel.Attention, H3RiskClassifier.Classify(4.5));
    EqualValue(H3RiskLevel.Normal, H3RiskClassifier.Classify(7));
}

static void GeneratorIsDeterministic()
{
    var generator = new SyntheticScenarioGenerator();
    var options = new SyntheticScenarioOptions(Days: 1, IntervalMinutes: 60, Seed: 42);
    var first = generator.Generate(options);
    var second = generator.Generate(options);
    EqualValue(first[5], second[5]);
}

static void SampleCount()
{
    var samples = new SyntheticScenarioGenerator().Generate(
        new SyntheticScenarioOptions(Days: 2, IntervalMinutes: 10));
    EqualValue(288, samples.Count);
}

static void StatisticsAreSynthetic()
{
    var samples = new SyntheticScenarioGenerator().Generate(
        new SyntheticScenarioOptions(Days: 3, IntervalMinutes: 10));
    var statistics = new H3StatisticsAnalyzer().Analyze(samples);
    True(statistics.SyntheticData, "O resumo perdeu a marcação de dado sintético.");
    True(statistics.HighRiskEvents > 0, "O cenário não produziu eventos de risco.");
}

static void BiContractRejectsUnsafeIdentifier()
{
    Throws<ArgumentException>(() => H3BiExportContext.CreateSynthetic(
        "run;inseguro",
        "h3-synthetic-generator-v1").Validate());
}

static void BiContractRejectsUnknownEvidenceClass()
{
    Throws<ArgumentException>(() => new H3BiExportContext(
        1,
        "h3-monitor",
        "evidence-class-test",
        "h3-test-source",
        "field",
        "valid",
        new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero)).Validate());
}

static void BiManifestAcceptsValidExport()
{
    WithSyntheticExport((directory, _) =>
        H3BiManifestVerifier.VerifyAsync(Path.Combine(directory, "h3-bi-manifest.json"))
            .GetAwaiter()
            .GetResult());
}

static void BiManifestRejectsTamperedCsv()
{
    WithSyntheticExport((directory, _) =>
    {
        File.AppendAllText(Path.Combine(directory, "h3-synthetic.csv"), Environment.NewLine);
        Throws<InvalidDataException>(() =>
            H3BiManifestVerifier.VerifyAsync(Path.Combine(directory, "h3-bi-manifest.json"))
                .GetAwaiter()
                .GetResult());
    });
}

static void BiManifestRejectsUnsafePath()
{
    WithSyntheticExport((directory, _) =>
    {
        var manifestPath = Path.Combine(directory, "h3-bi-manifest.json");
        var manifest = File.ReadAllText(manifestPath)
            .Replace("h3-synthetic.csv", "../h3-synthetic.csv", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, manifest);
        Throws<InvalidDataException>(() =>
            H3BiManifestVerifier.VerifyAsync(manifestPath).GetAwaiter().GetResult());
    });
}

static void WithSyntheticExport(Action<string, H3BiExportContext> assertion)
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "h3-monitor-manifest-tests",
        Guid.NewGuid().ToString("N"));

    try
    {
        var samples = new SyntheticScenarioGenerator().Generate(
            new SyntheticScenarioOptions(Days: 1, IntervalMinutes: 60));
        var context = H3BiExportContext.CreateSynthetic(
            "manifest-verifier-test",
            "h3-synthetic-generator-v1",
            new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        new H3ReportExporter().ExportAsync(
                directory,
                samples,
                new H3StatisticsAnalyzer().Analyze(samples),
                context)
            .GetAwaiter()
            .GetResult();
        assertion(directory, context);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void ReportsAreGenerated()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "h3-monitor-tests",
        Guid.NewGuid().ToString("N"));

    try
    {
        var samples = new SyntheticScenarioGenerator().Generate(
            new SyntheticScenarioOptions(Days: 1, IntervalMinutes: 30));
        var statistics = new H3StatisticsAnalyzer().Analyze(samples);
        var context = H3BiExportContext.CreateSynthetic(
            "simulation-contract-test",
            "h3-synthetic-generator-v1",
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        new H3ReportExporter().ExportAsync(directory, samples, statistics, context)
            .GetAwaiter()
            .GetResult();

        True(File.Exists(Path.Combine(directory, "h3-synthetic.csv")), "CSV ausente.");
        True(File.Exists(Path.Combine(directory, "h3-summary.json")), "JSON ausente.");
        True(File.Exists(Path.Combine(directory, "h3-dashboard.html")), "Dashboard ausente.");
        var html = File.ReadAllText(Path.Combine(directory, "h3-dashboard.html"));
        True(html.Contains("DADOS SINTÉTICOS", StringComparison.Ordinal), "Aviso ausente.");
        AssertBiContract(
            directory,
            "h3-synthetic.csv",
            "h3-bi-manifest.json",
            samples.Count,
            context.RunId);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void ExperimentIsDeterministicAndPaired()
{
    var options = new H3ExperimentOptions(
        Replications: 3,
        Days: 7,
        IntervalMinutes: 60,
        Seed: 42);
    var runner = new H3ExperimentRunner();
    var first = runner.Run(options);
    var second = runner.Run(options);

    EqualValue(first.Effects[0], second.Effects[0]);
    True(first.SyntheticData, "O experimento perdeu a marcação sintética.");
    var combined = first.Effects.Single(item => item.InterventionId == "combined-control");
    True(
        combined.AverageExposureHoursAvoided > 0,
        "O controle combinado não reduziu a exposição no cenário de teste.");
    True(
        first.Observations
            .Where(item => item.InterventionId == "combined-control")
            .All(item => item.InterventionExposureHours <= item.BaselineExposureHours),
        "A comparação pareada produziu aumento inesperado no controle combinado.");
}

static void ExperimentReportsAreGenerated()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        "h3-monitor-experiment-tests",
        Guid.NewGuid().ToString("N"));

    try
    {
        var result = new H3ExperimentRunner().Run(new H3ExperimentOptions(
            Replications: 2,
            Days: 2,
            IntervalMinutes: 60,
            Seed: 99));
        var context = H3BiExportContext.CreateSynthetic(
            "experiment-contract-test",
            "h3-paired-experiment-v1",
            new DateTimeOffset(2026, 10, 31, 12, 5, 0, TimeSpan.Zero));
        new H3ExperimentExporter().ExportAsync(directory, result, context)
            .GetAwaiter()
            .GetResult();

        True(
            File.Exists(Path.Combine(directory, "h3-experiment-observations.csv")),
            "CSV de observações ausente.");
        True(
            File.Exists(Path.Combine(directory, "h3-experiment-summary.json")),
            "JSON de pesquisa ausente.");
        True(
            File.Exists(Path.Combine(directory, "RELATORIO_PESQUISA_H3.md")),
            "Relatório Markdown ausente.");
        True(
            !Directory.EnumerateFiles(directory, "*.html").Any(),
            "O experimento criou HTML sem necessidade.");
        var report = File.ReadAllText(Path.Combine(directory, "RELATORIO_PESQUISA_H3.md"));
        True(
            report.Contains("DADOS SINTÉTICOS", StringComparison.Ordinal),
            "Aviso de evidência sintética ausente.");
        AssertBiContract(
            directory,
            "h3-experiment-observations.csv",
            "h3-experiment-bi-manifest.json",
            result.Observations.Count,
            context.RunId);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void AssertBiContract(
    string directory,
    string csvFileName,
    string manifestFileName,
    int expectedRows,
    string expectedRunId)
{
    var csvPath = Path.Combine(directory, csvFileName);
    var manifestPath = Path.Combine(directory, manifestFileName);
    True(File.Exists(manifestPath), $"Manifesto BI ausente: {manifestFileName}");
    var header = File.ReadLines(csvPath).First().TrimStart('\uFEFF');
    foreach (var column in new[]
             {
                 "schema_version", "project_id", "run_id", "source_id",
                 "evidence_class", "quality_status", "observed_at_utc",
                 "observed_time_basis", "ingested_at_utc", "interval_minutes"
             })
    {
        True(header.Split(';').Contains(column), $"Coluna BI ausente: {column}");
    }

    using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
    var root = manifest.RootElement;
    EqualValue(1, root.GetProperty("schemaVersion").GetInt32());
    EqualValue("h3-monitor", root.GetProperty("projectId").GetString()!);
    EqualValue(expectedRunId, root.GetProperty("runId").GetString()!);
    EqualValue("synthetic", root.GetProperty("evidenceClass").GetString()!);
    var table = root.GetProperty("tables")[0];
    EqualValue(csvFileName, table.GetProperty("fileName").GetString()!);
    EqualValue(expectedRows, table.GetProperty("rows").GetInt32());
    EqualValue(new FileInfo(csvPath).Length, table.GetProperty("bytes").GetInt64());
    var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csvPath)))
        .ToLowerInvariant();
    EqualValue(expectedHash, table.GetProperty("sha256").GetString()!);
}

static void InmetRangeFixtureIsImported()
{
    var archiveBytes = CreateInmetFixture(out var expectedCsv);
    var handler = new RangeFixtureHandler(archiveBytes, honorRanges: true);
    using var client = new HttpClient(handler);
    var service = new InmetStudyService(
        new RemoteZipRangeExtractor(client),
        _ => new Uri("https://fixture.local/2025.zip"));
    var directory = Path.Combine(
        Path.GetTempPath(),
        "h3-inmet-tests",
        Guid.NewGuid().ToString("N"));

    try
    {
        var summary = service.CollectAsync(2025, "A101", directory)
            .GetAwaiter()
            .GetResult();
        EqualValue(4, summary.Coverage.DataRows);
        EqualValue(4, summary.Coverage.DistinctHourlySlots);
        EqualValue("raw_unvalidated", summary.Provenance.QualityStatus);
        True(
            summary.Provenance.BytesTransferred < archiveBytes.Length,
            "O fixture inteiro foi transferido em vez de faixas.");
        True(handler.RangeRequests > 0, "Nenhuma requisição Range foi observada.");
        EqualValue(1, summary.Variables[0].MissingSamples);
        EqualValue(1, summary.Variables[1].MissingSamples);
        EqualValue(1, summary.Variables[2].MissingSamples);
        True(File.Exists(summary.Provenance.RawCsvPath), "CSV bruto não foi preservado.");
        True(
            File.ReadAllBytes(summary.Provenance.RawCsvPath).AsSpan().SequenceEqual(expectedCsv),
            "CSV bruto preservado diverge do conteúdo extraído.");
        True(File.Exists(Path.Combine(directory, "inmet-summary.json")), "Resumo INMET ausente.");
        True(File.Exists(Path.Combine(directory, "RELATORIO_INMET.md")), "Relatório INMET ausente.");
        True(
            !Directory.EnumerateFiles(directory, "*.html", SearchOption.AllDirectories).Any(),
            "A coleta INMET criou HTML sem necessidade.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void InmetRejectsImpossibleHumidity()
{
    var csv = """
REGIAO:;N
UF:;AM
ESTACAO:;MANAUS
CODIGO (WMO):;A101
Data;Hora UTC;TEMPERATURA DO AR - BULBO SECO, HORARIA (°C);UMIDADE RELATIVA DO AR, HORARIA (%);TEMPERATURA DO PONTO DE ORVALHO (°C);
2025/01/01;0000 UTC;30,0;101;26,2;
2025/01/01;0100 UTC;29,5;100;25,9;
""";

    var dataset = new InmetCsvParser().Parse(Encoding.UTF8.GetBytes(csv));
    EqualValue(2, dataset.Observations.Count);
    True(
        dataset.Observations[0].RelativeHumidityPercent is null,
        "Umidade acima de 100% foi aceita na camada derivada.");
    EqualValue(100d, dataset.Observations[1].RelativeHumidityPercent!.Value);
}

static void InmetRejectsFullDownload()
{
    var archiveBytes = CreateInmetFixture(out _);
    var handler = new RangeFixtureHandler(archiveBytes, honorRanges: false);
    using var client = new HttpClient(handler);
    var extractor = new RemoteZipRangeExtractor(client);
    Throws<InvalidDataException>(() => extractor.ExtractAsync(
            new Uri("https://fixture.local/2025.zip"),
            name => name.Contains("_A101_", StringComparison.OrdinalIgnoreCase))
        .GetAwaiter()
        .GetResult());
}

static void InmetRejectsCorruptZip()
{
    var archiveBytes = CreateInmetFixture(out _);
    for (var index = archiveBytes.Length - 22; index >= 0; index--)
    {
        if (archiveBytes[index] == 0x50 &&
            archiveBytes[index + 1] == 0x4b &&
            archiveBytes[index + 2] == 0x05 &&
            archiveBytes[index + 3] == 0x06)
        {
            archiveBytes[index] = 0;
            break;
        }
    }

    var handler = new RangeFixtureHandler(archiveBytes, honorRanges: true);
    using var client = new HttpClient(handler);
    var extractor = new RemoteZipRangeExtractor(client);
    Throws<InvalidDataException>(() => extractor.ExtractAsync(
            new Uri("https://fixture.local/2025.zip"),
            name => name.Contains("_A101_", StringComparison.OrdinalIgnoreCase))
        .GetAwaiter()
        .GetResult());
}

static byte[] CreateInmetFixture(out byte[] csvBytes)
{
    var csv = """
REGIAO:;N
UF:;AM
ESTACAO:;MANAUS
CODIGO (WMO):;A101
LATITUDE:;-3,103333
LONGITUDE:;-60,015556
ALTITUDE:;61,25
Data;Hora UTC;TEMPERATURA DO AR - BULBO SECO, HORARIA (°C);UMIDADE RELATIVA DO AR, HORARIA (%);TEMPERATURA DO PONTO DE ORVALHO (°C);
2025/01/01;0000 UTC;30,0;80;26,2;
2025/01/01;0100 UTC;;82;26,0;
2025/01/01;0200 UTC;29,0;9999;Null;
2025/01/01;0300 UTC;28,5;85;25,7;
""";
    csvBytes = Encoding.Latin1.GetBytes(csv);
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        var padding = archive.CreateEntry("padding/random.bin", CompressionLevel.NoCompression);
        using (var paddingStream = padding.Open())
        {
            var randomBytes = new byte[300_000];
            new Random(42).NextBytes(randomBytes);
            paddingStream.Write(randomBytes);
        }

        var entry = archive.CreateEntry(
            "2025/INMET_N_AM_A101_MANAUS_01-01-2025_A_31-12-2025.CSV",
            CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        entryStream.Write(csvBytes);
    }

    return stream.ToArray();
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Era esperada a exceção {typeof(TException).Name}.");
}

static void EqualClose(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Esperado {expected}; obtido {actual}.");
    }
}

static void EqualValue<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Esperado {expected}; obtido {actual}.");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class RangeFixtureHandler : HttpMessageHandler
{
    private readonly byte[] _content;
    private readonly bool _honorRanges;

    public RangeFixtureHandler(byte[] content, bool honorRanges)
    {
        _content = content;
        _honorRanges = honorRanges;
    }

    public int RangeRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage();
        response.RequestMessage = request;
        response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");

        if (request.Method == HttpMethod.Head)
        {
            response.StatusCode = HttpStatusCode.OK;
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentLength = _content.Length;
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            return Task.FromResult(response);
        }

        var range = request.Headers.Range?.Ranges.SingleOrDefault();
        if (!_honorRanges || range?.From is not long start || range.To is not long end)
        {
            response.StatusCode = HttpStatusCode.OK;
            response.Content = new ByteArrayContent(_content);
            response.Content.Headers.ContentLength = _content.Length;
            return Task.FromResult(response);
        }

        RangeRequests++;
        var length = checked((int)(end - start + 1));
        var bytes = _content.AsSpan((int)start, length).ToArray();
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content = new ByteArrayContent(bytes);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _content.Length);
        response.Content.Headers.ContentLength = length;
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Task.FromResult(response);
    }
}
