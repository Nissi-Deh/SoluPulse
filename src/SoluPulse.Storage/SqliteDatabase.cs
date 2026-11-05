using Dapper;
using Microsoft.Data.Sqlite;

namespace SoluPulse.Storage;

/// <summary>
/// Base locale SQLite (cahier des charges §5.1). Le mode WAL permet au service d'écrire pendant que
/// l'interface lit l'historique. Le fichier est protégé par ACL par le service (voir Agent).
/// </summary>
public sealed class SqliteDatabase
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE measurements (
            ts INTEGER NOT NULL,
            metric TEXT NOT NULL,
            value REAL NOT NULL,
            unit TEXT NOT NULL,
            source TEXT NOT NULL
        );
        CREATE INDEX ix_measurements_metric_ts ON measurements (metric, ts);
        CREATE INDEX ix_measurements_ts ON measurements (ts);

        CREATE TABLE measurements_hourly (
            hour INTEGER NOT NULL,
            metric TEXT NOT NULL,
            min REAL NOT NULL,
            max REAL NOT NULL,
            avg REAL NOT NULL,
            count INTEGER NOT NULL,
            unit TEXT NOT NULL,
            PRIMARY KEY (metric, hour)
        );

        CREATE TABLE alerts (
            id TEXT PRIMARY KEY,
            rule_id TEXT NOT NULL,
            subject TEXT NOT NULL,
            domain INTEGER NOT NULL,
            severity INTEGER NOT NULL,
            state INTEGER NOT NULL,
            opened_at INTEGER NOT NULL,
            resolved_at INTEGER,
            trigger_value REAL
        );
        CREATE UNIQUE INDEX ux_alerts_open ON alerts (rule_id, subject) WHERE state = 0;
        CREATE INDEX ix_alerts_opened ON alerts (opened_at);

        CREATE TABLE executions (
            id TEXT PRIMARY KEY,
            action_id TEXT NOT NULL,
            alert_id TEXT,
            origin INTEGER NOT NULL,
            parameters_json TEXT NOT NULL,
            caller_sid TEXT NOT NULL,
            caller_name TEXT NOT NULL,
            validated_at INTEGER NOT NULL,
            started_at INTEGER NOT NULL,
            finished_at INTEGER,
            state INTEGER NOT NULL,
            backup_json TEXT,
            message TEXT,
            verification INTEGER NOT NULL,
            undone_at INTEGER
        );
        CREATE INDEX ix_executions_started ON executions (started_at);

        CREATE TABLE audit (
            seq INTEGER PRIMARY KEY,
            ts INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            prev_hash TEXT NOT NULL,
            hash TEXT NOT NULL
        );

        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE inventory (
            kind TEXT PRIMARY KEY,
            ts INTEGER NOT NULL,
            json TEXT NOT NULL
        );

        CREATE TABLE sensor_issues (
            collector TEXT NOT NULL,
            sensor TEXT NOT NULL,
            message TEXT NOT NULL,
            ts INTEGER NOT NULL,
            PRIMARY KEY (collector, sensor)
        );
        """,
    ];

    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;").ConfigureAwait(false);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);
        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(Migrations[i], transaction: transaction).ConfigureAwait(false);
            await connection.ExecuteAsync($"PRAGMA user_version = {i + 1};", transaction: transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;").ConfigureAwait(false);
    }

    internal static long ToUnix(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromUnix(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    internal static DateTimeOffset? FromUnix(long? value) => value is { } v ? FromUnix(v) : null;
}
