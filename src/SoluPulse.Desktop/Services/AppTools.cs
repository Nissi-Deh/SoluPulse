using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SoluPulse.Desktop.Services;

/// <summary>
/// Lance le désinstalleur officiel d'un programme, comme « Applications installées » de Windows. Le
/// désinstalleur demande lui-même l'accord d'un administrateur et ses propres confirmations.
/// </summary>
public static partial class Uninstaller
{
    public static void Start(string command)
    {
        var (program, arguments) = Split(command) ?? throw new InvalidOperationException("Ce programme n'indique pas comment le désinstaller : utilisez « Applications installées » de Windows.");
        try
        {
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Désinstallation annulée : Windows demande l'accord d'un administrateur.", ex);
        }
    }

    /// <summary>
    /// Sépare le programme de ses arguments. Pour Windows Installer, /I (installer ou modifier) devient
    /// /X (désinstaller). Seul un programme existant, ou msiexec, est accepté.
    /// </summary>
    internal static (string Program, string Arguments)? Split(string? command)
    {
        var text = Environment.ExpandEnvironmentVariables(command?.Trim() ?? "");
        if (text.Length == 0)
        {
            return null;
        }

        if (MsiCommand().Match(text) is { Success: true } msi)
        {
            return ("msiexec.exe", $"/X{msi.Groups[1].Value}");
        }

        string program, arguments;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0)
            {
                return null;
            }

            program = text[1..end];
            arguments = text[(end + 1)..].Trim();
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            program = exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
            arguments = exe > 0 ? text[(exe + 4)..].Trim() : string.Join(' ', text.Split(' ').Skip(1));
        }

        return Path.IsPathFullyQualified(program) && File.Exists(program) ? (program, arguments) : null;
    }

    [GeneratedRegex(@"^msiexec(?:\.exe)?\s+/[IX]\s*(\{[0-9A-Fa-f\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCommand();
}

public sealed record WingetUpgrade(string Name, string Id, string Version, string Available);

/// <summary>
/// Mises à jour des logiciels par winget, le gestionnaire de paquets de Windows : la liste vient de
/// « winget upgrade », la mise à jour de « winget upgrade --id … --exact ». Les sources sont celles de
/// Microsoft ; rien n'est installé sans le clic de l'utilisateur.
/// </summary>
public static partial class Winget
{
    public static async Task<IReadOnlyList<WingetUpgrade>> ListAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await RunAsync(["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"], TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        var upgrades = Parse(output);
        return upgrades.Count == 0 && code != 0 && !output.Contains("---", StringComparison.Ordinal) && !NoUpdates(output)
            ? throw new InvalidOperationException($"winget n'a pas pu lister les mises à jour (code 0x{code:X8}). La connexion à ses sources est peut-être bloquée par le réseau.")
            : upgrades;
    }

    public static async Task<(bool Success, string Message)> UpgradeAsync(WingetUpgrade upgrade, CancellationToken cancellationToken)
    {
        if (!PackageId().IsMatch(upgrade.Id))
        {
            return (false, "Identifiant de paquet invalide.");
        }

        var (code, _) = await RunAsync(["upgrade", "--id", upgrade.Id, "--exact", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        return code == 0
            ? (true, $"{upgrade.Name} est à jour (version {upgrade.Available}).")
            : (false, $"La mise à jour de {upgrade.Name} a échoué (code 0x{code:X8}). Le programme est peut-être ouvert : fermez-le et réessayez.");
    }

    /// <summary>
    /// Tableau de « winget upgrade » : la ligne de tirets sépare l'en-tête des lignes ; les colonnes se
    /// lisent aux positions des titres (Nom, ID, Version, Disponible, Source), quelle que soit la langue.
    /// </summary>
    internal static IReadOnlyList<WingetUpgrade> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Contains('\r', StringComparison.Ordinal) ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();
        var separator = lines.FindIndex(l => l.Length > 20 && l.All(c => c == '-'));
        if (separator <= 0)
        {
            return [];
        }

        var header = lines[separator - 1];
        var starts = Columns().Matches(header).Select(m => m.Index).ToList();
        if (starts.Count < 4)
        {
            return [];
        }

        var upgrades = new List<WingetUpgrade>();
        foreach (var line in lines.Skip(separator + 1))
        {
            if (line.Length <= starts[3])
            {
                break;
            }

            string Column(int index) => index + 1 < starts.Count
                ? line[starts[index]..Math.Min(line.Length, starts[index + 1])].Trim()
                : line[Math.Min(line.Length, starts[index])..].Trim();

            var id = Column(1);
            if (PackageId().IsMatch(id))
            {
                upgrades.Add(new WingetUpgrade(Column(0), id, Column(2), Column(3)));
            }
        }

        return upgrades;
    }

    private static bool NoUpdates(string output) =>
        output.Contains("Aucune mise à niveau", StringComparison.OrdinalIgnoreCase) || output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase);

    private static async Task<(int Code, string Output)> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("winget.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("winget n'a pas démarré.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("winget n'est pas installé sur ce PC : installez « App Installer » depuis le Microsoft Store.", ex);
        }

        using (process)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(timer.Token);
            var errors = process.StandardError.ReadToEndAsync(timer.Token);
            try
            {
                await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("winget n'a pas répondu à temps.");
            }

            return (process.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_\-]*[A-Za-z0-9]$")]
    private static partial Regex PackageId();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Columns();
}
