using System.Globalization;
using System.Management;
using System.Xml.Linq;
using Microsoft.Win32;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Ce qui permet de se relever d'une panne, et ce qui expire : protection du système et points de
/// restauration, environnement de récupération, clé de récupération BitLocker, sauvegardes, état du
/// magasin des composants, fin de support de Windows, certificats Secure Boot 2023, âge du BIOS,
/// microcode des processeurs Intel instables.
/// </summary>
public sealed class ResilienceCollector(TimeProvider timeProvider) : ICollector
{
    private const string CimScope = @"\\.\root\cimv2";
    private const string BitLockerScope = @"\\.\root\cimv2\Security\MicrosoftVolumeEncryption";

    // Logiciels de sauvegarde reconnus dans la liste des programmes installés.
    private static readonly string[] BackupSoftware =
    [
        "Google Drive", "Dropbox", "Macrium Reflect", "Acronis", "Veeam Agent", "AOMEI Backupper", "EaseUS Todo Backup",
        "Backblaze", "IDrive", "Cobian", "Duplicati", "pCloud", "MEGAsync", "Paragon Backup",
    ];

    public string Name => "protection";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var now = timeProvider.GetUtcNow();
        var snapshot = new Dictionary<string, object?>();

        result.Try("protection_systeme", () =>
        {
            var protection = RestorePointService.Protection();
            result.Add("systeme.protection_systeme", (int)protection, "");
            snapshot["ProtectionSysteme"] = protection.ToString();
            if (protection == SystemProtection.Active && RestorePointService.TryList() is { } points)
            {
                result.Add("systeme.points_restauration", points.Count, "");
            }
        });

        result.Try("winre", () =>
        {
            var file = Path.Combine(Environment.SystemDirectory, "Recovery", "ReAgent.xml");
            if (File.Exists(file) && RecoveryEnvironmentEnabled(XDocument.Load(file)) is { } enabled)
            {
                result.Add("systeme.environnement_recuperation_actif", enabled ? 1 : 0, "");
                snapshot["EnvironnementRecuperation"] = enabled;
            }
        });

        result.Try("bitlocker_cle", () =>
        {
            var systemDrive = Drives().System;
            var volume = Wmi.Query(BitLockerScope, "SELECT DeviceID, DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume")
                .OfType<ManagementObject>()
                .FirstOrDefault(v => string.Equals(Wmi.Get<string>(v, "DriveLetter"), systemDrive, StringComparison.OrdinalIgnoreCase));
            if (volume is null || Wmi.Get<uint>(volume, "ProtectionStatus") != 1)
            {
                return;
            }

            // Type 3 : mot de passe de récupération de 48 chiffres. Sans lui, un changement de pièce ou une
            // mise à jour du BIOS peut rendre le disque illisible pour toujours.
            using var parameters = volume.GetMethodParameters("GetKeyProtectors");
            parameters["KeyProtectorType"] = 3u;
            using var output = volume.InvokeMethod("GetKeyProtectors", parameters, null);
            var hasRecovery = output?["VolumeKeyProtectorID"] is string[] { Length: > 0 };
            result.Add("securite.bitlocker.cle_recuperation", hasRecovery ? 1 : 0, "");
            snapshot["CleRecuperationBitLocker"] = hasRecovery;
        });

        result.Try("sauvegardes", () =>
        {
            var found = new List<string>();
            double? fileHistoryDays = null;
            foreach (var profile in UserProfiles.All())
            {
                var config = Path.Combine(profile.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration");
                if (Directory.Exists(config) && Directory.EnumerateFiles(config, "Config*.xml").Any())
                {
                    found.Add("Historique des fichiers");
                    var catalogs = Directory.EnumerateFiles(config, "Catalog*.edb").Select(File.GetLastWriteTimeUtc).ToList();
                    if (catalogs.Count > 0)
                    {
                        var days = Math.Floor((now.UtcDateTime - catalogs.Max()).TotalDays);
                        fileHistoryDays = fileHistoryDays is { } d ? Math.Min(d, days) : days;
                    }
                }

                using var shell = profile.OpenKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (shell?.GetValue("Personal", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string documents
                    && documents.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add("OneDrive (dossier Documents)");
                }
            }

            found.AddRange(InstalledPrograms.Names().Where(n => BackupSoftware.Any(b => n.Contains(b, StringComparison.OrdinalIgnoreCase))));
            var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Add("donnees.sauvegarde_detectee", distinct.Count > 0 ? 1 : 0, "");
            if (fileHistoryDays is { } last)
            {
                result.Add("donnees.historique_fichiers_jours", last, "jours");
            }

            snapshot["Sauvegardes"] = distinct;
        });

        result.Try("magasin_composants", () =>
        {
            if (DismHealth.Check() is { } state)
            {
                result.Add("systeme.magasin_composants_etat", state, "");
            }
        });

        result.Try("fin_support", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = int.TryParse(key?.GetValue("CurrentBuild") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
            var edition = key?.GetValue("EditionID") as string ?? "";
            var esu = build == 19045 && HasEsuLicense();
            if (SupportCalendar.EndOfSupport(build, edition, esu) is { } end)
            {
                var days = end.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;
                result.Add("systeme.jours_avant_fin_support", days, "jours");
                snapshot["FinSupport"] = end.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
            }

            snapshot["Build"] = build;
            snapshot["Edition"] = edition;
            if (build == 19045)
            {
                snapshot["Esu"] = esu;
            }
        });

        result.Try("secure_boot_2023", () =>
        {
            using var state = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (state?.GetValue("UEFISecureBootEnabled") is not 1)
            {
                return;
            }

            using var servicing = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing");
            var status = servicing?.GetValue("UEFICA2023Status") as string;
            result.Add("securite.secure_boot_certificats_2023", string.Equals(status, "Updated", StringComparison.OrdinalIgnoreCase) ? 1 : 0, "");
            snapshot["CertificatsSecureBoot2023"] = status ?? "absent";
        });

        DateTime? biosDate = null;
        result.Try("bios", () =>
        {
            var bios = Wmi.QueryFirst(CimScope, "SELECT ReleaseDate FROM Win32_BIOS");
            if (bios?["ReleaseDate"] is string text && text.Length >= 8
                && DateTime.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                biosDate = date;
                result.Add("materiel.bios_age_jours", Math.Floor((now.UtcDateTime - date).TotalDays), "jours");
            }
        });

        result.Try("microcode", () =>
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = cpu?.GetValue("ProcessorNameString") as string ?? "";
            if (!SupportCalendar.IsUnstableIntelGeneration(name))
            {
                return;
            }

            // « Update Revision » : 8 octets, la révision du microcode dans les 4 derniers. Sans valeur
            // lisible, la date du BIOS tranche : le correctif est sorti en septembre 2024.
            var revision = cpu?.GetValue("Update Revision") is byte[] { Length: 8 } raw ? BitConverter.ToUInt32(raw, 4) : (uint?)null;
            var atRisk = revision is { } r ? r < SupportCalendar.FixedIntelMicrocode : biosDate is { } d && d < new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            result.Add("cpu.microcode_instable", atRisk ? 1 : 0, "");
            snapshot["Microcode"] = revision is { } value ? $"0x{value:X}" : "illisible";
        });

        result.Snapshot("protection", snapshot);
        return Task.FromResult(result.Build());
    }

    /// <summary>InstallState 1 : l'environnement de récupération est prêt à démarrer.</summary>
    internal static bool? RecoveryEnvironmentEnabled(XDocument reAgent) =>
        reAgent.Root?.Element("InstallState")?.Attribute("state")?.Value switch
        {
            "1" => true,
            "0" => false,
            _ => null,
        };

    private static (string System, string Letter) Drives()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return (root.TrimEnd('\\'), root[..1]);
    }

    /// <summary>Licence ESU de Windows 10 activée (programme grand public ou entreprise).</summary>
    private static bool HasEsuLicense() =>
        Wmi.Query(CimScope, "SELECT Name, LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL", TimeSpan.FromSeconds(60))
            .Any(p => (Wmi.Get<string>(p, "Name") ?? "").Contains("ESU", StringComparison.OrdinalIgnoreCase) && Wmi.Get<uint>(p, "LicenseStatus") == 1);
}

/// <summary>Noms des programmes installés, lus dans les clés de désinstallation de Windows.</summary>
public static class InstalledPrograms
{
    public sealed record Program(string Name, string? Version, string? Publisher);

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
                if (key?.GetValue("DisplayName") is string display && display.Length > 0 && key.GetValue("SystemComponent") is not 1)
                {
                    programs.Add(new Program(display.Trim(), key.GetValue("DisplayVersion") as string, key.GetValue("Publisher") as string));
                }
            }
        }
    }
}
