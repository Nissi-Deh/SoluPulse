using System.Diagnostics;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Utilisation CPU, mémoire et disque, et processus les plus gourmands (EF-COL-05). Le CPU est mesuré
/// par différence entre deux relevés de GetSystemTimes, ce qui ne coûte presque rien.
/// </summary>
public sealed class PerformanceCollector(TimeProvider timeProvider) : ICollector
{
    private const int TopCount = 5;

    private readonly Dictionary<int, (TimeSpan Cpu, string Name)> _previousProcesses = [];
    private (ulong Idle, ulong Kernel, ulong User)? _previousTimes;
    private long _previousProcessSample;
    private int _runs;

    public string Name => "performances";

    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);

        result.Try("cpu", () =>
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                throw new InvalidOperationException("GetSystemTimes a échoué.");
            }

            var current = (idle.Value, kernel.Value, user.Value);
            if (_previousTimes is { } previous)
            {
                var idleDelta = current.Item1 - previous.Idle;
                var totalDelta = (current.Item2 - previous.Kernel) + (current.Item3 - previous.User);
                if (totalDelta > 0)
                {
                    result.Add("cpu.utilisation_pct", Math.Round(100d * (totalDelta - idleDelta) / totalDelta, 1), "%");
                }
            }

            _previousTimes = current;
        });

        result.Try("memoire", () =>
        {
            var status = new NativeMethods.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                throw new InvalidOperationException("GlobalMemoryStatusEx a échoué.");
            }

            result.Add("memoire.utilisation_pct", status.MemoryLoad, "%");
            result.Add("memoire.disponible_mo", Math.Round(status.AvailPhys / 1048576d), "Mo");
            result.Add("memoire.totale_mo", Math.Round(status.TotalPhys / 1048576d), "Mo");
        });

        // Les compteurs WMI de performance disque sont plus coûteux : un relevé sur quatre.
        if (_runs++ % 4 == 0)
        {
            result.Try("disque.activite", () =>
            {
                var rows = Wmi.Query(@"\\.\root\cimv2", "SELECT PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name = '_Total'");
                if (rows.Count > 0)
                {
                    var idle = Math.Min(100d, Wmi.Get<ulong>(rows[0], "PercentIdleTime"));
                    result.Add("disque_activite.utilisation_pct", 100 - idle, "%");
                }
            });
        }

        result.Try("systeme.alimentation", () =>
        {
            if (NativeMethods.GetActivePowerScheme() is { } scheme)
            {
                result.Add("systeme.plan_economie_energie", scheme == PowerSchemes.PowerSaver ? 1 : 0, "");
            }
        });

        result.Add("systeme.jours_depuis_demarrage", Math.Round(Environment.TickCount64 / 86_400_000d, 2), "j");

        result.Try("processus", () => result.Snapshot("processus", TopProcesses()));
        return Task.FromResult(result.Build());
    }

    private object TopProcesses()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _previousProcessSample == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_previousProcessSample, now);
        _previousProcessSample = now;

        var samples = new List<(int Pid, string Name, double CpuPct, long MemoryMb)>();
        var current = new Dictionary<int, (TimeSpan, string)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var cpu = process.TotalProcessorTime;
                    var name = process.ProcessName;
                    current[process.Id] = (cpu, name);
                    var cpuPct = elapsed > TimeSpan.Zero && _previousProcesses.TryGetValue(process.Id, out var previous) && previous.Name == name
                        ? 100d * (cpu - previous.Cpu).TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount
                        : 0;
                    samples.Add((process.Id, name, Math.Round(Math.Max(0, cpuPct), 1), process.WorkingSet64 / 1048576));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Processus protégé ou terminé entre-temps.
                }
            }
        }

        _previousProcesses.Clear();
        foreach (var (pid, value) in current)
        {
            _previousProcesses[pid] = value;
        }

        // Seul le nom du programme est conservé, jamais sa ligne de commande (ENF-CONF-02).
        return new
        {
            ParCpu = samples.OrderByDescending(s => s.CpuPct).Take(TopCount).Select(s => new { Nom = s.Name, CpuPct = s.CpuPct, MemoireMo = s.MemoryMb }),
            ParMemoire = samples.OrderByDescending(s => s.MemoryMb).Take(TopCount).Select(s => new { Nom = s.Name, CpuPct = s.CpuPct, MemoireMo = s.MemoryMb }),
        };
    }
}
