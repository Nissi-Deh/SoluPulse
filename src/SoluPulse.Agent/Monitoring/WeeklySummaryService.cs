using System.Globalization;
using SoluPulse.Contracts;
using SoluPulse.Core;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Scoring;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Une fois par semaine, compose le résumé « votre PC cette semaine » et le garde : l'application
/// l'affiche en notification dès qu'elle est connectée, une seule fois. Le premier résumé arrive une
/// semaine après l'installation.
/// </summary>
public sealed class WeeklySummaryService(
    DiagnosticRunner runner,
    AlertStore alerts,
    ExecutionJournal journal,
    SettingsStore store,
    EventHub events,
    TimeProvider time,
    ILogger<WeeklySummaryService> logger) : BackgroundService
{
    private const string DateKey = "resume.date";
    private const string TitleKey = "resume.titre";
    private const string TextKey = "resume.texte";
    private const string ScoreKey = "resume.score";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    public async Task<WeeklySummary?> LatestAsync(CancellationToken cancellationToken)
    {
        var values = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return values.TryGetValue(DateKey, out var date) && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && values.TryGetValue(TitleKey, out var title) && values.TryGetValue(TextKey, out var text)
            ? new WeeklySummary(at, title, text)
            : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(CheckEvery, time);
            do
            {
                try
                {
                    await RunIfDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Échec du résumé hebdomadaire.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt du service.
        }
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var values = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!values.TryGetValue(DateKey, out var last) || !DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastAt))
        {
            // Première semaine : on note le point de départ, le premier résumé viendra dans 7 jours.
            await store.SetAsync(new Dictionary<string, string?> { [DateKey] = now.ToString("o", CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (now - lastAt < WeeklySummary.Period)
        {
            return;
        }

        var since = now - WeeklySummary.Period;
        var status = await runner.StatusAsync(cancellationToken).ConfigureAwait(false);
        var recent = await alerts.ListRecentAsync(1000, cancellationToken).ConfigureAwait(false);
        var active = await alerts.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var executions = await journal.ListRecentAsync(500, cancellationToken).ConfigureAwait(false);
        int? previous = int.TryParse(values.GetValueOrDefault(ScoreKey), CultureInfo.InvariantCulture, out var score) ? score : null;

        var summary = WeeklySummary.Compose(
            now,
            status.GlobalScore,
            previous,
            recent.Count(a => a.OpenedAt >= since),
            recent.Count(a => a.ResolvedAt >= since),
            executions.Count(e => e.StartedAt >= since && e.State == ExecutionState.Reussie),
            active.Count,
            active.Count(a => a.Severity == Severity.Critique));

        await store.SetAsync(new Dictionary<string, string?>
        {
            [DateKey] = summary.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
            [TitleKey] = summary.Title,
            [TextKey] = summary.Text,
            [ScoreKey] = status.GlobalScore.ToString(CultureInfo.InvariantCulture),
        }, cancellationToken).ConfigureAwait(false);
        events.Publish(new AgentEvent { Kind = "resume", Status = status });
        logger.LogInformation("Résumé hebdomadaire : {Text}", summary.Text);
    }
}
