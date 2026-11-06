using LibreHardwareMonitor.Hardware;
using SoluPulse.Core.Collection;

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
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            AddGpu(result, hardware.Name, sensors);
                            break;
                        case HardwareType.Motherboard:
                            foreach (var temperature in sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 125))
                            {
                                result.Add($"carte_mere.{MetricName.Segment(temperature.Name)}.temperature_c", Math.Round(temperature.Value!.Value, 1), "°C");
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
                    result.Add("cpu.temperature_c", preferred ?? cpuTemperatures.Max(t => t.Value), "°C");
                }

                if (fans == 0)
                {
                    result.Issue("ventilateurs", "Aucun ventilateur lisible sur ce PC.");
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
