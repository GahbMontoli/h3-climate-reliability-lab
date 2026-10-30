namespace H3Monitor.Core;

public static class H3RiskClassifier
{
    public static H3RiskLevel Classify(double dewPointMarginC)
    {
        if (!double.IsFinite(dewPointMarginC))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dewPointMarginC),
                "A margem deve ser um número finito.");
        }

        return dewPointMarginC switch
        {
            <= 0 => H3RiskLevel.Condensation,
            <= 1 => H3RiskLevel.Critical,
            <= 3 => H3RiskLevel.High,
            <= 5 => H3RiskLevel.Attention,
            _ => H3RiskLevel.Normal
        };
    }
}
