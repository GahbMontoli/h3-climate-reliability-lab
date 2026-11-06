namespace H3Monitor.Core;

public sealed record H3ExperimentOptions(
    int Replications = 60,
    int Days = 30,
    int IntervalMinutes = 30,
    int Seed = 20261029);

public sealed record H3InterventionDefinition(
    string Id,
    string Description,
    SyntheticScenarioProfile Profile);

public sealed record H3ExperimentObservation(
    int Replication,
    int Seed,
    string InterventionId,
    double BaselineExposureHours,
    double InterventionExposureHours,
    double ExposureHoursAvoided,
    int BaselineHighRiskEvents,
    int InterventionHighRiskEvents,
    int HighRiskEventsAvoided,
    double BaselineMinimumMarginC,
    double InterventionMinimumMarginC,
    double MinimumMarginGainC);

public sealed record H3ExperimentEffect(
    string InterventionId,
    string Description,
    int Replications,
    double AverageBaselineExposureHours,
    double AverageInterventionExposureHours,
    double AverageExposureHoursAvoided,
    double ExposureHoursAvoidedCi95Low,
    double ExposureHoursAvoidedCi95High,
    double AverageExposureReductionPercent,
    double AverageHighRiskEventsAvoided,
    double AverageMinimumMarginGainC,
    double ImprovementProbabilityPercent);

public sealed record H3ExperimentResult(
    H3ExperimentOptions Options,
    IReadOnlyList<H3ExperimentEffect> Effects,
    IReadOnlyList<H3ExperimentObservation> Observations,
    bool SyntheticData = true,
    string ConfidenceIntervalMethod =
        "Aproximação normal de 95% sobre diferenças pareadas (média ± 1,96 × erro-padrão).",
    string EvidenceLimit =
        "Resultados validam apenas o comportamento interno do modelo sintético; não comprovam efeito causal em equipamentos reais.",
    int SchemaVersion = 1,
    string EvidenceClass = "synthetic");

public sealed class H3ExperimentRunner
{
    private static readonly IReadOnlyList<H3InterventionDefinition> Interventions =
    [
        new(
            "humidity-control",
            "Redução de 4 pontos percentuais no setpoint sintético de umidade interna.",
            new SyntheticScenarioProfile(
                "humidity-control",
                IndoorHumiditySetpointPercent: 52.0)),
        new(
            "thermal-margin",
            "Aumento sintético de 1 °C na temperatura superficial de referência.",
            new SyntheticScenarioProfile(
                "thermal-margin",
                SurfaceTemperatureBaseC: 21.6)),
        new(
            "air-ingress-control",
            "Redução da fração sintética de ingresso de umidade de 0,60 para 0,30.",
            new SyntheticScenarioProfile(
                "air-ingress-control",
                DoorHumidityIngressFraction: 0.30)),
        new(
            "hvac-reliability",
            "Remoção das interrupções semanais programadas do HVAC no modelo.",
            new SyntheticScenarioProfile(
                "hvac-reliability",
                EnableScheduledHvacOutages: false)),
        new(
            "combined-control",
            "Combinação das quatro intervenções sintéticas.",
            new SyntheticScenarioProfile(
                "combined-control",
                IndoorHumiditySetpointPercent: 52.0,
                SurfaceTemperatureBaseC: 21.6,
                DoorHumidityIngressFraction: 0.30,
                EnableScheduledHvacOutages: false))
    ];

    public H3ExperimentResult Run(H3ExperimentOptions options)
    {
        Validate(options);

        var generator = new SyntheticScenarioGenerator();
        var analyzer = new H3StatisticsAnalyzer();
        var observations = new List<H3ExperimentObservation>(
            options.Replications * Interventions.Count);

        for (var replication = 0; replication < options.Replications; replication++)
        {
            var seed = unchecked(options.Seed + replication * 7_919);
            var baseline = analyzer.Analyze(generator.Generate(
                CreateScenarioOptions(options, seed, SyntheticScenarioProfile.Baseline)));

            foreach (var intervention in Interventions)
            {
                var controlled = analyzer.Analyze(generator.Generate(
                    CreateScenarioOptions(options, seed, intervention.Profile)));

                observations.Add(new H3ExperimentObservation(
                    replication + 1,
                    seed,
                    intervention.Id,
                    baseline.H3ExposureHours,
                    controlled.H3ExposureHours,
                    Math.Round(baseline.H3ExposureHours - controlled.H3ExposureHours, 3),
                    baseline.HighRiskEvents,
                    controlled.HighRiskEvents,
                    baseline.HighRiskEvents - controlled.HighRiskEvents,
                    baseline.MinimumDewPointMarginC,
                    controlled.MinimumDewPointMarginC,
                    Math.Round(
                        controlled.MinimumDewPointMarginC - baseline.MinimumDewPointMarginC,
                        3)));
            }
        }

        var effects = Interventions
            .Select(intervention => Summarize(
                intervention,
                observations.Where(item => item.InterventionId == intervention.Id).ToArray()))
            .ToArray();

        return new H3ExperimentResult(options, effects, observations);
    }

    private static SyntheticScenarioOptions CreateScenarioOptions(
        H3ExperimentOptions options,
        int seed,
        SyntheticScenarioProfile profile) =>
        new(
            options.Days,
            options.IntervalMinutes,
            seed,
            Profile: profile);

    private static H3ExperimentEffect Summarize(
        H3InterventionDefinition intervention,
        IReadOnlyList<H3ExperimentObservation> observations)
    {
        var differences = observations
            .Select(item => item.ExposureHoursAvoided)
            .ToArray();
        var averageDifference = differences.Average();
        var standardDeviation = SampleStandardDeviation(differences, averageDifference);
        var margin = 1.96 * standardDeviation / Math.Sqrt(differences.Length);
        var baselineAverage = observations.Average(item => item.BaselineExposureHours);
        var interventionAverage = observations.Average(item => item.InterventionExposureHours);

        return new H3ExperimentEffect(
            intervention.Id,
            intervention.Description,
            observations.Count,
            Round(baselineAverage),
            Round(interventionAverage),
            Round(averageDifference),
            Round(averageDifference - margin),
            Round(averageDifference + margin),
            baselineAverage == 0
                ? 0
                : Round(averageDifference * 100.0 / baselineAverage),
            Round(observations.Average(item => item.HighRiskEventsAvoided)),
            Round(observations.Average(item => item.MinimumMarginGainC)),
            Round(observations.Count(item => item.ExposureHoursAvoided > 0) *
                  100.0 / observations.Count));
    }

    private static double SampleStandardDeviation(
        IReadOnlyList<double> values,
        double average)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var squaredDifferences = values.Sum(value => Math.Pow(value - average, 2));
        return Math.Sqrt(squaredDifferences / (values.Count - 1));
    }

    private static void Validate(H3ExperimentOptions options)
    {
        if (options.Replications is < 2 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Replications));
        }

        if (options.Days is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Days));
        }

        if (options.IntervalMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(options.IntervalMinutes));
        }
    }

    private static double Round(double value) => Math.Round(value, 3);
}
