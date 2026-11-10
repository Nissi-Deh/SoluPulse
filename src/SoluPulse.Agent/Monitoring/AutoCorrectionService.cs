using SoluPulse.Core.Corrections;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Corrections automatiques sans risque. Toutes les 15 minutes, pour chaque alerte active, SoluPulse
/// lance seul les actions marquées automatiques (faible risque, sans paramètre, sans perte de données),
/// si l'utilisateur ne l'a pas désactivé dans les paramètres. Une même action ne tourne pas plus d'une
/// fois par 24 heures, quelle que soit son origine. Chaque exécution passe par l'exécuteur : journal,
/// point de restauration, audit et vérification, comme une correction validée.
/// </summary>
public sealed class AutoCorrectionService(
    AlertStore alerts,
    AgentState state,
    CorrectionExecutor executor,
    ActionCatalog catalog,
    ExecutionJournal journal,
    SettingsService settings,
    DiagnosticRunner runner,
    AlertMapper mapper,
    TimeProvider time,
    ILogger<AutoCorrectionService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Échec des corrections automatiques.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt du service.
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!state.Ready || !(await settings.GetAsync(cancellationToken).ConfigureAwait(false)).AutomaticCorrections)
        {
            return 0;
        }

        var since = time.GetUtcNow() - MinimumGap;
        var recent = (await journal.ListRecentAsync(500, cancellationToken).ConfigureAwait(false))
            .Where(r => r.StartedAt >= since)
            .Select(r => r.ActionId)
            .ToHashSet(StringComparer.Ordinal);

        var ran = 0;
        foreach (var alert in await alerts.ListActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            var rule = state.Engine.FindRule(alert.RuleId);
            foreach (var actionId in rule?.Corrections ?? [])
            {
                if (recent.Contains(actionId) || !mapper.IsUseful(actionId) || !catalog.Get(actionId).Descriptor.Automatic)
                {
                    continue;
                }

                recent.Add(actionId);
                var result = await executor.ExecuteAutomaticAsync(actionId, alert.Id, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Correction automatique {Action} pour {Rule} : {State} — {Message}", actionId, alert.RuleId, result.State, result.Message);
                runner.RefreshDiskUsageAfter(actionId, cancellationToken);
                ran++;
            }
        }

        if (ran > 0)
        {
            await runner.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }

        return ran;
    }
}
