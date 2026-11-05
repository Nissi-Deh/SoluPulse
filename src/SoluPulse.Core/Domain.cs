namespace SoluPulse.Core;

/// <summary>Les quatre domaines surveillés (cahier des charges, objectif 1).</summary>
public enum HealthDomain
{
    Materiel,
    Systeme,
    Securite,
    Reseau,
}

/// <summary>Niveaux de gravité d'une alerte (EF-DIA-03).</summary>
public enum Severity
{
    Information = 0,
    Attention = 1,
    Critique = 2,
}

public enum AlertState
{
    Active = 0,
    Resolue = 1,
}

/// <summary>Une valeur horodatée relevée par un collecteur.</summary>
public sealed record Measurement(DateTimeOffset Timestamp, string Metric, double Value, string Unit, string Source);

/// <summary>Un capteur absent ou illisible : la collecte continue et le problème est signalé (EF-COL-12).</summary>
public sealed record SensorIssue(string Collector, string Sensor, string Message, DateTimeOffset Timestamp);

/// <summary>Inventaire sérialisé en JSON (programmes au démarrage, services, processus…).</summary>
public sealed record InventorySnapshot(string Kind, DateTimeOffset Timestamp, string Json);

public sealed record Alert(
    Guid Id,
    string RuleId,
    string Subject,
    HealthDomain Domain,
    Severity Severity,
    AlertState State,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt,
    double? TriggerValue);
