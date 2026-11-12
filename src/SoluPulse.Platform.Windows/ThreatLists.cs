using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoluPulse.Platform.Windows;

public sealed record FirmwareEntry(
    [property: JsonPropertyName("modele")] string Model,
    [property: JsonPropertyName("firmware")] string Firmware,
    [property: JsonPropertyName("conseil")] string Advice);

public sealed record SoftwareEntry(
    [property: JsonPropertyName("nom")] string Name,
    [property: JsonPropertyName("versionCorrigee")] string FixedVersion,
    [property: JsonPropertyName("faille")] string Flaw);

public sealed record SupportEntry(
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("famillePro")] DateOnly HomePro,
    [property: JsonPropertyName("entreprise")] DateOnly Enterprise);

/// <summary>Les listes de détection : firmwares dangereux, logiciels à faille connue, fin de support de Windows 11.</summary>
public sealed record ThreatListData(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("firmwaresDangereux")] IReadOnlyList<FirmwareEntry> Firmwares,
    [property: JsonPropertyName("logicielsVulnerables")] IReadOnlyList<SoftwareEntry> Software,
    [property: JsonPropertyName("windows11")] IReadOnlyList<SupportEntry> Windows11);

/// <summary>
/// Listes de détection, mises à jour sans nouvelle version de SoluPulse. La liste intégrée sert par
/// défaut ; une liste téléchargée n'est acceptée que signée par la clé de Solutech (ECDSA P-256), plus
/// récente que la liste en service, et dans des limites de taille raisonnables. Une liste ne contient
/// que des données : jamais de code.
/// </summary>
public static class ThreatLists
{
    public const string FileName = "listes.json";
    public const string SignatureName = "listes.json.sig";

    /// <summary>Clé publique de signature des listes ; la clé privée reste hors du dépôt, chez Solutech.</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQfibotizja0TzkXfhE4Avw9AWAPt
        9ugx/2QbqQWj8P+10Fy0IquKnwIiAPQUy6wENToizXZoxScHZEBSwLniRw==
        -----END PUBLIC KEY-----
        """;

    private const int MaxBytes = 512 * 1024;
    private const int MaxEntries = 2000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false, WriteIndented = true };

    public static ThreatListData Builtin { get; } = new(
        1,
        new DateOnly(2026, 11, 12),
        [
            new("Samsung SSD 980 PRO", "3B2QGXA7", "Firmware 3B2QGXA7 : usure accélérée puis passage en lecture seule. Installez le firmware 5B2QGXA7 ou plus récent avec Samsung Magician."),
            new("Samsung SSD 990 PRO", "0B2QJXD7", "Firmware 0B2QJXD7 : l'état de santé chute rapidement. Installez le firmware 1B2QJXD7 ou plus récent avec Samsung Magician."),
        ],
        [
            new("WinRAR", "7.13", "faille exploitée par des pièces jointes piégées (CVE-2025-8088)"),
            new("7-Zip", "25.0", "faille exploitée par des archives piégées (CVE-2025-11001)"),
            new("VLC media player", "3.0.21", "faille exploitée par des vidéos piégées (CVE-2024-46461)"),
        ],
        [
            new(22000, new DateOnly(2023, 10, 10), new DateOnly(2024, 10, 8)),
            new(22621, new DateOnly(2024, 10, 8), new DateOnly(2025, 10, 14)),
            new(22631, new DateOnly(2025, 11, 11), new DateOnly(2026, 11, 10)),
            new(26100, new DateOnly(2026, 10, 13), new DateOnly(2027, 10, 12)),
            new(26200, new DateOnly(2027, 10, 12), new DateOnly(2028, 10, 10)),
        ]);

    private static ThreatListData _current = Builtin;

    /// <summary>La liste en service : la liste intégrée, ou une liste téléchargée plus récente et signée.</summary>
    public static ThreatListData Current => Volatile.Read(ref _current);

    public static string Serialize(ThreatListData data) => JsonSerializer.Serialize(data, Json);

    /// <summary>Vérifie la signature, le contenu et la version, puis met la liste en service.</summary>
    public static bool TryApply(byte[] content, string signature, string publicKeyPem, out string reason)
    {
        if (content.Length is 0 or > MaxBytes)
        {
            reason = "taille invalide";
            return false;
        }

        if (!Verify(content, signature, publicKeyPem))
        {
            reason = "signature invalide";
            return false;
        }

        ThreatListData? data;
        try
        {
            data = JsonSerializer.Deserialize<ThreatListData>(content, Json);
        }
        catch (JsonException ex)
        {
            reason = $"contenu illisible ({ex.Message})";
            return false;
        }

        if (data is null || !IsValid(data))
        {
            reason = "contenu incomplet ou hors limites";
            return false;
        }

        if (data.Version <= Current.Version)
        {
            reason = "pas plus récente que la liste en service";
            return false;
        }

        Volatile.Write(ref _current, data);
        reason = $"version {data.Version} du {data.Date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"))}";
        return true;
    }

    /// <summary>Signature ECDSA P-256 sur SHA-256, encodée en Base64 (format DER).</summary>
    public static bool Verify(byte[] content, string signature, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(content, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    internal static void Reset() => Volatile.Write(ref _current, Builtin);

    private static bool IsValid(ThreatListData data) =>
        data.Firmwares is { Count: <= MaxEntries } && data.Software is { Count: <= MaxEntries } && data.Windows11 is { Count: <= MaxEntries }
        && data.Firmwares.All(f => Text(f.Model, 100) && Text(f.Firmware, 40) && Text(f.Advice, 500))
        && data.Software.All(s => Text(s.Name, 100) && Text(s.Flaw, 300) && Version.TryParse(s.FixedVersion, out _))
        && data.Windows11.All(w => w.Build is >= 22000 and < 100_000);

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
}
