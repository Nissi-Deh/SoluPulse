using Dapper;
using SoluPulse.Core.Audit;

namespace SoluPulse.Storage;

/// <summary>Journal d'audit chaîné par empreintes SHA-256 (ENF-SEC-06).</summary>
public sealed class SqliteAuditLog(SqliteDatabase database) : IAuditLog, IDisposable
{
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        var payload = AuditChain.Serialize(entry);
        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var last = (await connection.QueryAsync<(long Seq, string Hash)>(
                "SELECT seq, hash FROM audit ORDER BY seq DESC LIMIT 1", transaction: transaction).ConfigureAwait(false)).ToList();
            var sequence = last.Count == 0 ? 1 : last[0].Seq + 1;
            var previous = last.Count == 0 ? AuditChain.GenesisHash : last[0].Hash;
            await connection.ExecuteAsync(
                "INSERT INTO audit (seq, ts, payload_json, prev_hash, hash) VALUES (@Seq, @Ts, @Payload, @Prev, @Hash)",
                new { Seq = sequence, Ts = SqliteDatabase.ToUnix(entry.Timestamp), Payload = payload, Prev = previous, Hash = AuditChain.ComputeHash(sequence, previous, payload) },
                transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public void Dispose() => _appendLock.Dispose();

    public async Task<IReadOnlyList<ChainedAuditEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new ChainedAuditEntry(r.Seq, AuditChain.Deserialize(r.Payload), r.Prev, r.Hash)).ToList();
    }

    /// <summary>Renvoie le numéro de la première entrée altérée, ou <c>null</c> si le journal est intact.</summary>
    public async Task<long?> VerifyAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(cancellationToken).ConfigureAwait(false);
        return AuditChain.FindFirstBrokenEntry(rows.Select(r => (r.Seq, r.Payload, r.Prev, r.Hash)));
    }

    private async Task<IReadOnlyList<(long Seq, string Payload, string Prev, string Hash)>> ReadRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(long Seq, string Payload, string Prev, string Hash)>(
            "SELECT seq, payload_json, prev_hash, hash FROM audit ORDER BY seq").ConfigureAwait(false);
        return rows.ToList();
    }
}
