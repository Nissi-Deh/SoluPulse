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
        foreach (var file in entries.OfType<FileInfo>())
        {
            var extension = file.Extension.ToLowerInvariant();
            var hidden = file.Attributes.HasFlag(FileAttributes.Hidden);
            if (extension == ".lnk" && (LaunchesScript(file) || hiddenFolders.Any(d => string.Equals(d.Name, System.IO.Path.GetFileNameWithoutExtension(file.Name), StringComparison.OrdinalIgnoreCase))))
            {
                threats.Add(new UsbThreat(file.FullName, UsbThreatKind.Raccourci));
            }
            else if (hidden && ScriptExtensions.Contains(extension))
            {
                threats.Add(new UsbThreat(file.FullName, UsbThreatKind.Script));
            }
            else if (string.Equals(file.Name, "autorun.inf", StringComparison.OrdinalIgnoreCase) && LaunchesProgram(file))
            {
                threats.Add(new UsbThreat(file.FullName, UsbThreatKind.Autorun));
            }
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

    private static bool LaunchesProgram(FileInfo autorun)
    {
        if (autorun.Length > 65_536)
        {
            return true;
        }

        return File.ReadAllLines(autorun.FullName)
            .Select(l => l.Trim().ToLowerInvariant())
            .Any(l => l.StartsWith("open", StringComparison.Ordinal) || l.StartsWith("shellexecute", StringComparison.Ordinal) || l.StartsWith("shell\\", StringComparison.Ordinal));
    }
}
