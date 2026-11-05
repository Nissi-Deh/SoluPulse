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
        return result.Build();
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
