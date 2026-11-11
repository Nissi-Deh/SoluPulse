using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SoluPulse.Platform.Windows.Interop;

/// <summary>
/// La session de l'écran du PC : verrouillée, inactive, ou personne de connecté. Le service tourne
/// hors de cette session et l'interroge par les services Bureau à distance (WTSSessionInfoEx).
/// </summary>
public static partial class ConsoleSession
{
    private const int SessionInfoEx = 25;
    private const int StateActive = 0;
    private const int FlagUnlocked = 1;
    private const int MinimumLength = 208;

    /// <summary>
    /// Vrai si personne n'utilise le PC : session verrouillée, déconnectée ou absente. La date de
    /// dernière saisie n'est pas utilisée : Windows ne la tient pas à jour pour la session de l'écran.
    /// <c>null</c> si Windows ne répond pas.
    /// </summary>
    public static bool? IsUnattended()
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
        {
            return true;
        }

        if (!WTSQuerySessionInformationW(0, session, SessionInfoEx, out var buffer, out var length) || buffer == 0)
        {
            return null;
        }

        try
        {
            if (length < MinimumLength)
            {
                return null;
            }

            var data = new byte[MinimumLength];
            Marshal.Copy(buffer, data, 0, data.Length);
            return Interpret(data, DateTime.UtcNow);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// WTSINFOEXW : niveau sur 4 octets, puis l'union alignée sur 8 octets. WTSINFOEX_LEVEL1_W :
    /// identifiant (8), état (12), indicateurs (16), trois noms en UTF-16 (20 à 164), puis les dates
    /// au format FILETIME à partir de 168 : ouverture, connexion, déconnexion, dernière saisie (192),
    /// heure courante (200). Une heure courante incohérente signale une mise en page inattendue.
    /// </summary>
    internal static bool? Interpret(ReadOnlySpan<byte> data, DateTime nowUtc)
    {
        var state = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        var current = BinaryPrimitives.ReadInt64LittleEndian(data[200..]);
        if (current <= 0 || Math.Abs((DateTime.FromFileTimeUtc(current) - nowUtc).TotalHours) > 24)
        {
            return null;
        }

        return state != StateActive || flags != FlagUnlocked;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
