using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SoluPulse.Platform.Windows;

public enum StartupLocation
{
    MachineRun,
    MachineRun32,
    UserRun,
    CommonStartupFolder,
    UserStartupFolder,
}

/// <summary>Un programme lancé au démarrage de Windows.</summary>
public sealed record StartupEntry(string Id, string Name, string Command, StartupLocation Location, string? UserSid, bool Enabled)
{
    /// <summary>
    /// Clé « StartupApproved » où l'Explorateur et le Gestionnaire des tâches enregistrent l'état
    /// activé/désactivé. SoluPulse utilise le même mécanisme : la commande d'origine n'est jamais modifiée.
    /// </summary>
    public (RegistryKey Root, string SubKey, string ValueName) ApprovalKey(RegistryKey users)
    {
        const string Base = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        return Location switch
        {
            StartupLocation.MachineRun => (Registry.LocalMachine, Base + "Run", Name),
            StartupLocation.MachineRun32 => (Registry.LocalMachine, Base + "Run32", Name),
            StartupLocation.CommonStartupFolder => (Registry.LocalMachine, Base + "StartupFolder", Name),
            StartupLocation.UserRun => (users, $@"{UserSid}\{Base}Run", Name),
            StartupLocation.UserStartupFolder => (users, $@"{UserSid}\{Base}StartupFolder", Name),
            _ => throw new InvalidOperationException(),
        };
    }
}

/// <summary>
/// Inventaire des programmes au démarrage (EF-COL-07) pour la machine et pour chaque utilisateur dont
/// la session est chargée. Le service tourne sous SYSTEM : les clés des utilisateurs sont lues dans
/// HKEY_USERS et leurs dossiers Démarrage via la liste des profils.
/// </summary>
public static class StartupInventory
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string UserStartupFolder = @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";

    public static IReadOnlyList<StartupEntry> List()
    {
        var entries = new List<StartupEntry>();
        ReadRun(entries, Registry.LocalMachine, RunKey, StartupLocation.MachineRun, null);
        ReadRun(entries, Registry.LocalMachine, Run32Key, StartupLocation.MachineRun32, null);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonStartupFolder, null);

        foreach (var sid in Registry.Users.GetSubKeyNames().Where(IsUserSid))
        {
            ReadRun(entries, Registry.Users, $@"{sid}\{RunKey}", StartupLocation.UserRun, sid);
            if (ProfilePath(sid) is { } profile)
            {
                ReadFolder(entries, Path.Combine(profile, UserStartupFolder), StartupLocation.UserStartupFolder, sid);
            }
        }

        return entries;
    }

    public static bool IsUserSid(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.EndsWith("_Classes", StringComparison.Ordinal);

    public static string? ProfilePath(string sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{sid}");
        return key?.GetValue("ProfileImagePath") as string;
    }

    // Une clé ou un dossier illisible (autre utilisateur, droits insuffisants) est ignoré : l'inventaire
    // continue avec ce qui est lisible.
    private static void ReadRun(List<StartupEntry> entries, RegistryKey root, string path, StartupLocation location, string? sid)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                var command = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                Add(entries, name, command, location, sid);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void ReadFolder(List<StartupEntry> entries, string folder, StartupLocation location, string? sid)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                Add(entries, Path.GetFileName(file), file, location, sid);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void Add(List<StartupEntry> entries, string name, string command, StartupLocation location, string? sid)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{location}|{sid}|{name}")))[..16];
        var entry = new StartupEntry(id, name, command, location, sid, Enabled: true);
        entries.Add(entry with { Enabled = IsEnabled(entry) });
    }

    private static bool IsEnabled(StartupEntry entry)
    {
        try
        {
            var (root, subKey, valueName) = entry.ApprovalKey(Registry.Users);
            using var key = root.OpenSubKey(subKey);
            // Premier octet pair : activé ; impair (03, 07…) : désactivé.
            return key?.GetValue(valueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
