using System.Globalization;
using Microsoft.Win32;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows;

/// <summary>Programmes installés, lus dans les clés de désinstallation de Windows (machine et utilisateurs).</summary>
public static class InstalledPrograms
{
    /// <param name="UninstallCommand">La commande officielle de désinstallation, celle qu'utilise « Applications installées ».</param>
    public sealed record Program(
        string Name,
        string? Version,
        string? Publisher,
        string? InstallLocation = null,
        string? DisplayIcon = null,
        long? SizeKb = null,
        DateOnly? InstallDate = null,
        string? UninstallCommand = null,
        string Key = "");

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public static IEnumerable<string> Names() => All().Select(p => p.Name);

    public static IReadOnlyList<Program> All()
    {
        var programs = new List<Program>();
        foreach (var path in UninstallKeys)
        {
            Read(Registry.LocalMachine.OpenSubKey(path), programs);
        }

        foreach (var profile in UserProfiles.All())
        {
            Read(profile.OpenKey(UninstallKeys[0]), programs);
        }

        return programs;
    }

    internal static DateOnly? ParseInstallDate(string? text) =>
        text is { Length: 8 } && DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static void Read(RegistryKey? root, List<Program> programs)
    {
        using (root)
        {
            if (root is null)
            {
                return;
            }

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is not string display || display.Length == 0 || key.GetValue("SystemComponent") is 1
                    || key.GetValue("ParentKeyName") is string)
                {
                    continue;
                }

                programs.Add(new Program(
                    display.Trim(),
                    key.GetValue("DisplayVersion") as string,
                    key.GetValue("Publisher") as string,
                    (key.GetValue("InstallLocation") as string)?.Trim().Trim('"'),
                    (key.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim().Trim('"'),
                    key.GetValue("EstimatedSize") is int size && size > 0 ? size : null,
                    ParseInstallDate(key.GetValue("InstallDate") as string),
                    key.GetValue("UninstallString") as string,
                    name));
            }
        }
    }
}
