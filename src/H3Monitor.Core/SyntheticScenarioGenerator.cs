namespace H3Monitor.Core;

public sealed record SyntheticScenarioOptions(
    int Days = 90,
    int IntervalMinutes = 10,
    int Seed = 20261029,
    DateTimeOffset? Start = null,
    SyntheticScenarioProfile? Profile = null);

public sealed record SyntheticScenarioProfile(
    string Name,
    double IndoorHumiditySetpointPercent = 56.0,
    double SurfaceTemperatureBaseC = 20.6,
    double DoorHumidityIngressFraction = 0.60,
    bool EnableScheduledHvacOutages = true)
{
    public static SyntheticScenarioProfile Baseline { get; } = new("baseline");
}

public sealed class SyntheticScenarioGenerator
{
    public IReadOnlyList<H3Sample> Generate(SyntheticScenarioOptions options)
    {
        if (options.Days is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Days));
        }

        if (options.IntervalMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(options.IntervalMinutes));
        }

        var profile = options.Profile ?? SyntheticScenarioProfile.Baseline;
        ValidateProfile(profile);

        var random = new Random(options.Seed);
        var start = options.Start ??
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-4));
        var sampleCount = options.Days * 24 * 60 / options.IntervalMinutes;
        var samples = new List<H3Sample>(sampleCount);

        for (var index = 0; index < sampleCount; index++)
        {
            var timestamp = start.AddMinutes(index * options.IntervalMinutes);
            var hour = timestamp.TimeOfDay.TotalHours;
            var day = (int)(timestamp - start).TotalDays;
            var heatCycle = Math.Sin((hour - 8) * 2 * Math.PI / 24);

            var outdoorTemperature = 29.0 + 4.2 * heatCycle + Noise(random, 0.55);
            var outdoorHumidity = Clamp(82.0 - 12.0 * heatCycle + Noise(random, 2.4), 58, 99);

            var scheduledDoorOpening =
                IsBetween(hour, 8.0, 8.5) ||
                IsBetween(hour, 13.0, 13.35) ||
                IsBetween(hour, 18.0, 18.2);
            var doorOpen = scheduledDoorOpening || random.NextDouble() < 0.004;

            var scheduledHvacOutage = profile.EnableScheduledHvacOutages &&
                                      day % 7 == 3 &&
                                      IsBetween(hour, 14.0, 16.0);
            var hvacActive = !scheduledHvacOutage;

            var indoorTemperature = hvacActive
                ? 23.4 + 0.12 * (outdoorTemperature - 29.0) + Noise(random, 0.2)
                : outdoorTemperature - 1.4 + Noise(random, 0.35);

            var indoorHumidity = hvacActive
                ? profile.IndoorHumiditySetpointPercent +
                  0.12 * (outdoorHumidity - 70.0) +
                  Noise(random, 1.2)
                : outdoorHumidity - 2.0 + Noise(random, 1.3);

            if (doorOpen)
            {
                indoorHumidity += profile.DoorHumidityIngressFraction *
                                  (outdoorHumidity - indoorHumidity);
                indoorTemperature += 0.25 * (outdoorTemperature - indoorTemperature);
            }

            indoorHumidity = Clamp(indoorHumidity, 35, 100);
            var surfaceTemperature = hvacActive
                ? profile.SurfaceTemperatureBaseC +
                  0.08 * (indoorTemperature - 23.4) +
                  Noise(random, 0.18)
                : indoorTemperature - 0.6 + Noise(random, 0.25);

            var dewPoint = DewPointCalculator.CalculateCelsius(
                indoorTemperature,
                indoorHumidity);
            var margin = surfaceTemperature - dewPoint;
            var risk = H3RiskClassifier.Classify(margin);
            var scenarioTag = scheduledHvacOutage
                ? "hvac-outage"
                : doorOpen
                    ? "humid-air-ingress"
                    : "baseline";

            samples.Add(new H3Sample(
                timestamp,
                Round(outdoorTemperature),
                Round(outdoorHumidity),
                Round(indoorTemperature),
                Round(indoorHumidity),
                Round(surfaceTemperature),
                Round(dewPoint),
                Round(margin),
                risk,
                doorOpen,
                hvacActive,
                scenarioTag));
        }

        return samples;
    }

    private static bool IsBetween(double value, double start, double end) =>
        value >= start && value < end;

    private static double Noise(Random random, double amplitude) =>
        (random.NextDouble() * 2 - 1) * amplitude;

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Clamp(value, minimum, maximum);

    private static double Round(double value) => Math.Round(value, 3);

    private static void ValidateProfile(SyntheticScenarioProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        if (profile.IndoorHumiditySetpointPercent is < 35 or > 95)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (profile.SurfaceTemperatureBaseC is < 0 or > 80)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (profile.DoorHumidityIngressFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }
}
