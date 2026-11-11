using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Mises à jour Windows en attente (EF-COL-07), via l'agent Windows Update. La recherche est lente et
/// peut contacter les serveurs de Microsoft comme le fait Windows : une fois par jour seulement.
/// </summary>
public sealed class WindowsUpdateCollector(TimeProvider timeProvider) : ICollector
{
    public string Name => "mises_a_jour";

    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        await result.TryAsync("windows_update", async () =>
        {
            var search = Task.Run(Search, cancellationToken);
            var completed = await Task.WhenAny(search, Task.Delay(TimeSpan.FromMinutes(5), cancellationToken)).ConfigureAwait(false);
            if (completed != search)
            {
                throw new TimeoutException("La recherche Windows Update n'a pas répondu en 5 minutes.");
            }

            List<string> titles;
            try
            {
                titles = await search.ConfigureAwait(false);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new InvalidOperationException(
                    $"Recherche Windows Update impossible sur ce PC (code 0x{ex.HResult:X8}). Les mises à jour sont peut-être gérées par l'entreprise ou le service est désactivé.", ex);
            }

            result.Add("systeme.mises_a_jour_en_attente", titles.Count, "");
            result.Snapshot("mises_a_jour", titles);
        }).ConfigureAwait(false);

        result.Try("historique", () =>
        {
            var history = History();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var summary = UpdateHistory.Summarize(history, now);
            if (summary.LastSuccess is { } last)
            {
                result.Add("systeme.jours_depuis_derniere_maj", Math.Floor((now - last).TotalDays), "jours");
            }

            result.Add("systeme.mises_a_jour_echouees_30j", summary.Failures, "");
            result.Snapshot("echecs_mises_a_jour", summary.FailedTitles);
        });
        return result.Build();
    }

    private static List<UpdateHistory.Entry> History()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Agent Windows Update absent.");
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        var count = Math.Min((int)searcher.GetTotalHistoryCount(), 200);
        var entries = new List<UpdateHistory.Entry>();
        if (count == 0)
        {
            return entries;
        }

        dynamic history = searcher.QueryHistory(0, count);
        for (var i = 0; i < (int)history.Count; i++)
        {
            dynamic entry = history[i];
            entries.Add(new UpdateHistory.Entry((string)(entry.Title ?? ""), ((DateTime)entry.Date).ToUniversalTime(), (int)entry.ResultCode, (int)entry.Operation, (int)entry.HResult));
        }

        return entries;
    }

    private static List<string> Search()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Agent Windows Update absent.");
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
        var titles = new List<string>();
        for (var i = 0; i < (int)searchResult.Updates.Count; i++)
        {
            titles.Add((string)searchResult.Updates[i].Title);
        }

        return titles;
    }
}

/// <summary>Historique de Windows Update : dernière mise à jour de Windows réussie, échecs récents.</summary>
public static class UpdateHistory
{
    /// <param name="ResultCode">2 réussie, 3 réussie avec erreurs, 4 échouée, 5 abandonnée.</param>
    /// <param name="Operation">1 installation, 2 désinstallation.</param>
    public sealed record Entry(string Title, DateTime DateUtc, int ResultCode, int Operation, int HResult);

    public sealed record Summary(DateTime? LastSuccess, int Failures, IReadOnlyList<string> FailedTitles);

    // Signatures et plateforme de Defender, outil de suppression des logiciels malveillants : ils
    // arrivent même quand les mises à jour de Windows elles-mêmes échouent.
    private static readonly string[] IgnoredUpdates = ["KB2267602", "KB4052623", "KB2311978", "KB915597", "KB5007651", "KB890830"];

    public static Summary Summarize(IEnumerable<Entry> entries, DateTime nowUtc)
    {
        var installs = entries
            .Where(e => e.Operation == 1 && !IgnoredUpdates.Any(k => e.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var last = installs.Where(e => e.ResultCode is 2 or 3).Select(e => (DateTime?)e.DateUtc).Max();
        var failed = installs.Where(e => e.ResultCode is 4 or 5 && nowUtc - e.DateUtc <= TimeSpan.FromDays(30)).ToList();
        return new Summary(
            last,
            failed.Count,
            failed.GroupBy(e => e.Title).Select(g => $"{g.Key} (code 0x{g.First().HResult:X8}, {g.Count()} échec(s))").Take(10).ToList());
    }
}
