using System.Diagnostics;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Suit les programmes d'un passage à l'autre : un programme inconnu qui fait travailler le
/// processeur pendant que personne n'utilise le PC (cryptomineur caché), et un programme dont la
/// mémoire grossit sans arrêt (fuite de mémoire).
/// </summary>
public sealed class ProcessActivityCollector(TimeProvider timeProvider) : ICollector
{
    private const int LeakWindow = 13;

    private readonly Dictionary<int, Tracked> _tracked = [];
    private readonly Dictionary<string, SignatureState> _signatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public string Name => "programmes_actifs";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        result.Try("activite", () =>
        {
            lock (_lock)
            {
                Sample(result);
            }
        });

        return Task.FromResult(result.Build());
    }

    private void Sample(CollectorResultBuilder result)
    {
        var now = timeProvider.GetUtcNow();
        var unattended = ConsoleSession.IsUnattended() ?? false;
        var seen = new HashSet<int>();
        (double Cpu, string Name, string Path)? miner = null;
        (double Rate, string Name)? leak = null;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4 || !TryRead(process, out var cpu, out var privateBytes, out var started))
                {
                    continue;
                }

                seen.Add(process.Id);
                if (!_tracked.TryGetValue(process.Id, out var tracked) || tracked.Started != started)
                {
                    _tracked[process.Id] = new Tracked(process.ProcessName, started, cpu, now, new Queue<(DateTimeOffset, long)>([(now, privateBytes)]));
                    continue;
                }

                var elapsed = (now - tracked.LastSample).TotalSeconds;
                var share = elapsed > 0 ? (cpu - tracked.Cpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100 : 0;
                tracked.Cpu = cpu;
                tracked.LastSample = now;
                tracked.Memory.Enqueue((now, privateBytes));
                while (tracked.Memory.Count > LeakWindow)
                {
                    tracked.Memory.Dequeue();
                }

                if (unattended && share >= 40 && (miner is null || share > miner.Value.Cpu) && ImagePath(process) is { } path && IsUnknown(path))
                {
                    miner = (Math.Round(share, 0), process.ProcessName, path);
                }

                if (LeakRate(tracked.Memory) is { } rate && (leak is null || rate > leak.Value.Rate))
                {
                    leak = (rate, process.ProcessName);
                }
            }
        }

        foreach (var gone in _tracked.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _tracked.Remove(gone);
        }

        result.Add("securite.processeur_occupe_inactif_pct", miner?.Cpu ?? 0, "%");
        result.Add("memoire.fuite_processus_go_h", leak?.Rate ?? 0, "Go/h");
        result.Snapshot("programmes_actifs", new
        {
            PcInutilise = unattended,
            ProgrammeSuspect = miner is { } m ? new { m.Name, Chemin = m.Path, ProcesseurPct = m.Cpu } : null,
            FuiteMemoire = leak is { } l ? new { l.Name, GoParHeure = l.Rate } : null,
        });
    }

    /// <summary>
    /// Une fuite : sur au moins 55 minutes, la mémoire privée ne redescend jamais de plus de 3 %, a
    /// gagné plus de 1 Go et dépasse 1,5 Go. Renvoie la croissance en Go par heure.
    /// </summary>
    internal static double? LeakRate(IReadOnlyCollection<(DateTimeOffset Time, long Bytes)> samples)
    {
        if (samples.Count < 3)
        {
            return null;
        }

        var list = samples.ToList();
        var span = list[^1].Time - list[0].Time;
        var growth = list[^1].Bytes - list[0].Bytes;
        if (span < TimeSpan.FromMinutes(55) || growth < 1L << 30 || list[^1].Bytes < 1.5 * (1L << 30))
        {
            return null;
        }

        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Bytes < list[i - 1].Bytes * 0.97)
            {
                return null;
            }
        }

        return Math.Round(growth / (double)(1L << 30) / span.TotalHours, 1);
    }

    private bool IsUnknown(string path)
    {
        if (ProgramPaths.IsWindows(path))
        {
            return false;
        }

        if (!_signatures.TryGetValue(path, out var signature))
        {
            signature = Authenticode.Check(path);
            _signatures[path] = signature;
        }

        return signature is SignatureState.NonSigne or SignatureState.Invalide || (ProgramPaths.IsUserWritable(path) && signature != SignatureState.Valide);
    }

    private static string? ImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryRead(Process process, out TimeSpan cpu, out long privateBytes, out DateTime started)
    {
        try
        {
            cpu = process.TotalProcessorTime;
            privateBytes = process.PrivateMemorySize64;
            started = process.StartTime;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            cpu = default;
            privateBytes = 0;
            started = default;
            return false;
        }
    }

    private sealed class Tracked(string name, DateTime started, TimeSpan cpu, DateTimeOffset lastSample, Queue<(DateTimeOffset, long)> memory)
    {
        public string Name { get; } = name;

        public DateTime Started { get; } = started;

        public TimeSpan Cpu { get; set; } = cpu;

        public DateTimeOffset LastSample { get; set; } = lastSample;

        public Queue<(DateTimeOffset Time, long Bytes)> Memory { get; } = memory;
    }
}
