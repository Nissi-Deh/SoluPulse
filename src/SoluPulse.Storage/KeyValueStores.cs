using Dapper;
using SoluPulse.Core;

namespace SoluPulse.Storage;

public sealed class SettingsStore(SqliteDatabase database)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Key, string Value)>("SELECT key, value FROM settings").ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    public async Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in values)
        {
            if (value is null)
            {
                await connection.ExecuteAsync("DELETE FROM settings WHERE key = @Key", new { Key = key }, transaction).ConfigureAwait(false);
            }
            else
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@Key, @Value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    new { Key = key, Value = value }, transaction).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class InventoryStore(SqliteDatabase database)
{
    public async Task SaveAsync(IEnumerable<InventorySnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO inventory (kind, ts, json) VALUES (@Kind, @Ts, @Json) ON CONFLICT (kind) DO UPDATE SET ts = excluded.ts, json = excluded.json",
            snapshots.Select(s => new { s.Kind, Ts = SqliteDatabase.ToUnix(s.Timestamp), s.Json })).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventorySnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Kind, long Ts, string Json)>("SELECT kind, ts, json FROM inventory ORDER BY kind").ConfigureAwait(false);
        return rows.Select(r => new InventorySnapshot(r.Kind, SqliteDatabase.FromUnix(r.Ts), r.Json)).ToList();
    }

    public async Task<InventorySnapshot?> GetAsync(string kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Kind, long Ts, string Json)>(
            "SELECT kind, ts, json FROM inventory WHERE kind = @Kind", new { Kind = kind }).ConfigureAwait(false);
        return rows.Select(r => new InventorySnapshot(r.Kind, SqliteDatabase.FromUnix(r.Ts), r.Json)).FirstOrDefault();
    }
}

/// <summary>Capteurs absents ou illisibles, par collecteur (EF-COL-12).</summary>
public sealed class SensorIssueStore(SqliteDatabase database)
{
    public async Task ReplaceAsync(string collector, IEnumerable<SensorIssue> issues, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM sensor_issues WHERE collector = @Collector", new { Collector = collector }, transaction).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT OR REPLACE INTO sensor_issues (collector, sensor, message, ts) VALUES (@Collector, @Sensor, @Message, @Ts)",
            issues.Select(i => new { i.Collector, i.Sensor, i.Message, Ts = SqliteDatabase.ToUnix(i.Timestamp) }),
            transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SensorIssue>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Collector, string Sensor, string Message, long Ts)>(
            "SELECT collector, sensor, message, ts FROM sensor_issues ORDER BY collector, sensor").ConfigureAwait(false);
        return rows.Select(r => new SensorIssue(r.Collector, r.Sensor, r.Message, SqliteDatabase.FromUnix(r.Ts))).ToList();
    }
}
