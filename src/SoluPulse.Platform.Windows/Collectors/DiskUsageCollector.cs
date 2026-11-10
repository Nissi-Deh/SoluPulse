using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Actions;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Ce qui occupe le disque système, par catégorie, avec pour chacune l'action de SoluPulse qui peut
/// libérer la place. Le parcours des dossiers est long : une fois par jour, au démarrage du service et
/// après chaque correction d'espace disque, jamais pendant le diagnostic complet.
/// </summary>
public sealed class DiskUsageCollector(TimeProvider timeProvider) : ICollector
{
    public const string CollectorName = "occupation_disque";
    private const double GiB = 1024d * 1024 * 1024;

    public string Name => CollectorName;

    public TimeSpan Interval => TimeSpan.FromHours(24);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var categories = new List<(string Key, string Label, long Bytes, string Advice)>();

        void Measure(string key, string label, string advice, Func<long> bytes) => result.Try(key, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            categories.Add((key, label, bytes(), advice));
        });

        var profiles = StartupInventory.UserProfiles();
        var folders = profiles.SelectMany(p => UserFolders.List(p.Sid, p.Path))
            .Where(f => Directory.Exists(f.Path) && Drives.IsOnSystemDrive(f.Path))
            .GroupBy(f => (f.Key, f.Label));
        foreach (var group in folders)
        {
            Measure(group.Key.Key, group.Key.Label, "Déplaçable vers un autre disque ; gros fichiers à trier", () => group.Sum(f => Files.SizeOf(f.Path)));
        }

        Measure("docker", "Docker (disque virtuel)", "Réductible ou déplaçable depuis Docker Desktop", () => DockerDiskGuide.Disks().Sum(d => d.Length));
        Measure("caches_dev", "Caches des outils de développement", "Supprimables : retéléchargés si besoin", () =>
            profiles.Sum(p => CleanDeveloperCachesAction.Caches.Sum(c => Files.SizeOf(Path.Combine(p.Path, c.RelativePath)))));
        Measure("installeurs_maj", "Installeurs de mises à jour d'applications", "Supprimables sans risque", () => CleanUpdateLeftoversAction.Folders().Sum(Files.SizeOf));
        Measure("veille_prolongee", "Fichier de veille prolongée", "Supprimé si la veille prolongée est désactivée", () => FileSize(DisableHibernationAction.HiberFile));
        Measure("fichier_echange", "Mémoire virtuelle (pagefile)", "Gérée par Windows : à laisser", () =>
            FileSize(Path.Combine(Drives.SystemRoot, "pagefile.sys")) + FileSize(Path.Combine(Drives.SystemRoot, "swapfile.sys")));
        Measure("windows_old", "Ancienne version de Windows (Windows.old)", "Supprimable avec le nettoyage de disque de Windows", () => Files.SizeOf(Path.Combine(Drives.SystemRoot, "Windows.old")));
        Measure("temporaires", "Fichiers temporaires", "Supprimables sans risque", () => Files.Measure(Files.TempFolders()).Bytes);
        Measure("corbeille", "Corbeille", "À vider si rien n'est à récupérer", () => Files.SizeOf(Path.Combine(Drives.SystemRoot, "$Recycle.Bin")));
        Measure("cache_windows_update", "Cache de Windows Update", "Supprimable : retéléchargé si besoin", () =>
            Files.SizeOf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download")));

        foreach (var (key, _, bytes, _) in categories)
        {
            result.Add($"occupation.{key}_go", Math.Round(bytes / GiB, 2), "Go");
        }

        result.Snapshot(Name, categories
            .Where(c => c.Bytes >= 50L << 20)
            .OrderByDescending(c => c.Bytes)
            .Select(c => new { Cle = c.Key, Categorie = c.Label, Go = Math.Round(c.Bytes / GiB, 1), Conseil = c.Advice })
            .ToList());
        return result.Build();
    }, cancellationToken);

    private static long FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
