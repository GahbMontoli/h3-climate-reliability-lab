using System.Globalization;
using H3Monitor.Core;

try
{
    var options = ParseArguments(args);
    var exportContext = H3BiExportContext.CreateSynthetic(
        options.RunId ?? CreateRunId(options.Mode),
        string.Equals(options.Mode, "experiment", StringComparison.OrdinalIgnoreCase)
            ? "h3-paired-experiment-v1"
            : "h3-synthetic-generator-v1",
        options.ExportedAtUtc);
    if (string.Equals(options.Mode, "experiment", StringComparison.OrdinalIgnoreCase))
    {
        var result = new H3ExperimentRunner().Run(new H3ExperimentOptions(
            options.Replications,
            options.Days,
            options.IntervalMinutes,
            options.Seed));
        await new H3ExperimentExporter().ExportAsync(
            options.OutputDirectory,
            result,
            exportContext);

        Console.WriteLine("H3 Monitor - experimento pareado concluído");
        Console.WriteLine($"Replicações sintéticas: {result.Options.Replications:N0}");
        foreach (var effect in result.Effects)
        {
            Console.WriteLine(
                $"{effect.InterventionId}: {effect.AverageExposureHoursAvoided:0.00} h " +
                $"evitadas (IC95% {effect.ExposureHoursAvoidedCi95Low:0.00} a " +
                $"{effect.ExposureHoursAvoidedCi95High:0.00})");
        }
    }
    else if (string.Equals(options.Mode, "simulation", StringComparison.OrdinalIgnoreCase))
    {
        var generator = new SyntheticScenarioGenerator();
        var samples = generator.Generate(new SyntheticScenarioOptions(
            options.Days,
            options.IntervalMinutes,
            options.Seed));
        var statistics = new H3StatisticsAnalyzer().Analyze(samples);
        await new H3ReportExporter().ExportAsync(
            options.OutputDirectory,
            samples,
            statistics,
            exportContext);

        Console.WriteLine("H3 Monitor - simulação concluída");
        Console.WriteLine($"Amostras sintéticas: {statistics.TotalSamples:N0}");
        Console.WriteLine($"Margem mínima: {statistics.MinimumDewPointMarginC:0.00} °C");
        Console.WriteLine($"Exposição H3: {statistics.H3ExposureHours:0.0} h");
        Console.WriteLine($"Eventos de risco: {statistics.HighRiskEvents}");
    }
    else if (string.Equals(options.Mode, "inmet", StringComparison.OrdinalIgnoreCase))
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("H3Monitor/0.2");
        var service = new InmetStudyService(new RemoteZipRangeExtractor(httpClient));
        var summary = await service.CollectAsync(
            options.Year,
            options.StationCode,
            options.OutputDirectory);

        Console.WriteLine("H3 Monitor - coleta INMET por HTTP Range concluída");
        Console.WriteLine($"Estação/ano: {summary.StationCode}/{summary.Year}");
        Console.WriteLine($"Linhas: {summary.Coverage.DataRows:N0}");
        Console.WriteLine($"Cobertura horária: {summary.Coverage.CoveragePercent:0.00}%");
        Console.WriteLine(
            $"Payload: {summary.Provenance.BytesTransferred:N0} de " +
            $"{summary.Provenance.ArchiveBytes:N0} bytes");
    }
    else
    {
        throw new ArgumentException("Use --mode simulation, --mode experiment ou --mode inmet.");
    }

    Console.WriteLine($"Saída: {Path.GetFullPath(options.OutputDirectory)}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Falha: {exception.Message}");
    return 1;
}

static CliOptions ParseArguments(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--"))
        {
            throw new ArgumentException("Use pares no formato --opção valor.");
        }

        values[arguments[index][2..]] = arguments[index + 1];
    }

    var mode = values.GetValueOrDefault("mode", "simulation");
    var defaultOutput = string.Equals(mode, "inmet", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine("output", "inmet")
        : Path.Combine("output", "demo");
    return new CliOptions(
        mode,
        ReadInt(values, "days", 90, 1, 3650),
        ReadInt(values, "interval-minutes", 10, 1, 1440),
        ReadInt(values, "seed", 20261029, int.MinValue, int.MaxValue),
        ReadInt(values, "replications", 60, 2, 10_000),
        ReadInt(values, "year", 2025, 2000, 2100),
        values.GetValueOrDefault("station", "A101"),
        values.GetValueOrDefault("output", defaultOutput),
        values.GetValueOrDefault("run-id"),
        ReadUtcTimestamp(values, "exported-at-utc"));
}

static int ReadInt(
    IReadOnlyDictionary<string, string> values,
    string key,
    int fallback,
    int minimum,
    int maximum)
{
    if (!values.TryGetValue(key, out var raw))
    {
        return fallback;
    }

    if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
    {
        throw new ArgumentOutOfRangeException(key, $"Valor inválido para --{key}.");
    }

    return value;
}

static DateTimeOffset? ReadUtcTimestamp(
    IReadOnlyDictionary<string, string> values,
    string key)
{
    if (!values.TryGetValue(key, out var raw))
    {
        return null;
    }

    if (!DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value))
    {
        throw new ArgumentException($"Timestamp inválido para --{key}.", key);
    }

    return value.ToUniversalTime();
}

static string CreateRunId(string mode) =>
    $"{mode.ToLowerInvariant()}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";

internal sealed record CliOptions(
    string Mode,
    int Days,
    int IntervalMinutes,
    int Seed,
    int Replications,
    int Year,
    string StationCode,
    string OutputDirectory,
    string? RunId,
    DateTimeOffset? ExportedAtUtc);
