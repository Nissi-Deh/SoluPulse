using System.Text.Json;
using Dapper;
using SoluPulse.Core.Corrections;

namespace SoluPulse.Storage;

public sealed class ExecutionJournal(SqliteDatabase database) : IExecutionJournal
{
    private const string Columns =
        "id, action_id AS ActionId, alert_id AS AlertId, origin, parameters_json AS ParametersJson, caller_sid AS CallerSid, " +
        "caller_name AS CallerName, validated_at AS ValidatedAt, started_at AS StartedAt, finished_at AS FinishedAt, state, " +
        "backup_json AS BackupJson, message, verification, undone_at AS UndoneAt";

    public async Task BeginAsync(ExecutionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO executions (id, action_id, alert_id, origin, parameters_json, caller_sid, caller_name,
                                    validated_at, started_at, state, backup_json, verification)
            VALUES (@Id, @ActionId, @AlertId, @Origin, @Parameters, @Sid, @Name, @ValidatedAt, @StartedAt, @State, @Backup, 0)
            """,
            new
            {
                Id = record.Id.ToString(),
                record.ActionId,
                AlertId = record.AlertId?.ToString(),
                Origin = (int)record.Origin,
                Parameters = JsonSerializer.Serialize(record.Parameters),
                Sid = record.ValidatedBy.UserSid,
                Name = record.ValidatedBy.UserName,
                ValidatedAt = SqliteDatabase.ToUnix(record.ValidatedAt),
                StartedAt = SqliteDatabase.ToUnix(record.StartedAt),
                State = (int)record.State,
                record.Backup,
            }).ConfigureAwait(false);
    }

    public async Task SaveBackupAsync(Guid executionId, string backup, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("UPDATE executions SET backup_json = @Backup WHERE id = @Id", new { Id = executionId.ToString(), Backup = backup }).ConfigureAwait(false);
    }

    public async Task CompleteAsync(Guid executionId, ExecutionState state, string message, VerificationResult verification, DateTimeOffset finishedAt, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE executions SET state = @State, message = @Message, verification = @Verification, finished_at = @At WHERE id = @Id",
            new { Id = executionId.ToString(), State = (int)state, Message = message, Verification = (int)verification, At = SqliteDatabase.ToUnix(finishedAt) }).ConfigureAwait(false);
    }

    public async Task MarkUndoneAsync(Guid executionId, DateTimeOffset undoneAt, string message, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE executions SET state = @State, undone_at = @At, message = @Message WHERE id = @Id",
            new { Id = executionId.ToString(), State = (int)ExecutionState.Annulee, At = SqliteDatabase.ToUnix(undoneAt), Message = message }).ConfigureAwait(false);
    }

    public async Task<ExecutionRecord?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM executions WHERE id = @Id", new { Id = executionId.ToString() }).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListInProgressAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>($"SELECT {Columns} FROM executions WHERE state = @State", new { State = (int)ExecutionState.EnCours }).ConfigureAwait(false);
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>($"SELECT {Columns} FROM executions ORDER BY started_at DESC LIMIT @Limit", new { Limit = limit }).ConfigureAwait(false);
        return rows.Select(r => r.ToRecord()).ToList();
    }

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string? AlertId { get; set; }
        public long Origin { get; set; }
        public string ParametersJson { get; set; } = "{}";
        public string CallerSid { get; set; } = "";
        public string CallerName { get; set; } = "";
        public long ValidatedAt { get; set; }
        public long StartedAt { get; set; }
        public long? FinishedAt { get; set; }
        public long State { get; set; }
        public string? BackupJson { get; set; }
        public string? Message { get; set; }
        public long Verification { get; set; }
        public long? UndoneAt { get; set; }

        public ExecutionRecord ToRecord() => new(
            Guid.Parse(Id),
            ActionId,
            AlertId is null ? null : Guid.Parse(AlertId),
            (ExecutionOrigin)Origin,
            JsonSerializer.Deserialize<Dictionary<string, string>>(ParametersJson) ?? [],
            new CallerIdentity(CallerSid, CallerName),
            SqliteDatabase.FromUnix(ValidatedAt),
            SqliteDatabase.FromUnix(StartedAt),
            SqliteDatabase.FromUnix(FinishedAt),
            (ExecutionState)State,
            BackupJson,
            Message,
            (VerificationResult)Verification,
            SqliteDatabase.FromUnix(UndoneAt));
    }
}
