namespace SoluPulse.Core.Corrections;

public enum RiskLevel
{
    Faible = 0,
    Moyen = 1,
    Eleve = 2,
}

public enum ActionKind
{
    /// <summary>Modifie le système : passe par l'exécuteur avec sauvegarde et journal.</summary>
    Systeme,

    /// <summary>Recommandation guidée (nettoyage du ventilateur, remplacement…) : rien n'est exécuté.</summary>
    Guide,
}

public enum ExecutionOrigin
{
    Utilisateur,
    Technicien,
}

/// <summary>Ce que l'utilisateur voit avant de valider (EF-COR-02).</summary>
public sealed record ActionDescriptor(
    string Id,
    string Family,
    string Title,
    string Description,
    IReadOnlyList<string> Steps,
    RiskLevel Risk,
    bool Reversible,
    bool RequiresReboot,
    TimeSpan EstimatedDuration,
    ActionKind Kind = ActionKind.Systeme,
    IReadOnlyList<string>? ParameterNames = null)
{
    public bool RequiresRestorePoint => Kind == ActionKind.Systeme && Risk >= RiskLevel.Moyen;
}

/// <summary>Identité Windows de l'utilisateur qui a validé, obtenue par le service via le canal nommé.</summary>
public sealed record CallerIdentity(string UserSid, string UserName);

public sealed record ActionContext(IReadOnlyDictionary<string, string> Parameters, CallerIdentity Caller)
{
    public string Get(string name) =>
        Parameters.TryGetValue(name, out var value)
            ? value
            : throw new ActionParameterException($"Paramètre « {name} » manquant.");
}

public sealed class ActionParameterException(string message) : Exception(message);

public sealed record ActionEstimate(string? Summary)
{
    public static ActionEstimate None { get; } = new((string?)null);
}

public sealed record ActionOutcome(bool Success, string Message)
{
    public static ActionOutcome Ok(string message) => new(true, message);

    public static ActionOutcome Failed(string message) => new(false, message);
}

/// <summary>
/// Une action du catalogue fermé (EF-COR-07). Une action n'accepte que des paramètres validés
/// par elle-même, et n'appelle les outils Windows qu'avec des arguments fixes.
/// </summary>
public interface ISystemAction
{
    ActionDescriptor Descriptor { get; }

    /// <summary>Valide strictement les paramètres. Lève <see cref="ActionParameterException"/> sinon.</summary>
    Task ValidateAsync(ActionContext context, CancellationToken cancellationToken);

    /// <summary>Estimation affichée sur la fiche (espace libéré, éléments concernés…).</summary>
    Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken);

    /// <summary>Sauvegarde ce qui va être modifié, sérialisé, ou <c>null</c> si l'action n'est pas réversible.</summary>
    Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken);

    Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken);

    Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken);
}

/// <summary>Base pratique pour les actions sans paramètre ni sauvegarde.</summary>
public abstract class SystemActionBase : ISystemAction
{
    public abstract ActionDescriptor Descriptor { get; }

    public virtual Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var allowed = Descriptor.ParameterNames ?? [];
        var unexpected = context.Parameters.Keys.Where(k => !allowed.Contains(k, StringComparer.Ordinal)).ToList();
        if (unexpected.Count > 0)
        {
            throw new ActionParameterException($"Paramètre(s) non autorisé(s) : {string.Join(", ", unexpected)}.");
        }

        return Task.CompletedTask;
    }

    public virtual Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ActionEstimate.None);

    public virtual Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public abstract Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken);

    public virtual Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken) =>
        Task.FromResult(ActionOutcome.Failed("Cette action ne peut pas être annulée."));
}

/// <summary>Une recommandation guidée : la fiche affiche les étapes, rien n'est exécuté.</summary>
public sealed class GuidanceAction(ActionDescriptor descriptor) : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = descriptor with { Kind = ActionKind.Guide, Reversible = false };

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ActionOutcome.Ok("Recommandation consultée."));
}
