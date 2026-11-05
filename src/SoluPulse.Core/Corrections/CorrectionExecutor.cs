using System.Collections.Concurrent;
using System.Security.Cryptography;
using SoluPulse.Core.Audit;

namespace SoluPulse.Core.Corrections;

/// <summary>La fiche présentée à l'utilisateur avant validation (EF-COR-02).</summary>
public sealed record PreparedCorrection(
    string Token,
    ActionDescriptor Action,
    Guid? AlertId,
    IReadOnlyDictionary<string, string> Parameters,
    ActionEstimate Estimate,
    DateTimeOffset ExpiresAt);

public sealed record CorrectionResult(Guid? ExecutionId, ExecutionState State, string Message, VerificationResult Verification);

public sealed class CorrectionRejectedException(string message) : Exception(message);

/// <summary>
/// Applique les actions du catalogue. Une action ne peut s'exécuter qu'avec le jeton à usage unique
/// délivré par <see cref="PrepareAsync"/>, c'est-à-dire après que la fiche a été affichée, puis
/// validée par le même utilisateur Windows (EF-COR-01). Avant d'agir, l'exécuteur journalise, crée
/// un point de restauration si le risque l'exige (EF-COR-03) et sauvegarde l'état (EF-COR-04).
/// Après, il relance la règle (EF-COR-06) et inscrit le résultat au journal d'audit (EF-COR-05).
/// </summary>
public sealed class CorrectionExecutor(
    ActionCatalog catalog,
    IExecutionJournal journal,
    IAuditLog audit,
    IRestorePointService restorePoints,
    IAlertVerifier verifier,
    TimeProvider timeProvider) : IDisposable
{
    public static readonly TimeSpan ValidationWindow = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PendingCorrection> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PreparedCorrection> PrepareAsync(
        string actionId,
        Guid? alertId,
        IReadOnlyDictionary<string, string> parameters,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var action = catalog.Get(actionId);
        var context = new ActionContext(parameters, caller);
        await action.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        var estimate = await action.EstimateAsync(context, cancellationToken).ConfigureAwait(false);

        PurgeExpired();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expiresAt = timeProvider.GetUtcNow() + ValidationWindow;
        _pending[token] = new PendingCorrection(action, alertId, parameters, caller, expiresAt);
        return new PreparedCorrection(token, action.Descriptor, alertId, parameters, estimate, expiresAt);
    }

    /// <summary>L'utilisateur a refusé : le jeton est détruit et le refus journalisé.</summary>
    public async Task CancelAsync(string token, CallerIdentity caller, CancellationToken cancellationToken)
    {
        if (_pending.TryGetValue(token, out var pending) && pending.Caller.UserSid == caller.UserSid && _pending.TryRemove(token, out _))
        {
            await audit.AppendAsync(
                new AuditEntry(timeProvider.GetUtcNow(), "Refus", pending.Action.Descriptor.Id, null, pending.AlertId, nameof(ExecutionOrigin.Utilisateur), caller.UserName, null, "Refusée par l'utilisateur", null),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CorrectionResult> ExecuteAsync(string token, CallerIdentity caller, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!_pending.TryGetValue(token, out var pending) || pending.ExpiresAt < now)
        {
            _pending.TryRemove(token, out _);
            throw new CorrectionRejectedException("Validation absente ou expirée : la fiche de correction doit être réaffichée.");
        }

        if (!string.Equals(pending.Caller.UserSid, caller.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new CorrectionRejectedException("La correction doit être validée par l'utilisateur qui l'a demandée.");
        }

        // Usage unique : un second appel avec le même jeton échoue.
        if (!_pending.TryRemove(token, out _))
        {
            throw new CorrectionRejectedException("Cette validation a déjà été utilisée.");
        }

        var action = pending.Action;
        var descriptor = action.Descriptor;
        var context = new ActionContext(pending.Parameters, caller);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Les paramètres sont revalidés : l'état du PC a pu changer depuis l'affichage de la fiche.
            await action.ValidateAsync(context, cancellationToken).ConfigureAwait(false);

            var executionId = Guid.NewGuid();
            var record = new ExecutionRecord(
                executionId, descriptor.Id, pending.AlertId, ExecutionOrigin.Utilisateur, pending.Parameters,
                caller, now, timeProvider.GetUtcNow(), null, ExecutionState.EnCours, null, null,
                VerificationResult.NonApplicable, null);
            await journal.BeginAsync(record, cancellationToken).ConfigureAwait(false);

            if (descriptor.Kind == ActionKind.Guide)
            {
                var guided = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(record, ExecutionState.Reussie, guided.Message, VerificationResult.NonApplicable, cancellationToken).ConfigureAwait(false);
            }

            if (descriptor.RequiresRestorePoint)
            {
                var restorePoint = await restorePoints.CreateAsync($"SoluPulse - {descriptor.Title}", cancellationToken).ConfigureAwait(false);
                if (!restorePoint.Success)
                {
                    return await FinishAsync(
                        record, ExecutionState.Echouee,
                        $"Correction non appliquée : le point de restauration n'a pas pu être créé ({restorePoint.Message}).",
                        VerificationResult.NonApplicable, cancellationToken).ConfigureAwait(false);
                }
            }

            string? backup = null;
            if (descriptor.Reversible)
            {
                backup = await action.BackupAsync(context, cancellationToken).ConfigureAwait(false);
                if (backup is null)
                {
                    return await FinishAsync(
                        record, ExecutionState.Echouee, "Correction non appliquée : la sauvegarde de l'état a échoué.",
                        VerificationResult.NonApplicable, cancellationToken).ConfigureAwait(false);
                }

                await journal.SaveBackupAsync(executionId, backup, cancellationToken).ConfigureAwait(false);
                context = context with { Backup = backup };
            }

            ActionOutcome outcome;
            try
            {
                outcome = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = ActionOutcome.Failed(ex.Message);
            }

            if (!outcome.Success && backup is not null)
            {
                var rollback = await TryUndoAsync(action, context, backup, cancellationToken).ConfigureAwait(false);
                outcome = outcome with { Message = $"{outcome.Message} Retour à l'état précédent : {rollback.Message}" };
            }

            var verification = outcome.Success && pending.AlertId is { } alertId && !descriptor.RequiresReboot
                ? await verifier.VerifyAsync(alertId, cancellationToken).ConfigureAwait(false)
                : VerificationResult.NonApplicable;

            return await FinishAsync(
                record with { Backup = backup },
                outcome.Success ? ExecutionState.Reussie : ExecutionState.Echouee,
                outcome.Message, verification, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Le bouton « Annuler » (EF-COR-04).</summary>
    public async Task<CorrectionResult> UndoAsync(Guid executionId, CallerIdentity caller, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await journal.GetAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new CorrectionRejectedException("Exécution introuvable.");
            if (record.State != ExecutionState.Reussie || record.UndoneAt is not null)
            {
                throw new CorrectionRejectedException("Seule une correction réussie et non annulée peut être annulée.");
            }

            if (record.Backup is null || !catalog.Contains(record.ActionId))
            {
                throw new CorrectionRejectedException("Cette correction n'est pas réversible.");
            }

            var action = catalog.Get(record.ActionId);
            var outcome = await TryUndoAsync(action, new ActionContext(record.Parameters, caller), record.Backup, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (outcome.Success)
            {
                await journal.MarkUndoneAsync(executionId, now, outcome.Message, cancellationToken).ConfigureAwait(false);
            }

            await audit.AppendAsync(
                new AuditEntry(now, "Annulation", record.ActionId, executionId, record.AlertId, nameof(ExecutionOrigin.Utilisateur), caller.UserName, now, outcome.Success ? "Réussie" : "Échouée", outcome.Message),
                cancellationToken).ConfigureAwait(false);
            return new CorrectionResult(executionId, outcome.Success ? ExecutionState.Annulee : record.State, outcome.Message, VerificationResult.NonApplicable);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Au démarrage du service : toute exécution restée « en cours » a été interrompue (coupure,
    /// plantage). Si une sauvegarde existe, l'état précédent est restauré ; sinon l'exécution est
    /// marquée interrompue (ENF-FIA-02).
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        var interrupted = await journal.ListInProgressAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in interrupted)
        {
            var now = timeProvider.GetUtcNow();
            if (record.Backup is not null && catalog.Contains(record.ActionId))
            {
                var outcome = await TryUndoAsync(catalog.Get(record.ActionId), new ActionContext(record.Parameters, record.ValidatedBy), record.Backup, cancellationToken).ConfigureAwait(false);
                var state = outcome.Success ? ExecutionState.AnnuleeAuRedemarrage : ExecutionState.Interrompue;
                var message = $"Correction interrompue. Retour à l'état précédent : {outcome.Message}";
                await journal.CompleteAsync(record.Id, state, message, VerificationResult.NonApplicable, now, cancellationToken).ConfigureAwait(false);
                await audit.AppendAsync(new AuditEntry(now, "Reprise", record.ActionId, record.Id, record.AlertId, record.Origin.ToString(), record.ValidatedBy.UserName, record.ValidatedAt, state.ToString(), message), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                const string message = "Correction interrompue avant sa fin ; aucune sauvegarde à restaurer.";
                await journal.CompleteAsync(record.Id, ExecutionState.Interrompue, message, VerificationResult.NonApplicable, now, cancellationToken).ConfigureAwait(false);
                await audit.AppendAsync(new AuditEntry(now, "Reprise", record.ActionId, record.Id, record.AlertId, record.Origin.ToString(), record.ValidatedBy.UserName, record.ValidatedAt, nameof(ExecutionState.Interrompue), message), cancellationToken).ConfigureAwait(false);
            }
        }

        return interrupted.Count;
    }

    private async Task<CorrectionResult> FinishAsync(ExecutionRecord record, ExecutionState state, string message, VerificationResult verification, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await journal.CompleteAsync(record.Id, state, message, verification, now, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(
            new AuditEntry(now, "Execution", record.ActionId, record.Id, record.AlertId, record.Origin.ToString(), record.ValidatedBy.UserName, record.ValidatedAt, state.ToString(), message),
            cancellationToken).ConfigureAwait(false);
        return new CorrectionResult(record.Id, state, message, verification);
    }

    private static async Task<ActionOutcome> TryUndoAsync(ISystemAction action, ActionContext context, string backup, CancellationToken cancellationToken)
    {
        try
        {
            return await action.UndoAsync(context, backup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ActionOutcome.Failed(ex.Message);
        }
    }

    public void Dispose() => _gate.Dispose();

    private void PurgeExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (token, pending) in _pending)
        {
            if (pending.ExpiresAt < now)
            {
                _pending.TryRemove(token, out _);
            }
        }
    }

    private sealed record PendingCorrection(
        ISystemAction Action,
        Guid? AlertId,
        IReadOnlyDictionary<string, string> Parameters,
        CallerIdentity Caller,
        DateTimeOffset ExpiresAt);
}
