using SoluPulse.Core.Audit;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Rules;

namespace SoluPulse.Core.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal static class Measurements
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    public static Dictionary<string, Measurement> Of(params (string Metric, double Value)[] values) =>
        values.ToDictionary(v => v.Metric, v => new Measurement(T0, v.Metric, v.Value, "", "test"), StringComparer.Ordinal);
}

internal static class Rules
{
    public static Rule Load(string yaml, params string[] actions)
    {
        var set = RuleLoader.LoadFiles([("test.yaml", yaml)], actions.ToHashSet(StringComparer.Ordinal));
        Assert.Empty(set.Errors);
        return Assert.Single(set.Rules);
    }
}

internal sealed class InMemoryJournal : IExecutionJournal
{
    public Dictionary<Guid, ExecutionRecord> Records { get; } = [];

    public Task BeginAsync(ExecutionRecord record, CancellationToken cancellationToken)
    {
        Records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task SaveBackupAsync(Guid executionId, string backup, CancellationToken cancellationToken)
    {
        Records[executionId] = Records[executionId] with { Backup = backup };
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Guid executionId, ExecutionState state, string message, VerificationResult verification, DateTimeOffset finishedAt, CancellationToken cancellationToken)
    {
        Records[executionId] = Records[executionId] with { State = state, Message = message, Verification = verification, FinishedAt = finishedAt };
        return Task.CompletedTask;
    }

    public Task MarkUndoneAsync(Guid executionId, DateTimeOffset undoneAt, string message, CancellationToken cancellationToken)
    {
        Records[executionId] = Records[executionId] with { UndoneAt = undoneAt, State = ExecutionState.Annulee, Message = message };
        return Task.CompletedTask;
    }

    public Task<ExecutionRecord?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
        Task.FromResult(Records.GetValueOrDefault(executionId));

    public Task<IReadOnlyList<ExecutionRecord>> ListInProgressAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExecutionRecord>>(Records.Values.Where(r => r.State == ExecutionState.EnCours).ToList());

    public Task<IReadOnlyList<ExecutionRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExecutionRecord>>(Records.Values.OrderByDescending(r => r.StartedAt).Take(limit).ToList());
}

internal sealed class InMemoryAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChainedAuditEntry>> ReadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChainedAuditEntry>>([]);
}

/// <summary>Action de test qui compte ses appels et peut échouer sur commande.</summary>
internal sealed class RecordingAction(ActionDescriptor descriptor) : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = descriptor;

    public int Executions { get; private set; }

    public int Undos { get; private set; }

    public bool Fail { get; set; }

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("{\"avant\":1}");

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        Executions++;
        return Task.FromResult(Fail ? ActionOutcome.Failed("Échec simulé.") : ActionOutcome.Ok("Fait."));
    }

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        Undos++;
        return Task.FromResult(ActionOutcome.Ok("Restauré."));
    }

    public static ActionDescriptor Describe(string id, RiskLevel risk = RiskLevel.Faible, bool reversible = true) =>
        new(id, "Test", "Action de test", "Pour les tests.", ["Étape 1"], risk, reversible, false, TimeSpan.FromSeconds(5));
}
