namespace SoluPulse.Core.Corrections;

public sealed record RestorePointResult(bool Success, string Message);

/// <summary>Points de restauration Windows (EF-COR-03).</summary>
public interface IRestorePointService
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken);
}

public enum VerificationResult
{
    NonApplicable = 0,
    Resolue = 1,
    Persistante = 2,
    Indeterminee = 3,
}

/// <summary>Relance la règle de l'alerte après une correction (EF-COR-06).</summary>
public interface IAlertVerifier
{
    Task<VerificationResult> VerifyAsync(Guid alertId, CancellationToken cancellationToken);
}

public enum ExecutionState
{
    EnCours = 0,
    Reussie = 1,
    Echouee = 2,
    Annulee = 3,
    Interrompue = 4,
    AnnuleeAuRedemarrage = 5,
}

public sealed record ExecutionRecord(
    Guid Id,
    string ActionId,
    Guid? AlertId,
    ExecutionOrigin Origin,
    IReadOnlyDictionary<string, string> Parameters,
    CallerIdentity ValidatedBy,
    DateTimeOffset ValidatedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    ExecutionState State,
    string? Backup,
    string? Message,
    VerificationResult Verification,
    DateTimeOffset? UndoneAt);

/// <summary>
/// Journal des exécutions, écrit avant chaque étape pour qu'une correction interrompue par une
/// coupure de courant soit reprise ou annulée au redémarrage (ENF-FIA-02).
/// </summary>
public interface IExecutionJournal
{
    Task BeginAsync(ExecutionRecord record, CancellationToken cancellationToken);

    Task SaveBackupAsync(Guid executionId, string backup, CancellationToken cancellationToken);

    Task CompleteAsync(Guid executionId, ExecutionState state, string message, VerificationResult verification, DateTimeOffset finishedAt, CancellationToken cancellationToken);

    Task MarkUndoneAsync(Guid executionId, DateTimeOffset undoneAt, string message, CancellationToken cancellationToken);

    Task<ExecutionRecord?> GetAsync(Guid executionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionRecord>> ListInProgressAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}
