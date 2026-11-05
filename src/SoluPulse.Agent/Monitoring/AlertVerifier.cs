using SoluPulse.Core.Corrections;
using SoluPulse.Core.Rules;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Après une correction, relance les collecteurs concernés par la règle de l'alerte puis vérifie si
/// le problème a disparu (EF-COR-06). Si oui, l'alerte est fermée tout de suite.
/// </summary>
public sealed class AlertVerifier(AlertStore alerts, AgentState state, IServiceProvider services, SettingsService settings) : IAlertVerifier
{
    public async Task<VerificationResult> VerifyAsync(Guid alertId, CancellationToken cancellationToken)
    {
        var alert = await alerts.GetAsync(alertId, cancellationToken).ConfigureAwait(false);
        var rule = alert is null ? null : state.Engine.FindRule(alert.RuleId);
        if (alert is null || rule is null)
        {
            return VerificationResult.Indeterminee;
        }

        if (alert.State != Core.AlertState.Active)
        {
            return VerificationResult.Resolue;
        }

        // Le runner dépend de l'exécuteur via le service gRPC : résolution tardive pour éviter un cycle.
        var runner = services.GetRequiredService<DiagnosticRunner>();
        var collectors = state.CollectorsFor(rule.Conditions.Select(c => c.Metric));
        foreach (var collector in runner.Collectors.Where(c => collectors.Contains(c.Name)))
        {
            await runner.RunCollectorAsync(collector, waitIfRunning: true, cancellationToken).ConfigureAwait(false);
        }

        var overrides = (await settings.GetAsync(cancellationToken).ConfigureAwait(false)).ThresholdOverrides;
        var released = RuleEngine.IsReleased(rule, alert.Subject, state.Latest(), overrides.TryGetValue(rule.Id, out var t) ? t : null);
        var refreshed = await alerts.GetAsync(alertId, cancellationToken).ConfigureAwait(false);
        return refreshed?.State == Core.AlertState.Resolue || released == Truth.True
            ? VerificationResult.Resolue
            : released == Truth.False ? VerificationResult.Persistante : VerificationResult.Indeterminee;
    }
}
