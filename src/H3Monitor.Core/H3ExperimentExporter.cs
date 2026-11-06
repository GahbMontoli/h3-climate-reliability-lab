using System.Globalization;
using System.Text;
using System.Text.Json;

namespace H3Monitor.Core;

public sealed class H3ExperimentExporter
{
    public Task ExportAsync(
        string outputDirectory,
        H3ExperimentResult result,
        CancellationToken cancellationToken = default) =>
        ExportAsync(
            outputDirectory,
            result,
            H3BiExportContext.CreateSynthetic(
                $"experiment-{result.Options.Seed}-{result.Options.Replications}r",
                "h3-paired-experiment-v1"),
            cancellationToken);

    public async Task ExportAsync(
        string outputDirectory,
        H3ExperimentResult result,
        H3BiExportContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();
        Directory.CreateDirectory(outputDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "h3-experiment-observations.csv"),
            BuildCsv(result, context),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "h3-experiment-summary.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }),
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "RELATORIO_PESQUISA_H3.md"),
            BuildMarkdown(result),
            Encoding.UTF8,
            cancellationToken);

        await H3BiManifestWriter.WriteAsync(
            Path.Combine(outputDirectory, "h3-experiment-bi-manifest.json"),
            context,
            [new H3BiTableManifest(
                "h3-experiment-observations.csv",
                result.Observations.Count,
                "uma replicação sintética pareada por intervenção")],
            cancellationToken);
    }

    private static string BuildCsv(
        H3ExperimentResult result,
        H3BiExportContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "replication;seed;intervention_id;baseline_exposure_hours;" +
            "intervention_exposure_hours;exposure_hours_avoided;" +
            "baseline_high_risk_events;intervention_high_risk_events;" +
            "high_risk_events_avoided;baseline_minimum_margin_c;" +
            "intervention_minimum_margin_c;minimum_margin_gain_c;is_synthetic;" +
            "schema_version;project_id;run_id;source_id;evidence_class;quality_status;" +
            "observed_at_utc;observed_time_basis;ingested_at_utc;interval_minutes;experiment_days");

        foreach (var observation in result.Observations)
        {
            builder.Append(observation.Replication).Append(';')
                .Append(observation.Seed).Append(';')
                .Append(observation.InterventionId).Append(';')
                .Append(Number(observation.BaselineExposureHours)).Append(';')
                .Append(Number(observation.InterventionExposureHours)).Append(';')
                .Append(Number(observation.ExposureHoursAvoided)).Append(';')
                .Append(observation.BaselineHighRiskEvents).Append(';')
                .Append(observation.InterventionHighRiskEvents).Append(';')
                .Append(observation.HighRiskEventsAvoided).Append(';')
                .Append(Number(observation.BaselineMinimumMarginC)).Append(';')
                .Append(Number(observation.InterventionMinimumMarginC)).Append(';')
                .Append(Number(observation.MinimumMarginGainC)).Append(';')
                .Append("true;")
                .Append(context.SchemaVersion).Append(';')
                .Append(context.ProjectId).Append(';')
                .Append(context.RunId).Append(';')
                .Append(context.SourceId).Append(';')
                .Append(context.EvidenceClass).Append(';')
                .Append(context.QualityStatus).Append(';')
                .Append(context.ExportedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append("package_exported_at_utc_proxy;")
                .Append(context.ExportedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(';')
                .Append(result.Options.IntervalMinutes).Append(';')
                .AppendLine(result.Options.Days.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(H3ExperimentResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Experimento programacional H3")
            .AppendLine()
            .AppendLine("> **DADOS SINTÉTICOS.** Este experimento testa a coerência da hipótese dentro do modelo; não é validação de campo nem prova causal.")
            .AppendLine()
            .AppendLine("## Desenho")
            .AppendLine()
            .AppendLine($"- Replicações pareadas: {result.Options.Replications}")
            .AppendLine($"- Duração por replicação: {result.Options.Days} dias")
            .AppendLine($"- Intervalo: {result.Options.IntervalMinutes} minutos")
            .AppendLine($"- Semente mestre: {result.Options.Seed}")
            .AppendLine("- Comparação: mesma realização climática para referência e intervenção.")
            .AppendLine($"- Incerteza: {result.ConfidenceIntervalMethod}")
            .AppendLine()
            .AppendLine("## Resultados do modelo")
            .AppendLine()
            .AppendLine("| Intervenção | Exposição referência (h) | Exposição com controle (h) | Horas evitadas (IC95%) | Redução | Prob. melhora | Ganho margem mínima |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (var effect in result.Effects)
        {
            builder.Append("| ").Append(effect.InterventionId)
                .Append(" | ").Append(Number(effect.AverageBaselineExposureHours))
                .Append(" | ").Append(Number(effect.AverageInterventionExposureHours))
                .Append(" | ").Append(Number(effect.AverageExposureHoursAvoided))
                .Append(" [").Append(Number(effect.ExposureHoursAvoidedCi95Low))
                .Append("; ").Append(Number(effect.ExposureHoursAvoidedCi95High)).Append(']')
                .Append(" | ").Append(Number(effect.AverageExposureReductionPercent)).Append("%")
                .Append(" | ").Append(Number(effect.ImprovementProbabilityPercent)).Append("%")
                .Append(" | ").Append(Number(effect.AverageMinimumMarginGainC)).AppendLine(" °C |");
        }

        builder.AppendLine()
            .AppendLine("## Interpretação responsável")
            .AppendLine()
            .AppendLine("Uma diferença positiva indica que a intervenção reduziu exposição dentro da simulação. O resultado serve para priorizar quais variáveis medir e testar em bancada.")
            .AppendLine()
            .AppendLine("- Se o IC95% inclui zero, o experimento não distinguiu redução de exposição para aquela intervenção.")
            .AppendLine("- Uma redução de 100% pode indicar saturação dos limiares do modelo; não deve ser projetada como resultado de campo.")
            .AppendLine()
            .AppendLine("O próximo passo para comprovação é substituir as entradas sintéticas por séries rastreáveis de estação meteorológica e sensores calibrados, mantendo o mesmo protocolo de comparação.")
            .AppendLine()
            .AppendLine($"**Limite de evidência:** {result.EvidenceLimit}");

        return builder.ToString();
    }

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
