using System.Text;
using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

public enum UsbThreatKind
{
    /// <summary>Raccourci qui lance un script ou un interpréteur : le « virus raccourci ».</summary>
    Raccourci,

    /// <summary>Script caché à la racine de la clé.</summary>
    Script,

    /// <summary>autorun.inf qui lance un programme.</summary>
    Autorun,

    /// <summary>Dossier de l'utilisateur caché par le virus : à rendre visible, pas à supprimer.</summary>
    DossierCache,
}

public sealed record UsbThreat(string Path, UsbThreatKind Kind);

/// <summary>
/// Analyse la racine des clés USB et cartes mémoire branchées, une fois par minute : le virus
/// « raccourci », très répandu dans les cybercafés, cache les dossiers et les remplace par des
/// raccourcis qui lancent un script. Seule la racine est lue : l'analyse est instantanée.
/// </summary>
public sealed class UsbScanCollector(TimeProvider timeProvider) : ICollector
{
    public string Name => "cles_usb";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        result.Try("racines", () =>
        {
            var drives = new List<object>();
            var total = 0;
            foreach (var drive in UsbThreats.RemovableDrives())
            {
                var threats = UsbThreats.Scan(drive);
                var dangerous = threats.Count(t => t.Kind != UsbThreatKind.DossierCache);
                total += dangerous;
                drives.Add(new { Lecteur = drive, Suspects = threats.Where(t => t.Kind != UsbThreatKind.DossierCache).Select(t => System.IO.Path.GetFileName(t.Path)).ToList(), DossiersCaches = threats.Count(t => t.Kind == UsbThreatKind.DossierCache) });
            }

            result.Add("securite.usb.fichiers_suspects", total, "");
            result.Snapshot("cles_usb", drives);
        });

        return Task.FromResult(result.Build());
    }
}

public static class UsbThreats
{
    private static readonly string[] ScriptExtensions = [".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".bat", ".cmd", ".ps1", ".hta", ".scr", ".pif"];
    private static readonly string[] ShortcutPayloads = ["cmd.exe", "%comspec%", "wscript", "cscript", "rundll32", "powershell", "mshta", "regsvr32", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".bat"];
    private static readonly string[] SystemFolders = ["System Volume Information", "$RECYCLE.BIN", "RECYCLER", "Recycled"];

    /// <summary>Racines des lecteurs amovibles prêts (clés USB, cartes mémoire).</summary>
    public static IReadOnlyList<string> RemovableDrives() => DriveInfo.GetDrives()
        .Where(d =>
        {
            try
            {
                return d.DriveType == DriveType.Removable && d.IsReady;
            }
            catch (IOException)
            {
                return false;
            }
        })
        .Select(d => d.RootDirectory.FullName)
        .ToList();

    public static IReadOnlyList<UsbThreat> Scan(string root)
    {
        var threats = new List<UsbThreat>();
        var directory = new DirectoryInfo(root);
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return threats;
        }

        var hiddenFolders = entries.OfType<DirectoryInfo>()
            .Where(d => d.Attributes.HasFlag(FileAttributes.Hidden) && !SystemFolders.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var files = entries.OfType<FileInfo>().ToList();
        var hiddenScripts = files
            .Where(f => f.Attributes.HasFlag(FileAttributes.Hidden) && ScriptExtensions.Contains(f.Extension.ToLowerInvariant()))
            .ToList();
        var shortcuts = files.Where(f => f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)).ToList();

        // Le virus « raccourci » : un raccourci qui porte le nom d'un dossier caché, ou un raccourci vers un
        // interpréteur à côté de dossiers ou de scripts cachés. Un raccourci vers un script posé là par
        // l'utilisateur, sur une clé sans rien de caché, n'est pas une infection.
        var disguised = shortcuts
            .Where(f => hiddenFolders.Any(d => string.Equals(d.Name, System.IO.Path.GetFileNameWithoutExtension(f.Name), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var launchers = hiddenFolders.Count > 0 || hiddenScripts.Count > 0 ? shortcuts.Where(LaunchesScript).ToList() : [];
        threats.AddRange(disguised.Union(launchers).Select(f => new UsbThreat(f.FullName, UsbThreatKind.Raccourci)));
        threats.AddRange(hiddenScripts.Select(f => new UsbThreat(f.FullName, UsbThreatKind.Script)));

        var autorun = files.FirstOrDefault(f => string.Equals(f.Name, "autorun.inf", StringComparison.OrdinalIgnoreCase));
        if (autorun is not null && LaunchesHiddenOrScript(autorun, files))
        {
            threats.Add(new UsbThreat(autorun.FullName, UsbThreatKind.Autorun));
        }

        // Les dossiers cachés ne comptent que si la clé est infectée : un dossier caché seul est permis.
        if (threats.Count > 0)
        {
            threats.AddRange(hiddenFolders.Select(d => new UsbThreat(d.FullName, UsbThreatKind.DossierCache)));
        }

        return threats;
    }

    /// <summary>Le raccourci désigne un interpréteur ou un script : on cherche ces noms dans le fichier, en ASCII et en UTF-16.</summary>
    internal static bool LaunchesScript(FileInfo shortcut) => shortcut.Length <= 65_536 && ContainsPayload(File.ReadAllBytes(shortcut.FullName));

    internal static bool ContainsPayload(byte[] bytes)
    {
        var text = (Encoding.Latin1.GetString(bytes) + "\n" + Encoding.Unicode.GetString(bytes)).ToLowerInvariant();
        return ShortcutPayloads.Any(p => text.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// autorun.inf qui lance un script, ou un fichier caché de la clé. Un ancien installeur qui lance
    /// « setup.exe » visible n'est pas une infection (et Windows n'exécute plus l'autorun des clés).
    /// </summary>
    private static bool LaunchesHiddenOrScript(FileInfo autorun, IReadOnlyList<FileInfo> files)
    {
        if (autorun.Length > 65_536)
        {
            return false;
        }

        foreach (var line in File.ReadAllLines(autorun.FullName).Select(l => l.Trim()))
        {
            var separator = line.IndexOf('=');
            var key = separator > 0 ? line[..separator].Trim().ToLowerInvariant() : "";
            if (key is not ("open" or "shellexecute") && !key.StartsWith("shell\\", StringComparison.Ordinal))
            {
                continue;
            }

            var target = line[(separator + 1)..].Trim().Trim('"').Split(' ')[0];
            var name = System.IO.Path.GetFileName(target);
            if (ScriptExtensions.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant())
                || ContainsPayload(System.Text.Encoding.Latin1.GetBytes(line))
                || files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && f.Attributes.HasFlag(FileAttributes.Hidden)))
            {
                return true;
            }
        }

        return false;
    }
}
