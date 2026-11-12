using System.Globalization;
using System.Security.Cryptography;

namespace SoluPulse.Core.Corrections;

public enum TechnicianCheck
{
    Accepte,
    Refuse,
    Bloque,
}

/// <summary>
/// Mode simplifié (« PC d'un proche ») : les corrections à risque et les réglages avancés demandent le
/// code du technicien. Le code n'est jamais enregistré en clair (PBKDF2-SHA256) ; après 5 erreurs, les
/// essais sont bloqués 15 minutes. La vérification se fait dans le service : l'application ne peut pas
/// la contourner.
/// </summary>
public sealed class TechnicianLock(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private const int Iterations = 210_000;

    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset? _lockedUntil;

    /// <summary>Indicateur partagé, lu à chaque description d'action (sans accès à la base).</summary>
    public bool SimpleMode { get; set; }

    /// <summary>En mode simplifié, une correction qui modifie Windows avec un risque moyen ou élevé.</summary>
    public bool IsTechnicianOnly(ActionDescriptor descriptor) =>
        SimpleMode && descriptor.Kind == ActionKind.Systeme && descriptor.Risk >= RiskLevel.Moyen;

    /// <summary>De 4 à 12 chiffres.</summary>
    public static bool IsValidFormat(string? code) => code is { Length: >= 4 and <= 12 } && code.All(char.IsAsciiDigit);

    public static string Hash(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(code, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture, $"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
    }

    public TechnicianCheck Verify(string? code, string? stored)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_lockedUntil is { } until && now < until)
            {
                return TechnicianCheck.Bloque;
            }

            if (Matches(code, stored))
            {
                _failures = 0;
                _lockedUntil = null;
                return TechnicianCheck.Accepte;
            }

            if (++_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntil = now + LockDuration;
                return TechnicianCheck.Bloque;
            }

            return TechnicianCheck.Refuse;
        }
    }

    internal static bool Matches(string? code, string? stored)
    {
        if (string.IsNullOrEmpty(code) || stored?.Split(':') is not [var iterations, var salt, var hash]
            || !int.TryParse(iterations, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 10_000)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(code, Convert.FromBase64String(salt), count, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Message(TechnicianCheck check) => check switch
    {
        TechnicianCheck.Bloque => $"Trop d'essais : le code du technicien est bloqué pendant {LockDuration.TotalMinutes:0} minutes.",
        _ => "Code du technicien incorrect.",
    };
}
