using System.Globalization;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Actions;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Indicateurs de fiabilité des disques SATA et NVMe (EF-COL-03) : état de santé Windows, compteurs de
/// fiabilité du stockage (usure, heures, erreurs), attributs SMART des disques ATA (secteurs
/// réalloués, en attente, irrécupérables, erreurs non corrigées, erreurs de câble), page de santé
/// NVMe (réserve, avertissement critique, erreurs média) et firmwares connus comme dangereux.
/// Collecte espacée : ces requêtes WMI sont coûteuses.
/// </summary>
public sealed class StorageHealthCollector(TimeProvider timeProvider) : ICollector
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";
    private const string WmiScope = @"\\.\root\WMI";
    private const string CimScope = @"\\.\root\cimv2";

    public string Name => "sante_disques";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var disks = new Dictionary<string, DiskInfo>(StringComparer.OrdinalIgnoreCase);

        result.Try("msft_physicaldisk", () =>
        {
            foreach (var disk in Wmi.Query(StorageScope, "SELECT DeviceId, FriendlyName, HealthStatus, MediaType, BusType, Size, FirmwareVersion FROM MSFT_PhysicalDisk"))
            {
                var id = Wmi.Get<string>(disk, "DeviceId") ?? "";
                var name = Wmi.Get<string>(disk, "FriendlyName") ?? $"Disque {id}";
                var firmware = (Wmi.Get<string>(disk, "FirmwareVersion") ?? "").Trim();
                var info = new DiskInfo(name, Wmi.Get<ushort>(disk, "MediaType"), Wmi.Get<ushort>(disk, "BusType"), Wmi.Get<ulong>(disk, "Size"), firmware);
                disks[id] = info;

                // HealthStatus : 0 sain, 1 avertissement, 2 défaillant, 5 inconnu.
                var health = Wmi.Get<ushort>(disk, "HealthStatus");
                if (health <= 2)
                {
                    result.Add($"disque_physique.{info.Metric}.etat_sante", health, "");
                }

                if (firmware.Length > 0)
                {
                    result.Add($"disque_physique.{info.Metric}.firmware_a_risque", RiskyFirmware.Find(name, firmware) is null ? 0 : 1, "");
                }
            }
        });

        // Page de santé NVMe : plus précise que les compteurs de Windows, et lisible derrière la plupart
        // des contrôleurs Intel RST. Les disques SATA répondent par une erreur : on passe.
        result.Try("nvme", () =>
        {
            foreach (var (id, disk) in disks)
            {
                if (disk.MediaType == 3 || !int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    || DeviceIo.ReadNvmeHealth(number) is not { } health)
                {
                    continue;
                }

                var prefix = $"disque_physique.{disk.Metric}";
                disk.Nvme = health;
                result.Add($"{prefix}.usure_pct", health.PercentageUsed, "%");
                // Une réserve et un seuil à 0 : le disque ne gère pas ce compteur.
                if (health.AvailableSparePercent <= 100 && (health.AvailableSparePercent > 0 || health.AvailableSpareThresholdPercent > 0))
                {
                    result.Add($"{prefix}.reserve_pct", health.AvailableSparePercent, "%");
                }

                result.Add($"{prefix}.avertissement_critique", NvmeHealth.IsCritical(health.CriticalWarning) ? 1 : 0, "");
                result.Add($"{prefix}.erreurs_media", health.MediaErrors, "");
                result.Add($"{prefix}.arrets_non_securises", health.UnsafeShutdowns, "");
            }
        });

        result.Try("msft_storagereliabilitycounter", () =>
        {
            foreach (var counter in Wmi.Query(StorageScope, "SELECT DeviceId, Wear, ReadErrorsUncorrected, WriteErrorsUncorrected, PowerOnHours FROM MSFT_StorageReliabilityCounter"))
            {
                if (!disks.TryGetValue(Wmi.Get<string>(counter, "DeviceId") ?? "", out var disk))
                {
                    continue;
                }

                // Wear n'a de sens que pour les SSD (MediaType 4) et le NVMe (BusType 17). La page NVMe,
                // quand elle est lisible, donne déjà l'usure.
                if ((disk.MediaType == 4 || disk.BusType == 17) && disk.Nvme is null)
                {
                    result.Add($"disque_physique.{disk.Metric}.usure_pct", Wmi.Get<byte>(counter, "Wear"), "%");
                }

                AddIfPresent(result, counter, "PowerOnHours", $"disque_physique.{disk.Metric}.heures_fonctionnement", "h");
                AddIfPresent(result, counter, "ReadErrorsUncorrected", $"disque_physique.{disk.Metric}.erreurs_lecture_non_corrigees", "");
                AddIfPresent(result, counter, "WriteErrorsUncorrected", $"disque_physique.{disk.Metric}.erreurs_ecriture_non_corrigees", "");
            }
        });

        // SMART des disques ATA : les disques NVMe ne passent pas par ces classes.
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        result.Try("win32_diskdrive", () =>
        {
            foreach (var drive in Wmi.Query(CimScope, "SELECT Model, PNPDeviceID FROM Win32_DiskDrive"))
            {
                models[Wmi.Get<string>(drive, "PNPDeviceID") ?? ""] = Wmi.Get<string>(drive, "Model") ?? "";
            }
        });

        string MetricFor(string instanceName)
        {
            var pnp = instanceName.EndsWith("_0", StringComparison.Ordinal) ? instanceName[..^2] : instanceName;
            return MetricName.Segment(models.TryGetValue(pnp, out var model) && model.Length > 0 ? model : pnp);
        }

        result.Try("smart.prediction", () =>
        {
            foreach (var status in Wmi.Query(WmiScope, "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
            {
                var metric = MetricFor(Wmi.Get<string>(status, "InstanceName") ?? "");
                result.Add($"disque_physique.{metric}.prediction_panne", Wmi.Get<bool>(status, "PredictFailure") ? 1 : 0, "");
            }
        });

        // Pour un disque NVMe, Windows simule des attributs SMART à partir de la page de santé : les
        // erreurs média y deviennent l'attribut 187. La page NVMe fait foi, sans doublon.
        var nvmeDisks = disks.Values.Where(d => d.Nvme is not null).Select(d => d.Metric).ToHashSet(StringComparer.Ordinal);

        result.Try("smart.attributs", () =>
        {
            foreach (var data in Wmi.Query(WmiScope, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"))
            {
                var metric = MetricFor(Wmi.Get<string>(data, "InstanceName") ?? "");
                if (data["VendorSpecific"] is not byte[] raw)
                {
                    continue;
                }

                var attributes = SmartAttributes.Parse(raw);
                AddAttribute(result, attributes, SmartAttributes.ReallocatedSectors, $"disque_physique.{metric}.secteurs_realloues");
                AddAttribute(result, attributes, SmartAttributes.PendingSectors, $"disque_physique.{metric}.secteurs_en_attente");
                AddAttribute(result, attributes, SmartAttributes.UncorrectableSectors, $"disque_physique.{metric}.secteurs_irrecuperables");
                if (nvmeDisks.Contains(metric))
                {
                    // Comptées dans erreurs_media : 0 ici ferme une alerte ouverte par l'attribut simulé.
                    result.Add($"disque_physique.{metric}.erreurs_non_corrigees", 0, "");
                }
                else
                {
                    AddAttribute(result, attributes, SmartAttributes.ReportedUncorrectable, $"disque_physique.{metric}.erreurs_non_corrigees");
                    AddAttribute(result, attributes, SmartAttributes.CrcErrors, $"disque_physique.{metric}.erreurs_cable");
                }
            }
        });

        result.Try("disque_systeme", () =>
        {
            var letter = Drives.SystemRoot[0];

            // Windows sur un disque dur classique : la première cause de lenteur des PC de plus de 5 ans.
            var partition = Wmi.QueryFirst(StorageScope, $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'");
            if (partition is not null
                && disks.TryGetValue(Wmi.Get<uint>(partition, "DiskNumber").ToString(CultureInfo.InvariantCulture), out var system)
                && system.MediaType is 3 or 4)
            {
                result.Add("disque.systeme.disque_dur_classique", system.MediaType == 3 ? 1 : 0, "");

                // 1 disque dur, 2 SSD SATA, 3 SSD NVMe : la vitesse attendue au test du disque en dépend.
                result.Add("disque.systeme.type", system.MediaType == 3 ? 1 : system.Nvme is not null || system.BusType == 17 ? 3 : 2, "");
            }

            // Volume « sale » : Windows a noté une incohérence et relancera une vérification au démarrage.
            if (DeviceIo.IsVolumeDirty(letter) is { } dirty)
            {
                result.Add("disque.systeme.verification_en_attente", dirty ? 1 : 0, "");
            }
        });

        result.Snapshot("disques", disks.Values.Select(d => new
        {
            d.Name,
            Type = d.MediaType switch { 3 => "HDD", 4 => "SSD", _ => "Inconnu" },
            Bus = BusName(d.BusType),
            TailleGo = Math.Round(d.Size / 1e9, 0),
            d.Firmware,
            FirmwareARisque = RiskyFirmware.Find(d.Name, d.Firmware)?.Advice,
            ReservePct = d.Nvme?.AvailableSparePercent,
            ErreursMedia = d.Nvme?.MediaErrors,
            ArretsNonSecurises = d.Nvme?.UnsafeShutdowns,
        }));
        return Task.FromResult(result.Build());
    }

    /// <summary>Valeurs de MSFT_PhysicalDisk.BusType ; « RAID » couvre aussi les disques derrière Intel RST.</summary>
    internal static string? BusName(ushort busType) => busType switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "carte SD",
        13 => "carte MMC",
        17 => "NVMe",
        _ => null,
    };

    private static void AddIfPresent(CollectorResultBuilder result, System.Management.ManagementBaseObject item, string property, string metric, string unit)
    {
        if (item[property] is { } value)
        {
            result.Add(metric, Convert.ToDouble(value, CultureInfo.InvariantCulture), unit);
        }
    }

    private static void AddAttribute(CollectorResultBuilder result, IReadOnlyDictionary<byte, long> attributes, byte id, string metric)
    {
        if (attributes.TryGetValue(id, out var value))
        {
            result.Add(metric, value, "");
        }
    }

    private sealed record DiskInfo(string Name, ushort MediaType, ushort BusType, ulong Size, string Firmware)
    {
        public string Metric { get; } = MetricName.Segment(Name);

        public NvmeHealth? Nvme { get; set; }
    }
}

/// <summary>Lecture du tableau d'attributs SMART renvoyé par MSStorageDriver_FailurePredictData.</summary>
public static class SmartAttributes
{
    public const byte ReallocatedSectors = 5;
    public const byte ReportedUncorrectable = 187;
    public const byte PendingSectors = 197;
    public const byte UncorrectableSectors = 198;

    /// <summary>Erreurs CRC Ultra DMA : presque toujours un câble SATA abîmé ou mal branché.</summary>
    public const byte CrcErrors = 199;

    /// <summary>
    /// Le tableau commence par 2 octets de version, suivis de 30 entrées de 12 octets :
    /// identifiant, 2 octets d'indicateurs, valeur, pire valeur, 6 octets de valeur brute, 1 octet réservé.
    /// </summary>
    public static IReadOnlyDictionary<byte, long> Parse(byte[] vendorSpecific)
    {
        var attributes = new Dictionary<byte, long>();
        for (var offset = 2; offset + 12 <= vendorSpecific.Length && offset < 2 + (30 * 12); offset += 12)
        {
            var id = vendorSpecific[offset];
            if (id == 0)
            {
                continue;
            }

            long raw = 0;
            for (var i = 5; i >= 0; i--)
            {
                raw = (raw << 8) | vendorSpecific[offset + 5 + i];
            }

            attributes[id] = raw;
        }

        return attributes;
    }
}

/// <summary>Un firmware de disque connu pour abîmer ou tuer le disque, et ce qu'il faut faire.</summary>
public sealed record RiskyFirmware(string Model, string Firmware, string Advice)
{
    /// <summary>
    /// Liste courte : seuls les défauts reconnus par le fabricant y figurent. Le modèle est cherché
    /// dans le nom du disque, le firmware comparé exactement.
    /// </summary>
    public static IReadOnlyList<RiskyFirmware> Known { get; } =
    [
        new("Samsung SSD 980 PRO", "3B2QGXA7", "Firmware 3B2QGXA7 : usure accélérée puis passage en lecture seule. Installez le firmware 5B2QGXA7 ou plus récent avec Samsung Magician."),
        new("Samsung SSD 990 PRO", "0B2QJXD7", "Firmware 0B2QJXD7 : l'état de santé chute rapidement. Installez le firmware 1B2QJXD7 ou plus récent avec Samsung Magician."),
    ];

    // SSD SAS de HPE : panne définitive à 32 768 heures de fonctionnement sans le firmware HPD8.
    private static readonly string[] HpeSasModels =
    [
        "VO0480JFDGT", "VO0960JFDGU", "VO1920JFDGV", "VO3840JFDHA", "MO0400JFFCF", "MO0800JFFCH", "MO1600JFFCK", "MO3200JFFCL",
        "VO000480JWDAR", "VO000960JWDAT", "VO001920JWDAU", "VO003840JWDAV", "VO007680JWCNK", "VO015300JWCNL",
        "VK000960JWSSQ", "VK001920JWSSR", "VK003840JWSST", "VK003840JWSSU", "VK007680JWSSV", "VO015360JWZJR",
    ];

    public static RiskyFirmware? Find(string diskName, string firmware)
    {
        firmware = firmware.Trim();
        var known = Known.FirstOrDefault(k => diskName.Contains(k.Model, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.Firmware, firmware, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            return known;
        }

        var hpe = HpeSasModels.Any(m => diskName.Contains(m, StringComparison.OrdinalIgnoreCase))
            && firmware.Length == 4
            && firmware.StartsWith("HPD", StringComparison.OrdinalIgnoreCase)
            && firmware[3] is >= '0' and < '8';
        return hpe
            ? new RiskyFirmware(diskName, firmware, "SSD HPE : panne définitive à 32 768 heures de fonctionnement sans le firmware HPD8. Mettez à jour le firmware sans attendre.")
            : null;
    }
}
