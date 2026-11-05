using Microsoft.Extensions.Options;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Rules;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Boucle principale du service (EF-COL-01) : au démarrage, initialise la base, charge les règles,
/// reprend les corrections interrompues ; ensuite, lance chaque collecteur à sa fréquence (EF-COL-11)
/// et compacte l'historique une fois par jour.
/// </summary>
public sealed class MonitoringService(
    SqliteDatabase database,
    AlertStore alerts,
    MeasurementStore measurements,
    AgentState state,
    DiagnosticRunner runner,
    CorrectionExecutor executor,
    ActionCatalog catalog,
    SettingsService settings,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<MonitoringService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AggregateRetention = TimeSpan.FromDays(730);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartAsync().ConfigureAwait(false);

        var next = runner.Collectors.ToDictionary(c => c.Name, _ => DateTimeOffset.MinValue, StringComparer.Ordinal);
        var nextCompaction = time.GetUtcNow().AddMinutes(10);
        var running = new List<Task>();

        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            var now = time.GetUtcNow();
            foreach (var collector in runner.Collectors.Where(c => next[c.Name] <= now))
            {
                next[collector.Name] = now + collector.Interval;
                running.Add(runner.RunCollectorAsync(collector, waitIfRunning: false, stoppingToken));
            }

            running.RemoveAll(t => t.IsCompleted);

            if (now >= nextCompaction)
            {
                nextCompaction = now.AddDays(1);
                await CompactAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));

        await Task.WhenAll(running).ConfigureAwait(false);

        async Task StartAsync()
        {
            logger.LogInformation("Démarrage du service SoluPulse {Version}.", typeof(MonitoringService).Assembly.GetName().Version);
            await database.InitializeAsync(stoppingToken).ConfigureAwait(false);

            var rules = RuleLoader.LoadDirectory(options.Value.RulesDirectory, catalog.Ids);
            foreach (var error in rules.Errors)
            {
                logger.LogError("Règle ignorée : {Error}", error);
            }

            state.RuleErrors = rules.Errors;
            state.Engine = new RuleEngine(rules.Rules);
            var orphans = state.Engine.Restore(await alerts.ListActiveAsync(stoppingToken).ConfigureAwait(false));
            await runner.CloseOrphansAsync(orphans, stoppingToken).ConfigureAwait(false);
            logger.LogInformation("{Count} règles chargées depuis {Directory}.", rules.Rules.Count, options.Value.RulesDirectory);

            var recovered = await executor.RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                logger.LogWarning("{Count} correction(s) interrompue(s) traitée(s) au démarrage.", recovered);
            }

            await settings.GetAsync(stoppingToken).ConfigureAwait(false);
            state.Ready = true;
        }
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retention = (await settings.GetAsync(cancellationToken).ConfigureAwait(false)).DetailRetentionDays;
            var deleted = await measurements.CompactAsync(time.GetUtcNow(), TimeSpan.FromDays(retention), AggregateRetention, cancellationToken).ConfigureAwait(false);
            if (deleted > 10_000)
            {
                await database.VacuumAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Historique compacté : {Deleted} mesures agrégées.", deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Échec du compactage de l'historique.");
        }
    }
}
