using LibreHardwareMonitor.Hardware;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Températures CPU, GPU, carte mère et disques, vitesse des ventilateurs (EF-COL-02), puissance et
/// fréquence du processeur, charge et mémoire des cartes graphiques, via LibreHardwareMonitorLib.
/// Cette version de la bibliothèque passe par le pilote PawnIO ; l'ancien pilote WinRing0, détecté par
/// Defender, n'est jamais embarqué (ENF-SEC-07). Sans PawnIO, les capteurs du processeur et des
/// ventilateurs de la carte mère sont absents : la collecte continue et le manque est signalé.
/// </summary>
public sealed class HardwareSensorsCollector(TimeProvider timeProvider) : ICollector, IDisposable
{
    private static readonly string[] PreferredCpuSensors = ["CPU Package", "Core (Tctl/Tdie)", "Core Max", "CPU Die (average)"];

    // Le tableau complet des capteurs est enregistré toutes les 5 minutes (un relevé sur dix).
    private const int SnapshotEvery = 10;

    private static readonly SensorType[] ListedSensorTypes = [SensorType.Temperature, SensorType.Fan, SensorType.Load, SensorType.Power, SensorType.Clock, SensorType.Voltage];

    private readonly object _lock = new();
    private Computer? _computer;
    private int _runs;
    private string? _manufacturer;

    public string Name => "capteurs";

    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        result.Try("bibliotheque", () =>
        {
            lock (_lock)
            {
                var computer = Open();
                var cpuTemperatures = new List<(string Name, float Value)>();
                float? cpuLoad = null;
                var fans = 0;
                var table = new List<object>();

                foreach (var hardware in computer.Hardware)
                {
                    hardware.Update();
                    foreach (var sub in hardware.SubHardware)
                    {
                        sub.Update();
                    }

                    var sensors = hardware.Sensors.Concat(hardware.SubHardware.SelectMany(s => s.Sensors))
                        .Where(s => s.Value is { } v && float.IsFinite(v))
                        .ToList();

                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            cpuTemperatures.AddRange(sensors.Where(s => s.SensorType == SensorType.Temperature).Select(s => (s.Name, s.Value!.Value)));
                            AddCpu(result, sensors);
                            cpuLoad = sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value;
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            AddGpu(result, hardware.Name, sensors);
                            break;
                        case HardwareType.Motherboard:
                            foreach (var temperature in sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125))
                            {
                                result.Add($"carte_mere.{MetricName.Segment(temperature.Name)}.temperature_c", Math.Round(temperature.Value!.Value, 1), "°C");
                            }

                            foreach (var voltage in sensors.Where(s => s.SensorType == SensorType.Voltage))
                            {
                                if (PowerRail.Deviation(voltage.Name, voltage.Value!.Value) is { } rail)
                                {
                                    result.Add($"alimentation.{rail.Metric}.tension_v", Math.Round(voltage.Value!.Value, 2), "V");
                                    result.Add($"alimentation.{rail.Metric}.ecart_pct", rail.DeviationPercent, "%");
                                }
                            }

                            break;
                        case HardwareType.Storage:
                            var diskTemperature = sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                            if (diskTemperature is not null)
                            {
                                result.Add($"disque_physique.{MetricName.Segment(hardware.Name)}.temperature_c", diskTemperature.Value!.Value, "°C");
                            }

                            break;
                    }

                    foreach (var fan in sensors.Where(s => s.SensorType == SensorType.Fan))
                    {
                        result.Add($"ventilateur.{MetricName.Segment(hardware.Name + " " + fan.Name)}.tours_min", fan.Value!.Value, "tr/min");
                        fans++;
                    }

                    table.Add(new
                    {
                        Composant = hardware.Name,
                        Type = TypeLabel(hardware.HardwareType),
                        Capteurs = sensors
                            .Where(s => ListedSensorTypes.Contains(s.SensorType))
                            .Select(s => new { Nom = s.Name, Mesure = SensorLabel(s.SensorType), Valeur = Math.Round(s.Value!.Value, 1), Unite = Unit(s.SensorType) })
                            .ToList(),
                    });
                }

                if (_runs++ % SnapshotEvery == 0)
                {
                    result.Snapshot("capteurs", table);
                }

                if (cpuTemperatures.Count == 0)
                {
                    result.Issue("cpu.temperature", "Température du processeur indisponible : le pilote PawnIO n'est peut-être pas installé.");
                }
                else
                {
                    var preferred = PreferredCpuSensors
                        .Select(name => cpuTemperatures.Where(t => t.Name == name).Select(t => (float?)t.Value).FirstOrDefault())
                        .FirstOrDefault(v => v is not null);
                    var temperature = preferred ?? cpuTemperatures.Max(t => t.Value);
                    result.Add("cpu.temperature_c", temperature, "°C");

                    // Température au repos : comparable d'un mois à l'autre, quel que soit l'usage du PC.
                    if (cpuLoad is < 15)
                    {
                        result.Add("cpu.temperature_repos_c", temperature, "°C");
                    }
                }

                if (fans == 0)
                {
                    var portable = computer.Hardware.Any(h => h.HardwareType == HardwareType.Battery);
                    result.Issue("ventilateurs", FanMessage(_manufacturer ??= Manufacturer(), portable, cpuTemperatures.Count > 0));
                }
            }
        });

        return Task.FromResult(result.Build());
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _computer?.Close();
            _computer = null;
        }
    }

    /// <summary>
    /// Explique pourquoi la vitesse du ventilateur manque. Sur la plupart des portables, le ventilateur est
    /// piloté par la puce de gestion du fabricant (contrôleur embarqué), que Windows ne permet pas de lire.
    /// </summary>
    internal static string FanMessage(string manufacturer, bool portable, bool cpuTemperatureReadable)
    {
        var brand = manufacturer.Length > 0 ? $" {manufacturer}" : "";
        var cause = portable
            ? $"Le ventilateur de ce portable{brand} fonctionne, mais il est piloté par une puce du fabricant qui ne communique pas sa vitesse à Windows. C'est le cas de la plupart des portables."
            : $"Les ventilateurs de ce PC{brand} sont branchés sur des prises qui ne mesurent pas leur vitesse, ou la carte mère ne la communique pas à Windows.";
        var fallback = cpuTemperatureReadable
            ? " SoluPulse surveille la température du processeur à la place : si le ventilateur s'encrasse ou tombe en panne, elle monte, et une alerte vous prévient."
            : " La température du processeur n'est pas lisible non plus : le pilote PawnIO n'est peut-être pas installé.";
        return cause + fallback;
    }

    /// <summary>Nom court du fabricant (« Hewlett-Packard » → « HP »), vide s'il est inconnu ou générique.</summary>
    internal static string ShortManufacturer(string? raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0 || name.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || name.Contains("To be filled", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return name.ToUpperInvariant() switch
        {
            "HEWLETT-PACKARD" or "HP" or "HP INC." => "HP",
            "DELL INC." or "DELL" => "Dell",
            "LENOVO" => "Lenovo",
            "ASUSTEK COMPUTER INC." => "Asus",
            "ACER" => "Acer",
            "MICRO-STAR INTERNATIONAL CO., LTD." => "MSI",
            "MICROSOFT CORPORATION" => "Microsoft",
            _ => name,
        };
    }

    private static string Manufacturer()
    {
        try
        {
            var system = Wmi.QueryFirst(@"\\.\root\cimv2", "SELECT Manufacturer FROM Win32_ComputerSystem");
            return ShortManufacturer(system is null ? null : Wmi.Get<string>(system, "Manufacturer"));
        }
        catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static void AddCpu(CollectorResultBuilder result, List<ISensor> sensors)
    {
        // Puissance nulle : compteur d'énergie pas encore lu (premier relevé) ou inaccessible.
        var package = sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        if (package is { Value: > 0 })
        {
            result.Add("cpu.puissance_w", Math.Round(package.Value!.Value, 1), "W");
        }

        // Fréquence moyenne des cœurs (« Core #1 », « P-Core #1 »…), hors horloge du bus.
        var clocks = sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value > 0).ToList();
        if (clocks.Count > 0)
        {
            result.Add("cpu.frequence_mhz", Math.Round(clocks.Average(s => s.Value!.Value)), "MHz");
        }
    }

    private static void AddGpu(CollectorResultBuilder result, string name, List<ISensor> sensors)
    {
        var prefix = $"gpu.{MetricName.Segment(name)}";
        var core = sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
        if (core is not null)
        {
            result.Add($"{prefix}.temperature_c", core.Value!.Value, "°C");
        }

        var load = sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase));
        if (load is not null)
        {
            result.Add($"{prefix}.utilisation_pct", Math.Round(load.Value!.Value, 1), "%");
        }

        var used = sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase));
        var total = sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase));
        if (used is not null && total is { Value: > 0 })
        {
            result.Add($"{prefix}.memoire_utilisee_pct", Math.Round(100 * used.Value!.Value / total.Value!.Value, 1), "%");
            result.Add($"{prefix}.memoire_totale_mo", Math.Round(total.Value!.Value), "Mo");
        }

        var power = sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("GPU Power", StringComparison.OrdinalIgnoreCase)));
        if (power is { Value: > 0 })
        {
            result.Add($"{prefix}.puissance_w", Math.Round(power.Value!.Value, 1), "W");
        }
    }

    private static string TypeLabel(HardwareType type) => type switch
    {
        HardwareType.Cpu => "Processeur",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Carte graphique",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Carte mère",
        HardwareType.Storage => "Stockage",
        HardwareType.Memory => "Mémoire",
        HardwareType.Battery => "Batterie",
        HardwareType.Network => "Réseau",
        HardwareType.Cooler => "Refroidissement",
        HardwareType.Psu => "Alimentation",
        _ => type.ToString(),
    };

    private static string SensorLabel(SensorType type) => type switch
    {
        SensorType.Temperature => "Température",
        SensorType.Fan => "Ventilateur",
        SensorType.Load => "Charge",
        SensorType.Power => "Puissance",
        SensorType.Clock => "Fréquence",
        SensorType.Voltage => "Tension",
        _ => type.ToString(),
    };

    private static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Fan => "tr/min",
        SensorType.Load => "%",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Voltage => "V",
        _ => "",
    };

    private Computer Open()
    {
        if (_computer is null)
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = true,
                IsBatteryEnabled = true,
            };
            _computer.Open();
        }

        return _computer;
    }
}

/// <summary>
/// Rails de l'alimentation d'un PC fixe, tels que la puce de la carte mère les nomme (« +12V »,
/// « +5V », « +3.3V », « 3VCC »). La norme ATX tolère 5 % d'écart. Une valeur hors de 50 à 150 % de
/// la tension nominale est une erreur d'échelle du capteur, pas une mesure : elle est ignorée.
/// </summary>
public static class PowerRail
{
    public sealed record Reading(string Metric, double Nominal, double DeviationPercent);

    public static Reading? Deviation(string sensorName, double value)
    {
        var name = sensorName.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (name.Contains("VSB", StringComparison.Ordinal) || name.Contains("STANDBY", StringComparison.Ordinal) || name.Contains("BAT", StringComparison.Ordinal))
        {
            return null;
        }

        (string Metric, double Nominal)? rail = name switch
        {
            _ when name.Contains("12V", StringComparison.Ordinal) => ("rail_12v", 12),
            _ when name.Contains("+5V", StringComparison.Ordinal) || name == "5V" || name == "5VCC" => ("rail_5v", 5),
            _ when name.Contains("3.3V", StringComparison.Ordinal) || name.Contains("3VCC", StringComparison.Ordinal) => ("rail_3v3", 3.3),
            _ => null,
        };
        if (rail is not { } r || value < r.Nominal * 0.5 || value > r.Nominal * 1.5)
        {
            return null;
        }

        return new Reading(r.Metric, r.Nominal, Math.Round(Math.Abs(value - r.Nominal) / r.Nominal * 100, 1));
    }
}
