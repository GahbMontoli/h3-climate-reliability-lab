using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace H3Monitor.Core;

public sealed class H3ReportExporter
{
    public Task ExportAsync(
        string outputDirectory,
        IReadOnlyList<H3Sample> samples,
        H3Statistics statistics,
        CancellationToken cancellationToken = default) =>
        ExportAsync(
            outputDirectory,
            samples,
            statistics,
            H3BiExportContext.CreateSynthetic(
                $"simulation-{statistics.Start.ToUniversalTime():yyyyMMddTHHmmssZ}",
                "h3-synthetic-generator-v1"),
            cancellationToken);

    public async Task ExportAsync(
        string outputDirectory,
        IReadOnlyList<H3Sample> samples,
        H3Statistics statistics,
        H3BiExportContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();

        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "h3-synthetic.csv"),
            BuildCsv(samples, statistics.IntervalMinutes, context),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "h3-summary.json"),
            JsonSerializer.Serialize(statistics, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }),
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "h3-dashboard.html"),
            BuildHtml(samples, statistics),
            Encoding.UTF8,
            cancellationToken);

        await H3BiManifestWriter.WriteAsync(
            Path.Combine(outputDirectory, "h3-bi-manifest.json"),
            context,
            [new H3BiTableManifest(
                "h3-synthetic.csv",
                samples.Count,
                "uma observação sintética por execução, cenário e timestamp")],
            cancellationToken);
    }

    private static string BuildCsv(
        IReadOnlyList<H3Sample> samples,
        double intervalMinutes,
        H3BiExportContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "timestamp;outdoor_temperature_c;outdoor_rh_percent;indoor_temperature_c;" +
            "indoor_rh_percent;surface_temperature_c;dew_point_c;dew_point_margin_c;" +
            "risk_level;door_open;hvac_active;scenario_tag;is_synthetic;" +
            "schema_version;project_id;run_id;source_id;evidence_class;quality_status;" +
            "observed_at_utc;observed_time_basis;ingested_at_utc;interval_minutes");

        foreach (var sample in samples)
        {
            builder.Append(sample.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append(Number(sample.OutdoorTemperatureC)).Append(';')
                .Append(Number(sample.OutdoorRelativeHumidityPercent)).Append(';')
                .Append(Number(sample.IndoorTemperatureC)).Append(';')
                .Append(Number(sample.IndoorRelativeHumidityPercent)).Append(';')
                .Append(Number(sample.SurfaceTemperatureC)).Append(';')
                .Append(Number(sample.DewPointC)).Append(';')
                .Append(Number(sample.DewPointMarginC)).Append(';')
                .Append(sample.RiskLevel).Append(';')
                .Append(sample.DoorOpen.ToString().ToLowerInvariant()).Append(';')
                .Append(sample.HvacActive.ToString().ToLowerInvariant()).Append(';')
                .Append(sample.ScenarioTag).Append(';')
                .Append(sample.IsSynthetic.ToString().ToLowerInvariant()).Append(';')
                .Append(context.SchemaVersion).Append(';')
                .Append(context.ProjectId).Append(';')
                .Append(context.RunId).Append(';')
                .Append(context.SourceId).Append(';')
                .Append(context.EvidenceClass).Append(';')
                .Append(context.QualityStatus).Append(';')
                .Append(sample.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append("simulation_timestamp_utc;")
                .Append(context.ExportedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .AppendLine(Number(intervalMinutes));
        }

        return builder.ToString();
    }

    private static string BuildHtml(
        IReadOnlyList<H3Sample> samples,
        H3Statistics statistics)
    {
        var chartSamples = samples.TakeLast(Math.Min(samples.Count, 432)).ToArray();
        var polyline = BuildPolyline(chartSamples);
        var bars = string.Join("", statistics.Distribution.Select(item =>
            $"<div class='bar-row'><span>{Encode(RiskLabel(item.Level))}</span>" +
            $"<div class='bar'><i style='width:{item.Percent.ToString("0.###", CultureInfo.InvariantCulture)}%'></i></div>" +
            $"<strong>{item.Percent:0.0}%</strong></div>"));

        return $$$"""
<!doctype html>
<html lang="pt-BR">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>H3 Monitor - Dashboard sintético</title>
  <style>
    :root{color-scheme:dark;--bg:#07111f;--panel:#0e1c2e;--line:#20344e;--text:#e8f1fb;--muted:#9db0c8;--cyan:#37d4d9;--orange:#ff9f43}
    *{box-sizing:border-box} body{margin:0;background:linear-gradient(135deg,#07111f,#0a1728);font:15px system-ui;color:var(--text)}
    main{max-width:1180px;margin:auto;padding:32px 20px 48px} h1{margin:0 0 6px;font-size:34px} .subtitle{color:var(--muted);margin-bottom:26px}
    .warning{border:1px solid #8a5d23;background:#2d2112;color:#ffd89a;padding:14px 16px;border-radius:12px;margin-bottom:22px}
    .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px}.card,.panel{background:rgba(14,28,46,.92);border:1px solid var(--line);border-radius:14px;padding:18px}
    .card span{display:block;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.08em}.card strong{font-size:28px;display:block;margin-top:8px}
    .grid{display:grid;grid-template-columns:2fr 1fr;gap:16px;margin-top:16px}.panel h2{font-size:17px;margin:0 0 16px}.chart{width:100%;height:280px;background:#091625;border-radius:10px}
    .grid-line{stroke:#29415f;stroke-width:1}.zero{stroke:#ff6b6b;stroke-width:2;stroke-dasharray:6 5}.series{fill:none;stroke:var(--cyan);stroke-width:2}
    .bar-row{display:grid;grid-template-columns:90px 1fr 52px;gap:8px;align-items:center;margin:11px 0}.bar{height:10px;background:#142943;border-radius:8px;overflow:hidden}.bar i{display:block;height:100%;background:linear-gradient(90deg,var(--cyan),var(--orange))}
    footer{color:var(--muted);margin-top:20px;font-size:13px}@media(max-width:800px){.grid{grid-template-columns:1fr}}
  </style>
</head>
<body><main>
  <h1>H3 Monitor</h1>
  <div class="subtitle">Laboratório digital de risco higrotérmico</div>
  <div class="warning"><strong>DADOS SINTÉTICOS:</strong> este relatório demonstra o software; não representa medições ou evidência de campo.</div>
  <section class="cards">
    <div class="card"><span>Amostras</span><strong>{{{statistics.TotalSamples:N0}}}</strong></div>
    <div class="card"><span>Margem mínima</span><strong>{{{statistics.MinimumDewPointMarginC:0.00}}} °C</strong></div>
    <div class="card"><span>Exposição H3</span><strong>{{{statistics.H3ExposureHours:0.0}}} h</strong></div>
    <div class="card"><span>Eventos de risco</span><strong>{{{statistics.HighRiskEvents}}}</strong></div>
    <div class="card"><span>Maior evento</span><strong>{{{statistics.LongestHighRiskEventHours:0.0}}} h</strong></div>
    <div class="card"><span>Correlação RH externa/margem</span><strong>{{{statistics.OutdoorHumidityMarginCorrelation:0.000}}}</strong></div>
  </section>
  <section class="grid">
    <div class="panel"><h2>Margem para o ponto de orvalho - últimas {{{chartSamples.Length}}} amostras</h2>
      <svg class="chart" viewBox="0 0 900 280" preserveAspectRatio="none" role="img" aria-label="Margem para o ponto de orvalho">
        <line class="grid-line" x1="0" y1="70" x2="900" y2="70"/><line class="grid-line" x1="0" y1="140" x2="900" y2="140"/><line class="grid-line" x1="0" y1="210" x2="900" y2="210"/>
        <line class="zero" x1="0" y1="224" x2="900" y2="224"/><polyline class="series" points="{{{polyline}}}"/>
      </svg>
    </div>
    <div class="panel"><h2>Distribuição de risco</h2>{{{bars}}}</div>
  </section>
  <footer>Período simulado: {{{statistics.Start:dd/MM/yyyy HH:mm}}} a {{{statistics.End:dd/MM/yyyy HH:mm}}}. Limites experimentais; validar antes de qualquer uso operacional.</footer>
</main></body></html>
""";
    }

    private static string BuildPolyline(IReadOnlyList<H3Sample> samples)
    {
        if (samples.Count == 0)
        {
            return string.Empty;
        }

        const double minimum = -4;
        const double maximum = 16;
        return string.Join(" ", samples.Select((sample, index) =>
        {
            var x = samples.Count == 1 ? 0 : index * 900.0 / (samples.Count - 1);
            var clamped = Math.Clamp(sample.DewPointMarginC, minimum, maximum);
            var y = 280 - (clamped - minimum) * 280 / (maximum - minimum);
            return $"{x.ToString("0.##", CultureInfo.InvariantCulture)},{y.ToString("0.##", CultureInfo.InvariantCulture)}";
        }));
    }

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string RiskLabel(H3RiskLevel level) => level switch
    {
        H3RiskLevel.Normal => "Normal",
        H3RiskLevel.Attention => "Atenção",
        H3RiskLevel.High => "Elevado",
        H3RiskLevel.Critical => "Crítico",
        H3RiskLevel.Condensation => "Condensação",
        _ => level.ToString()
    };

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
