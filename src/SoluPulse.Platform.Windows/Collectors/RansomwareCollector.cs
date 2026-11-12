using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Repère un rançongiciel au travail dans les dossiers personnels (Bureau, Documents, Images,
/// Vidéos, Musique, Téléchargements, OneDrive) : des dizaines de fichiers renommés avec une nouvelle
/// extension en une minute, ou des notes de rançon déposées dans plusieurs dossiers. Le signal reste
/// levé une heure. Rien n'est lu dans les fichiers : seuls leurs noms sont observés.
/// </summary>
public sealed partial class RansomwareCollector(TimeProvider timeProvider) : ICollector, IDisposable
{
    public const int RenameBurst = 50;
    public const int NoteBurst = 3;
    private static readonly string[] WatchedFolders = ["Desktop", "Documents", "Pictures", "Videos", "Music", "Downloads", "OneDrive"];
    private static readonly TimeSpan Latch = TimeSpan.FromHours(1);

    private readonly ConcurrentQueue<DateTimeOffset> _renames = new();
    private readonly ConcurrentQueue<(DateTimeOffset Time, string Folder)> _notes = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _alarmUntil;
    private string? _alarmReason;

    public string Name => "rancongiciel";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        result.Try("surveillance", () =>
        {
            lock (_lock)
            {
                var now = timeProvider.GetUtcNow();
                if (now >= _nextRefresh)
                {
                    Refresh();
                    _nextRefresh = now.AddHours(1);
                }

                Trim(now);
                var renames = MaxInWindow(_renames.ToArray(), TimeSpan.FromMinutes(1));
                var notes = _notes.Where(n => now - n.Time <= TimeSpan.FromMinutes(5)).Select(n => n.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (renames >= RenameBurst || notes >= NoteBurst)
                {
                    _alarmUntil = now + Latch;
                    _alarmReason = renames >= RenameBurst
                        ? $"{renames} fichiers renommés avec une nouvelle extension en une minute"
                        : $"notes de rançon déposées dans {notes} dossiers";
                }

                var alarm = _alarmUntil is { } until && now < until;
                result.Add("securite.rancongiciel_suspect", alarm ? 1 : 0, "");
                result.Snapshot("rancongiciel", new { DossiersSurveilles = _watchers.Count, Alerte = alarm ? _alarmReason : null });
            }
        });

        return Task.FromResult(result.Build());
    }

    /// <summary>Plus grand nombre d'événements tombant dans une même fenêtre glissante.</summary>
    internal static int MaxInWindow(IReadOnlyList<DateTimeOffset> times, TimeSpan window)
    {
        var sorted = times.Order().ToList();
        var best = 0;
        for (int start = 0, end = 0; end < sorted.Count; end++)
        {
            while (sorted[end] - sorted[start] > window)
            {
                start++;
            }

            best = Math.Max(best, end - start + 1);
        }

        return best;
    }

    // Ce que chiffre un rançongiciel : documents, images, vidéos, archives. Les noms temporaires
    // (OneDrive « .849C9593-… », Git « .lock », navigateurs « .crdownload ») n'en font pas partie.
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf", ".txt", ".pdf", ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv",
        ".ppt", ".pptx", ".pptm", ".odp", ".pub", ".vsdx", ".one", ".accdb", ".mdb", ".sql", ".db", ".sqlite",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".webp", ".raw", ".cr2", ".nef", ".psd", ".ai", ".svg",
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".3gp", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg",
        ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".pst", ".ost", ".eml", ".msg", ".dwg", ".dxf", ".indd", ".epub",
    };

    /// <summary>
    /// Un document dont l'extension change, sans redevenir un document : « rapport.docx » →
    /// « rapport.docx.lockbit ». Un fichier temporaire renommé en document (synchronisation, téléchargement)
    /// ne compte pas, ni une conversion d'un document vers un autre.
    /// </summary>
    internal static bool IsEncryptionRename(string oldPath, string newPath)
    {
        var before = Path.GetExtension(oldPath);
        var after = Path.GetExtension(newPath);
        return DocumentExtensions.Contains(before)
            && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase)
            && !DocumentExtensions.Contains(after)
            && !after.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            && !after.Equals(".bak", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRansomNote(string path) => RansomNote().IsMatch(Path.GetFileName(path));

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void Refresh()
    {
        var wanted = UserProfiles.All()
            .SelectMany(p => WatchedFolders.Select(f => Path.Combine(p.Folder, f)))
            .Where(Directory.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _watchers.Where(w => !wanted.Contains(w.Path)).ToList())
        {
            stale.Dispose();
            _watchers.Remove(stale);
        }

        foreach (var folder in wanted.Where(f => _watchers.All(w => !string.Equals(w.Path, f, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Renamed += (_, e) =>
                {
                    if (IsEncryptionRename(e.OldFullPath, e.FullPath))
                    {
                        _renames.Enqueue(timeProvider.GetUtcNow());
                    }
                };
                watcher.Created += (_, e) =>
                {
                    if (IsRansomNote(e.FullPath))
                    {
                        _notes.Enqueue((timeProvider.GetUtcNow(), Path.GetDirectoryName(e.FullPath) ?? ""));
                    }
                };

                // Débordement du tampon (décompression d'une grosse archive, par exemple) : ignoré, pour
                // ne jamais crier au rançongiciel sur une simple copie de fichiers.
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Dossier inaccessible ou disparu : on surveille les autres.
            }
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_renames.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(5))
        {
            _renames.TryDequeue(out _);
        }

        while (_notes.TryPeek(out var note) && now - note.Time > TimeSpan.FromMinutes(10))
        {
            _notes.TryDequeue(out _);
        }
    }

    [GeneratedRegex(@"(decrypt|ransom|recover.?(your|my|all)?.?files|restore.?(your|my|all)?.?files|how.?to.?(back|get|recover|restore)|_readme|readme.?(for|to).?(decrypt|restore)|help.?(decrypt|restore))[^\\]*\.(txt|html?|hta|rtf)$", RegexOptions.IgnoreCase)]
    private static partial Regex RansomNote();
}
