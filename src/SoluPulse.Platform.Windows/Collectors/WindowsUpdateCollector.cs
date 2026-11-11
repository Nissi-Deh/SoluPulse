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
        var session = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Agent Windows Update absent.");
        var searcher = Com.Call(session, "CreateUpdateSearcher");
        var count = Math.Min(Com.Int(Com.Call(searcher, "GetTotalHistoryCount")), 200);
        var entries = new List<UpdateHistory.Entry>();
        if (count == 0)
        {
            return entries;
        }

        var history = Com.Call(searcher, "QueryHistory", 0, count);
        var total = Com.Int(Com.Get(history, "Count"));
        for (var i = 0; i < total; i++)
        {
            if (Com.Get(history, "Item", i) is not { } entry)
            {
                continue;
            }

            entries.Add(new UpdateHistory.Entry(
                Com.Get(entry, "Title") as string ?? "",
                Com.Get(entry, "Date") is DateTime date ? date.ToUniversalTime() : DateTime.MinValue,
                Com.Int(Com.Get(entry, "ResultCode")),
                Com.Int(Com.Get(entry, "Operation")),
                Com.Int(Com.Get(entry, "HResult"))));
        }

        return entries;
    }

    private static List<string> Search()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Agent Windows Update absent.");
        var session = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Agent Windows Update absent.");
        var searcher = Com.Call(session, "CreateUpdateSearcher");
        searcher.GetType().InvokeMember("Online", System.Reflection.BindingFlags.SetProperty, null, searcher, [true], System.Globalization.CultureInfo.InvariantCulture);
        var searchResult = Com.Call(searcher, "Search", "IsInstalled=0 and IsHidden=0 and Type='Software'");
        var updates = Com.Get(searchResult, "Updates") ?? throw new InvalidOperationException("Windows Update n'a pas renvoyé de liste.");
        var titles = new List<string>();
        var count = Com.Int(Com.Get(updates, "Count"));
        for (var i = 0; i < count; i++)
        {
            titles.Add(Com.Get(Com.Get(updates, "Item", i)!, "Title") as string ?? "");
        }

        return titles;
    }
}

/// <summary>
/// Historique de Windows Update : dernière mise à jour de Windows réussie, échecs récents. Les
/// applications du Microsoft Store passent aussi par cet historique (titre « 9WZDNCRFJBMP-… ») :
/// elles sont écartées. Un pilote installé ne prouve pas que Windows reçoit ses correctifs : il ne
/// compte pas comme dernière mise à jour réussie.
/// </summary>
public static partial class UpdateHistory
{
    /// <param name="ResultCode">2 réussie, 3 réussie avec erreurs, 4 échouée, 5 abandonnée.</param>
    /// <param name="Operation">1 installation, 2 désinstallation.</param>
    public sealed record Entry(string Title, DateTime DateUtc, int ResultCode, int Operation, int HResult);

    public sealed record Summary(DateTime? LastSuccess, int Failures, IReadOnlyList<string> FailedTitles);

    // Signatures et plateforme de Defender, outil de suppression des logiciels malveillants : ils
    // arrivent même quand les mises à jour de Windows elles-mêmes échouent. Seul le numéro est cherché :
    // en français, Windows écrit « 2267602 Ko » au lieu de « KB2267602 ».
    private static readonly string[] IgnoredUpdates = ["2267602", "4052623", "2311978", "915597", "5007651", "890830"];

    public static Summary Summarize(IEnumerable<Entry> entries, DateTime nowUtc)
    {
        var installs = entries
            .Where(e => e.Operation == 1 && !IsStoreApp(e.Title) && !IgnoredUpdates.Any(k => e.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var last = installs.Where(e => e.ResultCode is 2 or 3 && !IsDriver(e.Title)).Select(e => (DateTime?)e.DateUtc).Max();
        var failed = installs.Where(e => e.ResultCode is 4 or 5 && nowUtc - e.DateUtc <= TimeSpan.FromDays(30)).ToList();
        return new Summary(
            last,
            failed.Count,
            failed.GroupBy(e => e.Title).Select(g => $"{g.Key} (code 0x{g.First().HResult:X8}, {g.Count()} échec(s))").Take(10).ToList());
    }

    internal static bool IsStoreApp(string title) => StoreApp().IsMatch(title);

    /// <summary>Pilotes : « Intel Ports Driver Update (…) », ou le format « Fabricant - Classe - Version ».</summary>
    internal static bool IsDriver(string title) =>
        title.Contains("Driver", StringComparison.OrdinalIgnoreCase)
        || title.Contains("pilote", StringComparison.OrdinalIgnoreCase)
        || DriverTitle().IsMatch(title);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9A-Z]{12}-")]
    private static partial System.Text.RegularExpressions.Regex StoreApp();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^-]+ - [^-]+ - [\d.]+$")]
    private static partial System.Text.RegularExpressions.Regex DriverTitle();
}

/// <summary>Appels explicites aux objets COM de Windows Update (IDispatch), sans <c>dynamic</c>.</summary>
internal static class Com
{
    public static object Call(object target, string method, params object?[] arguments) =>
        target.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, target, arguments, System.Globalization.CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"Windows Update n'a rien renvoyé pour {method}.");

    public static object? Get(object target, string property, params object?[] arguments) =>
        target.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, target, arguments, System.Globalization.CultureInfo.InvariantCulture);

    public static int Int(object? value) => value is null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
}
