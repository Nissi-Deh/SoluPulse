using System.Globalization;
using System.Management;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Inventaire de tous les composants du PC : modèle, carte mère, BIOS, processeur, barrettes de mémoire,
/// cartes graphiques, écrans, cartes réseau, audio, caméras, Bluetooth et imprimantes.
/// Aucun numéro de série ni adresse MAC n'est relevé (ENF-CONF-02).
/// </summary>
public sealed class HardwareInventoryCollector(TimeProvider timeProvider) : ICollector
{
    private const string CimScope = @"\\.\root\cimv2";
    private const string WmiScope = @"\\.\root\WMI";
    private const double GiB = 1024d * 1024 * 1024;

    // Périphériques logiciels (machines virtuelles, VPN, micros virtuels) : ce ne sont pas des composants.
    private static readonly string[] VirtualDevicePrefixes = [@"ROOT\", @"SWD\", "BTH", "{"];

    // Appareils Bluetooth associés (souris, casques, téléphones), hors services et profils.
    private static readonly string[] PairedBluetoothPrefixes = [@"BTHENUM\DEV_", @"BTHLE\DEV_"];

    // Imprimantes logicielles : PDF, OneNote, XPS, télécopie.
    private static readonly string[] VirtualPrinterPorts = ["PORTPROMPT:", "nul:", "SHRFAX:", "XPSPort:"];

    public string Name => "materiel";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var inventory = new Dictionary<string, object?>();

        result.Try("ordinateur", () =>
        {
            var system = Wmi.QueryFirst(CimScope, "SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem");
            var board = Wmi.QueryFirst(CimScope, "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            var bios = Wmi.QueryFirst(CimScope, "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            inventory["Ordinateur"] = new
            {
                Fabricant = Text(system, "Manufacturer"),
                Modele = Text(system, "Model"),
                Type = system is null ? null : Wmi.Get<ushort>(system, "PCSystemType") switch { 1 => "PC fixe", 2 => "Portable", 3 => "Station de travail", _ => null },
                CarteMere = Join(Text(board, "Manufacturer"), Text(board, "Product")),
                Bios = Join(Text(bios, "SMBIOSBIOSVersion"), DmtfDate(Text(bios, "ReleaseDate"))),
            };
        });

        result.Try("processeur", () =>
        {
            inventory["Processeurs"] = Wmi.Query(CimScope, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor")
                .Select(p => new
                {
                    Nom = Text(p, "Name"),
                    Coeurs = Wmi.Get<uint>(p, "NumberOfCores"),
                    Threads = Wmi.Get<uint>(p, "NumberOfLogicalProcessors"),
                    FrequenceMaxMhz = Wmi.Get<uint>(p, "MaxClockSpeed"),
                    CacheL2Ko = Wmi.Get<uint>(p, "L2CacheSize"),
                    CacheL3Ko = Wmi.Get<uint>(p, "L3CacheSize"),
                    Support = Text(p, "SocketDesignation"),
                })
                .ToList();
        });

        result.Try("memoire", () =>
        {
            var modules = Wmi.Query(CimScope, "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory")
                .Select(m => new
                {
                    Emplacement = Text(m, "DeviceLocator"),
                    TailleGo = Math.Round(Wmi.Get<ulong>(m, "Capacity") / GiB, 1),
                    FrequenceMhz = Wmi.Get<uint>(m, "ConfiguredClockSpeed") is var configured and > 0 ? configured : Wmi.Get<uint>(m, "Speed"),
                    Type = MemoryType(Wmi.Get<uint>(m, "SMBIOSMemoryType")),
                    Fabricant = Text(m, "Manufacturer"),
                    Reference = Text(m, "PartNumber"),
                })
                .ToList();
            var array = Wmi.QueryFirst(CimScope, "SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
            var slots = array is null ? 0 : Wmi.Get<uint>(array, "MemoryDevices");

            result.Add("memoire.barrettes", modules.Count, "");
            if (slots > 0)
            {
                result.Add("memoire.emplacements_libres", Math.Max(0, slots - modules.Count), "");
            }

            inventory["Memoire"] = new
            {
                TotaleGo = modules.Sum(m => m.TailleGo),
                Emplacements = slots,
                MaximumGo = array is null ? 0 : Math.Round(Wmi.Get<ulong>(array, "MaxCapacityEx") / 1048576d),
                // Des barrettes de tailles différentes empêchent souvent le double canal.
                TaillesIdentiques = modules.Select(m => m.TailleGo).Distinct().Count() <= 1,
                Barrettes = modules,
            };
        });

        result.Try("cartes_graphiques", () =>
        {
            inventory["CartesGraphiques"] = Wmi.Query(CimScope, "SELECT Name, PNPDeviceID, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, Status FROM Win32_VideoController")
                .Where(IsPhysical)
                .Select(g => new
                {
                    Nom = Text(g, "Name"),
                    Pilote = Join(Text(g, "DriverVersion"), DmtfDate(Text(g, "DriverDate"))),
                    Affichage = Wmi.Get<uint>(g, "CurrentHorizontalResolution") is var width and > 0
                        ? $"{width} × {Wmi.Get<uint>(g, "CurrentVerticalResolution")} à {Wmi.Get<uint>(g, "CurrentRefreshRate")} Hz"
                        : "inactive (économie d'énergie)",
                    Etat = Text(g, "Status"),
                })
                .ToList();
        });

        result.Try("ecrans", () =>
        {
            var screens = Wmi.Query(WmiScope, "SELECT ManufacturerName, UserFriendlyName, YearOfManufacture, Active FROM WmiMonitorID")
                .Where(s => Wmi.Get<bool>(s, "Active"))
                .Select(s => new
                {
                    Nom = Edid(s, "UserFriendlyName") is { Length: > 0 } name ? name : "Écran intégré",
                    Fabricant = Edid(s, "ManufacturerName"),
                    Annee = Wmi.Get<ushort>(s, "YearOfManufacture"),
                })
                .ToList();
            result.Add("ecrans.connectes", screens.Count, "");
            inventory["Ecrans"] = screens;
        });

        result.Try("cartes_reseau", () =>
        {
            inventory["CartesReseau"] = Wmi.Query(CimScope, "SELECT Name, PNPDeviceID, NetConnectionStatus, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE")
                .Where(IsPhysical)
                .Select(n => new
                {
                    Nom = Text(n, "Name"),
                    Etat = Wmi.Get<ushort>(n, "NetConnectionStatus") switch { 2 => "connectée", 7 => "déconnectée", 0 => "désactivée", _ => "autre" },
                    VitesseMbps = Wmi.Get<ulong>(n, "Speed") is var speed and > 0 and < 1_000_000_000_000 ? speed / 1_000_000 : (ulong?)null,
                })
                .ToList();
        });

        result.Try("audio", () =>
        {
            inventory["Audio"] = Wmi.Query(CimScope, "SELECT Name, PNPDeviceID, Status FROM Win32_SoundDevice")
                .Where(IsPhysical)
                .Select(a => new { Nom = Text(a, "Name"), Etat = Text(a, "Status") })
                .ToList();
        });

        result.Try("peripheriques", () =>
        {
            var all = Wmi.Query(CimScope, "SELECT Name, PNPClass, PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image' OR PNPClass = 'Bluetooth' OR PNPClass = 'Biometric'");
            var devices = all.Where(IsPhysical)
                .Select(d => new { Classe = Text(d, "PNPClass"), Nom = Text(d, "Name"), Etat = Text(d, "Status") })
                .ToList();
            inventory["Cameras"] = devices.Where(d => d.Classe is "Camera" or "Image").Select(d => new { d.Nom, d.Etat }).ToList();
            inventory["Bluetooth"] = devices.Where(d => d.Classe == "Bluetooth").Select(d => new { d.Nom, d.Etat }).ToList();
            inventory["AppareilsBluetooth"] = all
                .Where(d => Text(d, "PNPDeviceID") is { } id && PairedBluetoothPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Select(d => Text(d, "Name"))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCulture)
                .ToList();
            inventory["LecteursBiometriques"] = devices.Where(d => d.Classe == "Biometric").Select(d => new { d.Nom, d.Etat }).ToList();
        });

        result.Try("imprimantes", () =>
        {
            inventory["Imprimantes"] = Wmi.Query(CimScope, "SELECT Name, PortName, Default, WorkOffline FROM Win32_Printer")
                .Where(p => !VirtualPrinterPorts.Contains(Text(p, "PortName") ?? "", StringComparer.OrdinalIgnoreCase)
                    && Text(p, "Name")?.Contains("OneNote", StringComparison.OrdinalIgnoreCase) != true)
                .Select(p => new { Nom = Text(p, "Name"), ParDefaut = Wmi.Get<bool>(p, "Default"), HorsLigne = Wmi.Get<bool>(p, "WorkOffline") })
                .ToList();
        });

        result.Snapshot("materiel", inventory);
        return Task.FromResult(result.Build());
    }

    private static bool IsPhysical(ManagementBaseObject item) =>
        Text(item, "PNPDeviceID") is { } id && !VirtualDevicePrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string? Text(ManagementBaseObject? item, string property) =>
        item is null ? null : Wmi.Get<string>(item, property)?.Trim() is { Length: > 0 } text ? text : null;

    private static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } kept ? string.Join(" · ", kept) : null;

    /// <summary>Date WMI (« 20260505000000.000000+000 ») au format jour/mois/année.</summary>
    internal static string? DmtfDate(string? value) =>
        value is { Length: >= 8 } && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Texte EDID d'un écran : tableau de codes de caractères terminé par des zéros.</summary>
    private static string Edid(ManagementBaseObject item, string property) =>
        item[property] is ushort[] codes ? new string(codes.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";

    internal static string MemoryType(uint smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "inconnu",
    };
}
