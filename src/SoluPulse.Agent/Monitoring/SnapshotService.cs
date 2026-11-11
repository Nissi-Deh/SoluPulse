using SoluPulse.Core.Audit;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Captures du système : des points de restauration Windows, créés à la demande depuis l'application
/// ou chaque semaine. Revenir à une capture passe par l'outil de restauration de Windows, ouvert par
/// l'application : SoluPulse ne restaure jamais le système lui-même. Chaque capture est inscrite au
/// journal d'audit.
/// </summary>
public sealed class SnapshotService(
    RestorePointService restorePoints,
    SettingsService settings,
    IAuditLog audit,
    TimeProvider time,
    ILogger<SnapshotService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(10);

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
                    await RunWeeklyAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Échec de la capture hebdomadaire.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Arrêt du service.
        }
    }

    public async Task<bool> RunWeeklyAsync(CancellationToken cancellationToken)
    {
        if (!(await settings.GetAsync(cancellationToken).ConfigureAwait(false)).WeeklySnapshots
            || RestorePointService.Protection() != SystemProtection.Active
            || !RestorePointService.IsWeeklyDue(RestorePointService.Latest()?.CreatedAt, time.GetUtcNow()))
        {
            return false;
        }

        var result = await restorePoints.CreateAsync(RestorePointService.WeeklyDescription, cancellationToken).ConfigureAwait(false);
        await AuditAsync("Automatique", null, result, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Capture hebdomadaire : {Success} — {Message}", result.Success, result.Message);
        return result.Success;
    }

    public async Task<RestorePointResult> CreateManualAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        switch (RestorePointService.Protection())
        {
            case SystemProtection.BloqueeParStrategie:
                return new RestorePointResult(false, "La protection du système est bloquée par une stratégie de l'entreprise : demandez à votre service informatique.");
            case SystemProtection.Desactivee:
                return new RestorePointResult(false, "La protection du système est désactivée : activez-la d'abord.");
        }

        if (RestorePointService.Latest() is { } latest && time.GetUtcNow() - latest.CreatedAt < RestorePointService.ManualGap)
        {
            return new RestorePointResult(false, "Une capture vient d'être créée : attendez quelques minutes avant d'en créer une autre.");
        }

        var result = await restorePoints.CreateNowAsync(RestorePointService.ManualDescription, cancellationToken).ConfigureAwait(false);
        await AuditAsync("Utilisateur", caller.UserName, result, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Capture manuelle demandée par {User} : {Success} — {Message}", caller.UserName, result.Success, result.Message);
        return result;
    }

    private Task AuditAsync(string origin, string? user, RestorePointResult result, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return audit.AppendAsync(new AuditEntry(now, "Capture", null, null, null, origin, user, user is null ? null : now, result.Success ? "Réussie" : "Échouée", result.Message), cancellationToken);
    }
}
