namespace H3Monitor.Core;

public sealed record H3RiskDistribution(
    H3RiskLevel Level,
    int Samples,
    double Percent);

public sealed record H3Statistics(
    int TotalSamples,
    DateTimeOffset Start,
    DateTimeOffset End,
    double IntervalMinutes,
    double AverageDewPointMarginC,
    double MinimumDewPointMarginC,
    double MaximumDewPointMarginC,
    double H3ExposureHours,
    double CriticalExposureHours,
    int HighRiskEvents,
    double LongestHighRiskEventHours,
    double OutdoorHumidityMarginCorrelation,
    IReadOnlyList<H3RiskDistribution> Distribution,
    bool SyntheticData = true,
    int SchemaVersion = 1,
    string EvidenceClass = "synthetic");

public sealed class H3StatisticsAnalyzer
{
    public H3Statistics Analyze(IReadOnlyList<H3Sample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2)
        {
            throw new ArgumentException(
                "São necessárias pelo menos duas amostras.",
                nameof(samples));
        }

        var ordered = samples.OrderBy(sample => sample.Timestamp).ToArray();
        var intervalMinutes = (ordered[1].Timestamp - ordered[0].Timestamp).TotalMinutes;
        if (intervalMinutes <= 0)
        {
            throw new ArgumentException("Os timestamps devem ser crescentes.", nameof(samples));
        }

        var distribution = Enum.GetValues<H3RiskLevel>()
            .Select(level =>
            {
                var count = ordered.Count(sample => sample.RiskLevel == level);
                return new H3RiskDistribution(
                    level,
                    count,
                    Math.Round(count * 100.0 / ordered.Length, 3));
            })
            .ToArray();

        var exposureSamples = ordered.Count(sample => sample.RiskLevel >= H3RiskLevel.High);
        var criticalSamples = ordered.Count(sample => sample.RiskLevel >= H3RiskLevel.Critical);
        var eventCount = 0;
        var currentEventSamples = 0;
        var longestEventSamples = 0;

        foreach (var sample in ordered)
        {
            if (sample.RiskLevel >= H3RiskLevel.High)
            {
                if (currentEventSamples == 0)
                {
                    eventCount++;
                }

                currentEventSamples++;
                longestEventSamples = Math.Max(longestEventSamples, currentEventSamples);
            }
            else
            {
                currentEventSamples = 0;
            }
        }

        return new H3Statistics(
            ordered.Length,
            ordered[0].Timestamp,
            ordered[^1].Timestamp,
            intervalMinutes,
            Math.Round(ordered.Average(sample => sample.DewPointMarginC), 3),
            ordered.Min(sample => sample.DewPointMarginC),
            ordered.Max(sample => sample.DewPointMarginC),
            Math.Round(exposureSamples * intervalMinutes / 60.0, 3),
            Math.Round(criticalSamples * intervalMinutes / 60.0, 3),
            eventCount,
            Math.Round(longestEventSamples * intervalMinutes / 60.0, 3),
            Math.Round(PearsonCorrelation(
                ordered.Select(sample => sample.OutdoorRelativeHumidityPercent).ToArray(),
                ordered.Select(sample => sample.DewPointMarginC).ToArray()), 4),
            distribution);
    }

    private static double PearsonCorrelation(double[] first, double[] second)
    {
        var firstAverage = first.Average();
        var secondAverage = second.Average();
        var numerator = 0.0;
        var firstSquares = 0.0;
        var secondSquares = 0.0;

        for (var index = 0; index < first.Length; index++)
        {
            var firstDelta = first[index] - firstAverage;
            var secondDelta = second[index] - secondAverage;
            numerator += firstDelta * secondDelta;
            firstSquares += firstDelta * firstDelta;
            secondSquares += secondDelta * secondDelta;
        }

        var denominator = Math.Sqrt(firstSquares * secondSquares);
        return denominator == 0 ? 0 : numerator / denominator;
    }
}
