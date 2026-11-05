using Dapper;
using SoluPulse.Core;

namespace SoluPulse.Storage;

public sealed class AlertStore(SqliteDatabase database)
{
    private const string Columns = "id, rule_id AS RuleId, subject, domain, severity, state, opened_at AS OpenedAt, resolved_at AS ResolvedAt, trigger_value AS TriggerValue";

    /// <summary>
    /// Ouvre une alerte. S'il en existe déjà une ouverte pour la même règle et le même sujet, elle est
    /// renvoyée telle quelle : l'index unique empêche les doublons (EF-DIA-06).
    /// </summary>
    public async Task<Alert> OpenAsync(string ruleId, string subject, HealthDomain domain, Severity severity, double? value, DateTimeOffset openedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var alert = new Alert(Guid.NewGuid(), ruleId, subject, domain, severity, AlertState.Active, openedAt, null, value);
        var inserted = await connection.ExecuteAsync(
            """
            INSERT INTO alerts (id, rule_id, subject, domain, severity, state, opened_at, trigger_value)
            VALUES (@Id, @RuleId, @Subject, @Domain, @Severity, 0, @OpenedAt, @Value)
            ON CONFLICT DO NOTHING
            """,
            new { Id = alert.Id.ToString(), RuleId = ruleId, Subject = subject, Domain = (int)domain, Severity = (int)severity, OpenedAt = SqliteDatabase.ToUnix(openedAt), Value = value }).ConfigureAwait(false);
        if (inserted == 1)
        {
            return alert;
        }

        var existing = await connection.QuerySingleAsync<AlertRow>(
            $"SELECT {Columns} FROM alerts WHERE rule_id = @RuleId AND subject = @Subject AND state = 0",
            new { RuleId = ruleId, Subject = subject }).ConfigureAwait(false);
        return existing.ToAlert();
    }

    public async Task<Alert?> ResolveAsync(string ruleId, string subject, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<AlertRow>(
            $"SELECT {Columns} FROM alerts WHERE rule_id = @RuleId AND subject = @Subject AND state = 0",
            new { RuleId = ruleId, Subject = subject }).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        await connection.ExecuteAsync(
            "UPDATE alerts SET state = 1, resolved_at = @At WHERE id = @Id",
            new { Id = row.Id, At = SqliteDatabase.ToUnix(resolvedAt) }).ConfigureAwait(false);
        return row.ToAlert() with { State = AlertState.Resolue, ResolvedAt = resolvedAt };
    }

    public async Task<Alert?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<AlertRow>($"SELECT {Columns} FROM alerts WHERE id = @Id", new { Id = id.ToString() }).ConfigureAwait(false);
        return row?.ToAlert();
    }

    public async Task<IReadOnlyList<Alert>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AlertRow>($"SELECT {Columns} FROM alerts WHERE state = 0 ORDER BY severity DESC, opened_at DESC").ConfigureAwait(false);
        return rows.Select(r => r.ToAlert()).ToList();
    }

    public async Task<IReadOnlyList<Alert>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AlertRow>($"SELECT {Columns} FROM alerts ORDER BY opened_at DESC LIMIT @Limit", new { Limit = limit }).ConfigureAwait(false);
        return rows.Select(r => r.ToAlert()).ToList();
    }

    private sealed class AlertRow
    {
        public string Id { get; set; } = "";
        public string RuleId { get; set; } = "";
        public string Subject { get; set; } = "";
        public long Domain { get; set; }
        public long Severity { get; set; }
        public long State { get; set; }
        public long OpenedAt { get; set; }
        public long? ResolvedAt { get; set; }
        public double? TriggerValue { get; set; }

        public Alert ToAlert() => new(
            Guid.Parse(Id), RuleId, Subject, (HealthDomain)Domain, (Core.Severity)Severity, (AlertState)State,
            SqliteDatabase.FromUnix(OpenedAt), SqliteDatabase.FromUnix(ResolvedAt), TriggerValue);
    }
}
