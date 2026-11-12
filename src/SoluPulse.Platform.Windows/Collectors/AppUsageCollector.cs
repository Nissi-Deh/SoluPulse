using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>Un programme installé, avec la date de son dernier lancement connue de Windows.</summary>
public sealed record AppUsage(
    string Name,
    string? Publisher,
    string? Version,
    double? SizeMb,
    DateOnly? InstallDate,
    DateTimeOffset? LastUsed,
    bool Unused,
    string? UninstallCommand,
    string Key);

/// <summary>
/// Applications qui ne servent plus : la date de dernier lancement vient du dossier Prefetch de Windows
/// (un fichier par programme lancé, mis à jour à chaque lancement). Un programme jamais lancé depuis plus
/// de 6 mois, ou installé il y a plus de 3 mois sans jamais avoir été lancé, est « inutilisé ». Les
/// pilotes, composants de Windows, bibliothèques et antivirus ne sont jamais proposés.
/// </summary>
public sealed class AppUsageCollector(TimeProvider timeProvider) : ICollector
{
    public const string CollectorName = "applications";
    private static readonly TimeSpan UnusedAfter = TimeSpan.FromDays(180);
    private static readonly TimeSpan NewInstall = TimeSpan.FromDays(90);

    private static readonly string[] Excluded =
    [
        "Redistributable", "Runtime", ".NET", "Driver", "Pilote", "SDK", "Visual C++", "WebView2", "SoluPulse", "PawnIO",
        "Windows", "Microsoft Edge", "Update Health", "Chipset", "Intel(R)", "Intel ", "Realtek", "NVIDIA", "AMD ", "Synaptics",
        "Bonjour", "Java", "Defender", "Antivirus", "Security", "Sécurité", "Kaspersky", "Avast", "AVG", "ESET", "Bitdefender",
        "Malwarebytes", "McAfee", "Norton", "Sophos", "Office", "Microsoft 365", "OneDrive", "Teams", "Support Assist",
        "HP ", "Dell ", "Lenovo ", "Asus ", "Acer ", "Bluetooth", "Audio", "Firmware", "BIOS", "Vulkan", "DirectX", "Hotfix",
    ];

    private static readonly string[] NotTheApp = ["unins", "uninst", "setup", "update", "updater", "crash", "helper", "elevate", "install", "report", "squirrel"];

    public string Name => CollectorName;

    public TimeSpan Interval => TimeSpan.FromHours(12);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        result.Try("utilisation", () =>
        {
            var lastRuns = Prefetch();
            if (lastRuns.Count == 0)
            {
                // Dossier illisible (hors du service) ou Prefetch désactivé : aucune conclusion possible.
                return;
            }

            var now = timeProvider.GetUtcNow();
            var apps = InstalledPrograms.All()
                .Where(p => !Excluded.Any(e => p.Name.Contains(e, StringComparison.OrdinalIgnoreCase)))
                .Select(p => Describe(p, lastRuns, now))
                .OfType<AppUsage>()
                .DistinctBy(a => a.Name)
                .OrderByDescending(a => a.Unused)
                .ThenByDescending(a => a.SizeMb ?? 0)
                .ToList();
            var unused = apps.Where(a => a.Unused).ToList();
            result.Add("applications.inutilisees", unused.Count, "");
            result.Add("applications.inutilisees_go", Math.Round(unused.Sum(a => a.SizeMb ?? 0) / 1024, 1), "Go");
            result.Snapshot("applications", apps);
        });

        return Task.FromResult(result.Build());
    }

    internal static AppUsage? Describe(InstalledPrograms.Program program, IReadOnlyDictionary<string, DateTimeOffset> lastRuns, DateTimeOffset now)
    {
        var executables = Executables(program).ToList();
        if (executables.Count == 0)
        {
            // Sans exécutable connu, on ne sait pas dire si le programme sert : il n'est pas listé.
            return null;
        }

        DateTimeOffset? lastUsed = executables
            .Select(e => lastRuns.TryGetValue(e, out var at) ? at : (DateTimeOffset?)null)
            .Max();
        var installedLongAgo = program.InstallDate is { } date && now - new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) > NewInstall;
        var unused = lastUsed is { } last ? now - last > UnusedAfter : installedLongAgo;
        return new AppUsage(
            program.Name,
            program.Publisher,
            program.Version,
            program.SizeKb is { } kb ? Math.Round(kb / 1024.0) : null,
            program.InstallDate,
            lastUsed,
            unused,
            program.UninstallCommand,
            program.Key);
    }

    /// <summary>Noms des exécutables du programme (en majuscules, comme dans Prefetch), sans désinstalleur ni mise à jour.</summary>
    internal static IEnumerable<string> Executables(InstalledPrograms.Program program)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (program.DisplayIcon is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            names.Add(Path.GetFileName(icon));
        }

        if (program.InstallLocation is { Length: > 3 } folder && Directory.Exists(folder))
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
                foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", options).Take(40))
                {
                    names.Add(Path.GetFileName(exe));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dossier illisible : l'icône suffit.
            }
        }

        return names
            .Where(n => !NotTheApp.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.ToUpperInvariant());
    }

    /// <summary>« CHROME.EXE-1A2B3C4D.pf » : le programme et la date de son dernier lancement.</summary>
    private static Dictionary<string, DateTimeOffset> Prefetch()
    {
        var runs = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.pf"))
            {
                var dash = file.Name.LastIndexOf('-');
                if (dash <= 0)
                {
                    continue;
                }

                var exe = file.Name[..dash].ToUpperInvariant();
                var at = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                if (!runs.TryGetValue(exe, out var known) || at > known)
                {
                    runs[exe] = at;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return runs;
        }

        return runs;
    }
}
