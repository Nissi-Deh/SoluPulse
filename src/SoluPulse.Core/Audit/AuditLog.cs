using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoluPulse.Core.Audit;

/// <summary>Une entrée du journal d'audit (EF-COR-05) : origine, validation, horodatage, résultat.</summary>
public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Kind,
    string? ActionId,
    Guid? ExecutionId,
    Guid? AlertId,
    string Origin,
    string? ValidatedBy,
    DateTimeOffset? ValidatedAt,
    string Result,
    string? Details);

public sealed record ChainedAuditEntry(long Sequence, AuditEntry Entry, string PreviousHash, string Hash);

public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChainedAuditEntry>> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Chaînage par empreintes (ENF-SEC-06) : chaque entrée contient le SHA-256 de l'entrée précédente
/// et de son propre contenu. Modifier ou supprimer une entrée casse toutes les suivantes.
/// </summary>
public static class AuditChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(AuditEntry entry) => JsonSerializer.Serialize(entry, JsonOptions);

    public static AuditEntry Deserialize(string json) =>
        JsonSerializer.Deserialize<AuditEntry>(json, JsonOptions) ?? throw new InvalidDataException("Entrée d'audit vide.");

    public static string ComputeHash(long sequence, string previousHash, string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes($"{sequence}\n{previousHash}\n{payloadJson}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Renvoie le numéro de la première entrée altérée, ou <c>null</c> si la chaîne est intacte.</summary>
    public static long? FindFirstBrokenEntry(IEnumerable<(long Sequence, string PayloadJson, string PreviousHash, string Hash)> entries)
    {
        var expectedPrevious = GenesisHash;
        long? expectedSequence = null;
        foreach (var (sequence, payload, previous, hash) in entries)
        {
            if (expectedSequence is not null && sequence != expectedSequence)
            {
                return expectedSequence;
            }

            if (previous != expectedPrevious || ComputeHash(sequence, previous, payload) != hash)
            {
                return sequence;
            }

            expectedPrevious = hash;
            expectedSequence = sequence + 1;
        }

        return null;
    }
}
