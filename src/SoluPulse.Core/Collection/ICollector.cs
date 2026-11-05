namespace SoluPulse.Core.Collection;

/// <summary>
/// Un collecteur indépendant (ENF-MAIN-01). Il ne doit jamais lever d'exception pour un capteur
/// absent : il le signale dans <see cref="CollectorResult.Issues"/> (EF-COL-12).
/// </summary>
public interface ICollector
{
    string Name { get; }

    /// <summary>Fréquence propre au collecteur (EF-COL-11).</summary>
    TimeSpan Interval { get; }

    Task<CollectorResult> CollectAsync(CancellationToken cancellationToken);
}

public sealed record CollectorResult(
    IReadOnlyList<Measurement> Measurements,
    IReadOnlyList<SensorIssue> Issues,
    IReadOnlyList<InventorySnapshot> Snapshots)
{
    public static CollectorResult Empty { get; } = new([], [], []);
}
