namespace SoluPulse.Core.Rules;

public enum ComparisonOperator
{
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Equal,
    NotEqual,
}

public enum ConditionMode
{
    /// <summary>Toutes les conditions doivent être vraies.</summary>
    Tous,

    /// <summary>Au moins une condition doit être vraie.</summary>
    AuMoinsUn,
}

/// <summary>
/// Une condition structurée : métrique, opérateur, seuil, hystérésis. Un fichier de règles ne peut
/// donc jamais contenir de code exécutable (cahier des charges §6.6).
/// </summary>
public sealed record RuleCondition(string Metric, ComparisonOperator Operator, double Threshold, double Hysteresis)
{
    public bool IsBreached(double value, double? thresholdOverride = null) =>
        Compare(value, thresholdOverride ?? Threshold);

    /// <summary>
    /// La condition n'est levée qu'une fois la valeur revenue au-delà du seuil décalé de l'hystérésis,
    /// pour éviter les alertes qui clignotent (EF-DIA-06).
    /// </summary>
    public bool IsReleased(double value, double? thresholdOverride = null)
    {
        var threshold = thresholdOverride ?? Threshold;
        var releaseThreshold = Operator switch
        {
            ComparisonOperator.LessThan or ComparisonOperator.LessThanOrEqual => threshold + Hysteresis,
            ComparisonOperator.GreaterThan or ComparisonOperator.GreaterThanOrEqual => threshold - Hysteresis,
            _ => threshold,
        };
        return !Compare(value, releaseThreshold);
    }

    private bool Compare(double value, double threshold) => Operator switch
    {
        ComparisonOperator.LessThan => value < threshold,
        ComparisonOperator.LessThanOrEqual => value <= threshold,
        ComparisonOperator.GreaterThan => value > threshold,
        ComparisonOperator.GreaterThanOrEqual => value >= threshold,
        ComparisonOperator.Equal => value.Equals(threshold),
        ComparisonOperator.NotEqual => !value.Equals(threshold),
        _ => throw new InvalidOperationException($"Opérateur inconnu : {Operator}"),
    };

    public static string Symbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.Equal => "==",
        ComparisonOperator.NotEqual => "!=",
        _ => "?",
    };
}

/// <summary>Une règle de diagnostic chargée depuis un fichier YAML (EF-DIA-01, EF-DIA-02).</summary>
public sealed record Rule(
    string Id,
    int Version,
    HealthDomain Domain,
    string Title,
    Severity Severity,
    ConditionMode Mode,
    IReadOnlyList<RuleCondition> Conditions,
    TimeSpan MinDuration,
    string ProbableCause,
    string SimpleExplanation,
    string? TechnicalExplanation,
    IReadOnlyList<string> Corrections,
    string SourceFile)
{
    public RuleCondition PrimaryCondition => Conditions[0];
}
