using System.Runtime.InteropServices;

namespace SoluPulse.Platform.Windows.Interop;

/// <summary>
/// État du magasin des composants de Windows, l'équivalent de « DISM /Online /Cleanup-Image
/// /CheckHealth » : lecture seule et rapide, elle ne lance pas d'analyse complète. Demande les droits
/// du service.
/// </summary>
internal static partial class DismHealth
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int LogErrors = 0;
    private static readonly Lock Gate = new();

    /// <summary>0 sain, 1 réparable, 2 irréparable ; <c>null</c> si DISM ne répond pas.</summary>
    public static int? Check()
    {
        // L'API DISM n'accepte qu'une session à la fois par processus.
        lock (Gate)
        {
            if (DismInitialize(LogErrors, null, null) != 0)
            {
                return null;
            }

            try
            {
                if (DismOpenSession(OnlineImage, null, null, out var session) != 0)
                {
                    return null;
                }

                try
                {
                    return DismCheckImageHealth(session, 0, 0, 0, 0, out var state) == 0 ? state : null;
                }
                finally
                {
                    _ = DismCloseSession(session);
                }
            }
            finally
            {
                _ = DismShutdown();
            }
        }
    }

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismCheckImageHealth(uint session, int scanImage, nint cancelEvent, nint progress, nint userData, out int imageHealth);

    [LibraryImport("dismapi.dll")]
    private static partial int DismCloseSession(uint session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismShutdown();
}
