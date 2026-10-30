namespace H3Monitor.Core;

public static class DewPointCalculator
{
    private const double A = 17.62;
    private const double B = 243.12;

    public static double CalculateCelsius(
        double airTemperatureC,
        double relativeHumidityPercent)
    {
        if (!double.IsFinite(airTemperatureC))
        {
            throw new ArgumentOutOfRangeException(
                nameof(airTemperatureC),
                "A temperatura deve ser um número finito.");
        }

        if (!double.IsFinite(relativeHumidityPercent) ||
            relativeHumidityPercent <= 0 ||
            relativeHumidityPercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relativeHumidityPercent),
                "A umidade relativa deve estar no intervalo (0, 100].");
        }

        var gamma = Math.Log(relativeHumidityPercent / 100.0) +
                    (A * airTemperatureC) / (B + airTemperatureC);

        return B * gamma / (A - gamma);
    }
}
