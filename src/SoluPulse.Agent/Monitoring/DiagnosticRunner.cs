using SoluPulse.Contracts;
using SoluPulse.Core;
using SoluPulse.Core.Collection;
using SoluPulse.Core.Rules;
using SoluPulse.Core.Scoring;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Exécute les collecteurs, enregistre leurs résultats, évalue les règles et persiste les alertes.
/// Un collecteur en échec n'arrête jamais les autres (EF-COL-12).
/// </summary>
public sealed class DiagnosticRunner : IDisposable
{
    private readonly IReadOnlyList<ICollector> _collectors;
    private readonly AgentState _state;
    private readonly MeasurementStore _measurements;
    private readonly AlertStore _alerts;
    private readonly InventoryStore _inventory;
    private readonly SensorIssueStore _issues;
    private readonly SettingsService _settings;
    private readonly EventHub _events;
    private readonly AlertMapper _mapper;
    private readonly TimeProvider _time;
    private readonly ILogger<DiagnosticRunner> _logger;
    private readonly Dictionary<string, SemaphoreSlim> _collectorLocks;
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly SemaphoreSlim _fullDiagnosticLock = new(1, 1);

    public DiagnosticRunner(
        IEnumerable<ICollector> collectors,
        AgentState state,
        MeasurementStore measurements,
        AlertStore alerts,
        InventoryStore inventory,
        SensorIssueStore issues,
        SettingsService settings,
        EventHub events,
        AlertMapper mapper,
        TimeProvider time,
        ILogger<DiagnosticRunner> logger)
    {
        _collectors = collectors.ToList();
        _state = state;
        _measurements = measurements;
        _alerts = alerts;
        _inventory = inventory;
        _issues = issues;
        _settings = settings;
        _events = events;
        _mapper = mapper;
        _time = time;
        _logger = logger;
        _collectorLocks = _collectors.ToDictionary(c => c.Name, _ => new SemaphoreSlim(1, 1), StringComparer.Ordinal);
    }

    public IReadOnlyList<ICollector> Collectors => _collectors;

    /// <summary>Exécute un collecteur. S'il tourne déjà, <paramref name="waitIfRunning"/> décide d'attendre ou de passer.</summary>
    public async Task RunCollectorAsync(ICollector collector, bool waitIfRunning, CancellationToken cancellationToken)
    {
        var gate = _collectorLocks[collector.Name];
        if (waitIfRunning)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            CollectorResult result;
            try
            {
                result = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Le collecteur {Collector} a échoué.", collector.Name);
                result = new CollectorResult([], [new SensorIssue(collector.Name, "collecteur", ex.Message, _time.GetUtcNow())], []);
            }

            await _measurements.InsertAsync(result.Measurements, cancellationToken).ConfigureAwait(false);
            await _issues.ReplaceAsync(collector.Name, result.Issues, cancellationToken).ConfigureAwait(false);
            if (result.Snapshots.Count > 0)
            {
                await _inventory.SaveAsync(result.Snapshots, cancellationToken).ConfigureAwait(false);
            }

            _state.Update(collector.Name, result.Measurements, _time.GetUtcNow());
        }
        finally
        {
            gate.Release();
        }

        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Après une correction qui libère de la place, l'analyse de l'occupation du disque est relancée en
    /// arrière-plan pour que la page « Mon PC » montre le nouvel état.
    /// </summary>
    public void RefreshDiskUsageAfter(string actionId, CancellationToken stoppingToken)
    {
        if (!IsSpaceAction(actionId) || _collectors.FirstOrDefault(c => c.Name == Platform.Windows.Collectors.DiskUsageCollector.CollectorName) is not { } collector)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCollectorAsync(collector, waitIfRunning: false, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Le service s'arrête.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Échec de l'analyse de l'occupation du disque après {Action}.", actionId);
            }
        }, CancellationToken.None);
    }

    private static bool IsSpaceAction(string actionId) =>
        actionId.StartsWith("ACT-CLEAN-", StringComparison.Ordinal)
        || actionId.StartsWith("ACT-DISK-", StringComparison.Ordinal)
        || actionId == Core.Corrections.ActionIds.DisableHibernation;

    /// <summary>
    /// Diagnostic complet à la demande (EF-DIA-08). La recherche Windows Update, qui peut durer
    /// plusieurs minutes, garde son rythme quotidien pour tenir l'objectif de 2 minutes (ENF-PERF-03).
    /// </summary>
    public async Task RunFullDiagnosticAsync(CancellationToken cancellationToken)
    {
        await _fullDiagnosticLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _state.DiagnosticRunning = true;
        _events.Publish(new AgentEvent { Kind = "etat", Status = await StatusAsync(cancellationToken).ConfigureAwait(false) });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await Task.WhenAll(_collectors
                .Where(c => c.Interval < TimeSpan.FromHours(24))
                .Select(c => RunCollectorAsync(c, waitIfRunning: true, timeout.Token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Le diagnostic complet a dépassé 3 minutes.");
        }
        finally
        {
            _state.DiagnosticRunning = false;
            _fullDiagnosticLock.Release();
        }

        _events.Publish(new AgentEvent { Kind = "etat", Status = await StatusAsync(cancellationToken).ConfigureAwait(false) });
    }

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _evaluationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var transitions = _state.Engine.Evaluate(_state.Latest(), now, settings.ThresholdOverrides);
            foreach (var transition in transitions)
            {
                await ApplyAsync(transition, now, cancellationToken).ConfigureAwait(false);
            }

            if (transitions.Count > 0)
            {
                _events.Publish(new AgentEvent { Kind = "etat", Status = await StatusAsync(cancellationToken).ConfigureAwait(false) });
            }
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    public async Task<StatusReply> StatusAsync(CancellationToken cancellationToken)
    {
        var active = await _alerts.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var report = HealthScoreCalculator.Compute(active, _state.EvaluatedDomains());
        var issues = await _issues.ListAsync(cancellationToken).ConfigureAwait(false);
        return _mapper.Status(report, active, issues, _state);
    }

    public void Dispose()
    {
        foreach (var gate in _collectorLocks.Values)
        {
            gate.Dispose();
        }

        _evaluationLock.Dispose();
        _fullDiagnosticLock.Dispose();
    }

    private async Task ApplyAsync(AlertTransition transition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (transition.Kind == TransitionKind.Opened)
        {
            var alert = await _alerts.OpenAsync(transition.Rule.Id, transition.Subject, transition.Rule.Domain, transition.Rule.Severity, transition.Value, now, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Alerte ouverte : {Rule} {Subject} ({Severity}).", transition.Rule.Id, transition.Subject, transition.Rule.Severity);
            _events.Publish(new AgentEvent { Kind = "alerte_ouverte", Alert = _mapper.Alert(alert, transition.Rule) });
        }
        else
        {
            var alert = await _alerts.ResolveAsync(transition.Rule.Id, transition.Subject, now, cancellationToken).ConfigureAwait(false);
            if (alert is not null)
            {
                _logger.LogInformation("Alerte résolue : {Rule} {Subject}.", transition.Rule.Id, transition.Subject);
                _events.Publish(new AgentEvent { Kind = "alerte_resolue", Alert = _mapper.Alert(alert, transition.Rule) });
            }
        }
    }

    /// <summary>Ferme les alertes dont la règle a été retirée de la base de règles.</summary>
    public async Task CloseOrphansAsync(IEnumerable<Alert> orphans, CancellationToken cancellationToken)
    {
        foreach (var orphan in orphans)
        {
            await _alerts.ResolveAsync(orphan.RuleId, orphan.Subject, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
    }

    public static HealthDomain[] AllDomains => Enum.GetValues<HealthDomain>();
}
