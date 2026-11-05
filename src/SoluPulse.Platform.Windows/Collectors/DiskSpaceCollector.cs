using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>Espace libre des volumes fixes. Le volume système a son propre préfixe : <c>disque.systeme</c>.</summary>
public sealed class DiskSpaceCollector(TimeProvider timeProvider) : ICollector
{
    private const double GiB = 1024d * 1024 * 1024;

    public string Name => "espace_disque";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var volumes = new List<object>();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            result.Try(drive.Name, () =>
            {
                if (!drive.IsReady || drive.TotalSize == 0)
                {
                    return;
                }

                var isSystem = string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase);
                var prefix = isSystem ? "disque.systeme" : $"volume.{MetricName.Segment(drive.Name)}";
                var freePercent = 100d * drive.AvailableFreeSpace / drive.TotalSize;
                result.Add($"{prefix}.espace_libre_pct", Math.Round(freePercent, 1), "%");
                result.Add($"{prefix}.espace_libre_go", Math.Round(drive.AvailableFreeSpace / GiB, 2), "Go");
                result.Add($"{prefix}.taille_go", Math.Round(drive.TotalSize / GiB, 1), "Go");
                volumes.Add(new
                {
                    Lettre = drive.Name,
                    Systeme = isSystem,
                    Nom = drive.VolumeLabel,
                    drive.DriveFormat,
                    TailleGo = Math.Round(drive.TotalSize / GiB, 1),
                    LibreGo = Math.Round(drive.AvailableFreeSpace / GiB, 1),
                });
            });
        }

        result.Snapshot("volumes", volumes);
        return Task.FromResult(result.Build());
    }
}
