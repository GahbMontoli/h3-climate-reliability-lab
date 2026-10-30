namespace H3Monitor.Core;

public enum H3RiskLevel
{
    Normal = 0,
    Attention = 1,
    High = 2,
    Critical = 3,
    Condensation = 4
}

public sealed record H3Sample(
    DateTimeOffset Timestamp,
    double OutdoorTemperatureC,
    double OutdoorRelativeHumidityPercent,
    double IndoorTemperatureC,
    double IndoorRelativeHumidityPercent,
    double SurfaceTemperatureC,
    double DewPointC,
    double DewPointMarginC,
    H3RiskLevel RiskLevel,
    bool DoorOpen,
    bool HvacActive,
    string ScenarioTag,
    bool IsSynthetic = true);
