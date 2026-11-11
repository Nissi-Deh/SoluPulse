using SoluPulse.Core.Corrections;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>
/// Active la protection du système sur le disque de Windows, condition pour créer des captures
/// (points de restauration). Proposée par la page « Captures du système » quand elle est coupée.
/// Pas d'annulation : la couper effacerait toutes les captures ; elle se règle aussi dans Windows.
/// </summary>
public sealed class EnableSystemProtectionAction : SystemActionBase, IApplicableAction
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.EnableSystemProtection, Family.Repair,
        "Activer la protection du système",
        "Sans protection du système, Windows ne peut pas créer de points de restauration : impossible de revenir en arrière après un pilote, une mise à jour ou un logiciel qui pose problème. Windows réserve une petite part du disque C: aux captures et supprime les plus anciennes quand elle est pleine.",
        ["Activer la protection du système sur le disque de Windows", "Les captures deviennent possibles"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromSeconds(10),
        // Un point de restauration est impossible tant que la protection est coupée.
        TouchesSystem: false);

    public bool IsApplicable() => RestorePointService.Protection() == SystemProtection.Desactivee;

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
        new ActionEstimate(RestorePointService.Protection() switch
        {
            SystemProtection.Active => "La protection du système est déjà active.",
            SystemProtection.BloqueeParStrategie => "La protection du système est bloquée par une stratégie de l'entreprise : demandez à votre service informatique.",
            _ => $"La protection sera activée sur {Drives.SystemRoot.TrimEnd('\\')} ; {Files.Size(Drives.FreeBytes(Drives.SystemRoot))} libres sur ce disque.",
        }), cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (RestorePointService.Protection() == SystemProtection.BloqueeParStrategie)
        {
            return ActionOutcome.Failed("La protection du système est bloquée par une stratégie de l'entreprise.");
        }

        var code = RestorePointService.Enable();
        return code == 0
            ? ActionOutcome.Ok("Protection du système activée : vous pouvez maintenant créer des captures.")
            : ActionOutcome.Failed($"Windows a refusé d'activer la protection du système (code {code}).");
    }, cancellationToken);
}
