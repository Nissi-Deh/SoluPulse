using Dapper;
using SoluPulse.Core;

namespace SoluPulse.Storage;

public sealed record HistoryPoint(DateTimeOffset Timestamp, double Value, double Min, double Max);

public sealed record MetricInfo(string Metric, string Unit, DateTimeOffset LastSeen, double LastValue);

/// <summary>
/// Historique des mesures (EF-COL-10) : détail pendant la rétention (90 jours par défaut), puis
/// agrégats horaires min/max/moyenne, pour tenir sous 500 Mo (ENF-PERF-05).
/// </summary>
public sealed class MeasurementStore(SqliteDatabase database)
{
    public async Task InsertAsync(IReadOnlyCollection<Measurement> measurements, CancellationToken cancellationToken = default)
    {
        if (measurements.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "INSERT INTO measurements (ts, metric, value, unit, source) VALUES (@Ts, @Metric, @Value, @Unit, @Source)",
            measurements.Select(m => new { Ts = SqliteDatabase.ToUnix(m.Timestamp), m.Metric, m.Value, m.Unit, m.Source }),
            transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MetricInfo>> ListMetricsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Metric, string Unit, long Ts, double Value)>(
            """
            SELECT m.metric, m.unit, m.ts, m.value
            FROM measurements m
            JOIN (SELECT metric, MAX(ts) AS ts FROM measurements GROUP BY metric) last
              ON last.metric = m.metric AND last.ts = m.ts
            GROUP BY m.metric
            ORDER BY m.metric
            """).ConfigureAwait(false);
        return rows.Select(r => new MetricInfo(r.Metric, r.Unit, SqliteDatabase.FromUnix(r.Ts), r.Value)).ToList();
    }

    /// <summary>
    /// Historique d'une métrique, réduit à <paramref name="maxPoints"/> points au plus pour les graphiques.
    /// </summary>
    public async Task<IReadOnlyList<HistoryPoint>> HistoryAsync(
        string metric,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxPoints = 500,
        CancellationToken cancellationToken = default)
    {
        var fromMs = SqliteDatabase.ToUnix(from);
        var toMs = SqliteDatabase.ToUnix(to);
        var bucket = Math.Max(1, (toMs - fromMs) / Math.Max(1, maxPoints));

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var detail = await connection.QueryAsync<(long Bucket, double Avg, double Min, double Max)>(
            """
            SELECT (ts / @Bucket) * @Bucket AS bucket, AVG(value), MIN(value), MAX(value)
            FROM measurements
            WHERE metric = @Metric AND ts BETWEEN @From AND @To
            GROUP BY bucket
            """,
            new { Metric = metric, From = fromMs, To = toMs, Bucket = bucket }).ConfigureAwait(false);
        var hourly = await connection.QueryAsync<(long Bucket, double Avg, double Min, double Max)>(
            """
            SELECT (hour / @Bucket) * @Bucket AS bucket, SUM(avg * count) / SUM(count), MIN(min), MAX(max)
            FROM measurements_hourly
            WHERE metric = @Metric AND hour BETWEEN @From AND @To
            GROUP BY bucket
            """,
            new { Metric = metric, From = fromMs, To = toMs, Bucket = bucket }).ConfigureAwait(false);

        return hourly.Concat(detail)
            .GroupBy(p => p.Bucket)
            .Select(g => g.First())
            .OrderBy(p => p.Bucket)
            .Select(p => new HistoryPoint(SqliteDatabase.FromUnix(p.Bucket), p.Avg, p.Min, p.Max))
            .ToList();
    }

    /// <summary>
    /// Agrège en heures les mesures plus anciennes que <paramref name="detailRetention"/>, puis supprime
    /// le détail et les agrégats plus anciens que <paramref name="aggregateRetention"/>.
    /// </summary>
    public async Task<int> CompactAsync(DateTimeOffset now, TimeSpan detailRetention, TimeSpan aggregateRetention, CancellationToken cancellationToken = default)
    {
        var detailLimit = SqliteDatabase.ToUnix(now - detailRetention);
        var aggregateLimit = SqliteDatabase.ToUnix(now - aggregateRetention);
        const long hourMs = 3_600_000;

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO measurements_hourly (hour, metric, min, max, avg, count, unit)
            SELECT (ts / @Hour) * @Hour, metric, MIN(value), MAX(value), AVG(value), COUNT(*), MAX(unit)
            FROM measurements
            WHERE ts < @Limit
            GROUP BY (ts / @Hour), metric
            ON CONFLICT (metric, hour) DO UPDATE SET
                min = MIN(measurements_hourly.min, excluded.min),
                max = MAX(measurements_hourly.max, excluded.max),
                avg = (measurements_hourly.avg * measurements_hourly.count + excluded.avg * excluded.count)
                      / (measurements_hourly.count + excluded.count),
                count = measurements_hourly.count + excluded.count
            """,
            new { Hour = hourMs, Limit = detailLimit },
            transaction).ConfigureAwait(false);
        var deleted = await connection.ExecuteAsync("DELETE FROM measurements WHERE ts < @Limit", new { Limit = detailLimit }, transaction).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM measurements_hourly WHERE hour < @Limit", new { Limit = aggregateLimit }, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }
}
