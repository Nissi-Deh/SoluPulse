using System.Text;
using System.Text.RegularExpressions;

namespace SoluPulse.Platform.Windows.Interop;

/// <summary>Lecture prudente des lignes de commande et des emplacements de programmes.</summary>
public static partial class ProgramPaths
{
    private static readonly string[] UserWritableMarkers = [@"\AppData\", @"\Temp\", @"\Users\Public\", @"\Downloads\", @"\ProgramData\", @"\$Recycle.Bin\"];
    private static readonly string[] HiddenPlaces = [@"\Temp\", @"\Downloads\", @"\Users\Public\", @"\$Recycle.Bin\"];
    private static readonly string[] RootFolders = [@"\AppData\Roaming", @"\AppData\Local", @"\AppData\LocalLow", @"\ProgramData"];

    /// <summary>Interpréteurs que les logiciels malveillants utilisent pour lancer un script caché.</summary>
    private static readonly string[] ScriptHosts = ["powershell", "pwsh", "mshta", "wscript", "cscript", "rundll32", "regsvr32", "cmd", "certutil", "bitsadmin", "conhost"];

    /// <summary>Contenus qui ne se trouvent pas dans une commande légitime : script encodé ou téléchargé.</summary>
    private static readonly string[] Payloads = ["-enc ", "-encodedcommand", "frombase64string", "downloadstring", "downloadfile", "invoke-webrequest", "invoke-expression", "iwr ", "iex ", "iex(", "-urlcache", "http://", "https://"];

    /// <summary>Le programme lancé par une commande : entre guillemets, ou jusqu'à « .exe ».</summary>
    public static string? Executable(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
    }

    /// <summary>Ce qui suit le programme dans la commande.</summary>
    public static string Arguments(string command, string executable)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        var index = text.IndexOf(executable, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "" : text[(index + executable.Length)..].TrimStart('"').Trim();
    }

    /// <summary>Dossier où n'importe quel programme lancé par l'utilisateur peut écrire.</summary>
    public static bool IsUserWritable(string path) =>
        UserWritableMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase))
        && !path.Contains(@"\ProgramData\Microsoft\", StringComparison.OrdinalIgnoreCase)

        // Alias des applications du Microsoft Store : des liens vers des paquets signés, pas des programmes.
        && !path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Les cachettes préférées des logiciels malveillants : Temp, Téléchargements, Public, corbeille, un
    /// programme posé à la racine d'AppData ou de ProgramData, ou dans AppData\Roaming\Microsoft. Les
    /// logiciels légitimes installés pour un seul utilisateur ont leur propre dossier
    /// (AppData\Local\Programs\…) et n'en font pas partie.
    /// </summary>
    public static bool IsSuspiciousLocation(string path)
    {
        if (!IsUserWritable(path))
        {
            return false;
        }

        if (HiddenPlaces.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var folder = (Path.GetDirectoryName(path) ?? "").TrimEnd('\\');
        return RootFolders.Any(r => folder.EndsWith(r, StringComparison.OrdinalIgnoreCase))
            || (path.Contains(@"\AppData\Roaming\Microsoft\", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsWindows(string path) =>
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Une commande suspecte : un interpréteur de scripts avec un contenu encodé ou téléchargé, ou qui
    /// lance un fichier rangé dans une cachette ; ou un programme non signé rangé dans une cachette.
    /// </summary>
    public static bool IsSuspiciousLaunch(string executable, string arguments, Func<string, SignatureState> signature)
    {
        var program = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        var args = Environment.ExpandEnvironmentVariables(arguments);
        var name = Path.GetFileNameWithoutExtension(program).ToLowerInvariant();
        if (ScriptHosts.Contains(name))
        {
            var lower = args.ToLowerInvariant() + " ";
            return Payloads.Any(p => lower.Contains(p, StringComparison.Ordinal))
                || PathsIn(args).Any(IsSuspiciousLocation);
        }

        return program.Length > 0
            && !IsWindows(program)
            && IsSuspiciousLocation(program)
            && signature(program) is SignatureState.NonSigne or SignatureState.Invalide;
    }

    /// <summary>Chemins de fichiers cités dans une ligne de commande.</summary>
    internal static IEnumerable<string> PathsIn(string text) => FilePath().Matches(text).Select(m => m.Value);

    /// <summary>
    /// Le programme visé par un raccourci (.lnk) : son chemin est écrit en clair dans le fichier, en
    /// ASCII ou en UTF-16. Pas besoin d'interpréter tout le format.
    /// </summary>
    public static string? ShortcutTarget(string shortcut)
    {
        try
        {
            var info = new FileInfo(shortcut);
            if (!info.Exists || info.Length > 65_536)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(shortcut);
            var text = Encoding.Unicode.GetString(bytes) + "\n" + Encoding.Latin1.GetString(bytes);
            return FilePath().Match(text) is { Success: true } match ? match.Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\x00-\x1f""<>|*?\n]*?\.(exe|scr|com|bat|cmd|vbs|vbe|js|jse|wsf|hta|ps1|dll)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FilePath();
}
