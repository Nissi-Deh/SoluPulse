using LibreHardwareMonitor.Hardware;
using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Températures CPU, GPU et disques, vitesse des ventilateurs (EF-COL-02), via LibreHardwareMonitorLib.
/// Cette version de la bibliothèque passe par le pilote PawnIO ; l'ancien pilote WinRing0, détecté par
/// Defender, n'est jamais embarqué (ENF-SEC-07). Sans PawnIO, les capteurs du processeur et des
/// ventilateurs de la carte mère sont absents : la collecte continue et le manque est signalé.
/// </summary>
public sealed class HardwareSensorsCollector(TimeProvider timeProvider) : ICollector, IDisposable
{
    private static readonly string[] PreferredCpuSensors = ["CPU Package", "Core (Tctl/Tdie)", "Core Max", "CPU Die (average)"];

    private readonly object _lock = new();
    private Computer? _computer;

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
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            var core = sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                            if (core is not null)
                            {
                                result.Add($"gpu.{MetricName.Segment(hardware.Name)}.temperature_c", core.Value!.Value, "°C");
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
            };
            _computer.Open();
        }

        return _computer;
    }
}
