using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Indicateurs de fiabilité des disques SATA et NVMe (EF-COL-03) : état de santé Windows, compteurs de
/// fiabilité du stockage (usure, heures, erreurs) et attributs SMART des disques ATA (secteurs
/// réalloués, en attente, irrécupérables). Collecte espacée : ces requêtes WMI sont coûteuses.
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
            foreach (var disk in Wmi.Query(StorageScope, "SELECT DeviceId, FriendlyName, HealthStatus, MediaType, BusType, Size, SerialNumber FROM MSFT_PhysicalDisk"))
            {
                var id = Wmi.Get<string>(disk, "DeviceId") ?? "";
                var name = Wmi.Get<string>(disk, "FriendlyName") ?? $"Disque {id}";
                var info = new DiskInfo(name, Wmi.Get<ushort>(disk, "MediaType"), Wmi.Get<ushort>(disk, "BusType"), Wmi.Get<ulong>(disk, "Size"));
                disks[id] = info;

                // HealthStatus : 0 sain, 1 avertissement, 2 défaillant, 5 inconnu.
                var health = Wmi.Get<ushort>(disk, "HealthStatus");
                if (health <= 2)
                {
                    result.Add($"disque_physique.{info.Metric}.etat_sante", health, "");
                }
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

                // Wear n'a de sens que pour les SSD (MediaType 4) et le NVMe (BusType 17).
                if (disk.MediaType == 4 || disk.BusType == 17)
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
            }
        });

        result.Snapshot("disques", disks.Values.Select(d => new
        {
            d.Name,
            Type = d.MediaType switch { 3 => "HDD", 4 => "SSD", _ => "Inconnu" },
            Bus = d.BusType == 17 ? "NVMe" : d.BusType == 11 ? "SATA" : d.BusType == 7 ? "USB" : d.BusType.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TailleGo = Math.Round(d.Size / 1e9, 0),
        }));
        return Task.FromResult(result.Build());
    }

    private static void AddIfPresent(CollectorResultBuilder result, System.Management.ManagementBaseObject item, string property, string metric, string unit)
    {
        if (item[property] is { } value)
        {
            result.Add(metric, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), unit);
        }
    }

    private static void AddAttribute(CollectorResultBuilder result, IReadOnlyDictionary<byte, long> attributes, byte id, string metric)
    {
        if (attributes.TryGetValue(id, out var value))
        {
            result.Add(metric, value, "");
        }
    }

    private sealed record DiskInfo(string Name, ushort MediaType, ushort BusType, ulong Size)
    {
        public string Metric { get; } = MetricName.Segment(Name);
    }
}

/// <summary>Lecture du tableau d'attributs SMART renvoyé par MSStorageDriver_FailurePredictData.</summary>
public static class SmartAttributes
{
    public const byte ReallocatedSectors = 5;
    public const byte PendingSectors = 197;
    public const byte UncorrectableSectors = 198;

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
