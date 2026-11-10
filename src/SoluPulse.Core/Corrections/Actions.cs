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

    /// <summary>Lancée par SoluPulse lui-même : seules les actions marquées <see cref="ActionDescriptor.Automatic"/>.</summary>
    Automatique,
}

/// <summary>Ce que l'utilisateur voit avant de valider (EF-COR-02).</summary>
/// <param name="Automatic">
/// Action sans risque que SoluPulse peut lancer seul quand l'alerte est ouverte et que l'utilisateur a
/// laissé les corrections automatiques activées : faible risque, sans paramètre, sans perte de données.
/// </param>
/// <param name="TouchesSystem">
/// Faux pour les actions qui ne touchent que les fichiers personnels (déplacement, suppression de gros
/// fichiers) : un point de restauration Windows ne les protégerait pas, la sauvegarde propre à l'action oui.
/// </param>
/// <param name="MultiChoiceParameters">Paramètres où l'utilisateur coche plusieurs valeurs, séparées par <see cref="ActionContext.Separator"/>.</param>
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
    IReadOnlyList<string>? ParameterNames = null,
    bool Automatic = false,
    bool TouchesSystem = true,
    IReadOnlyList<string>? MultiChoiceParameters = null)
{
    /// <summary>Un point de restauration est tenté avant chaque action qui modifie Windows (EF-COR-03).</summary>
    public bool RequiresRestorePoint => Kind == ActionKind.Systeme && TouchesSystem;

    /// <summary>Au-delà du risque faible, l'action n'est pas appliquée si le point de restauration échoue.</summary>
    public bool RestorePointMandatory => RequiresRestorePoint && Risk >= RiskLevel.Moyen;

    public bool IsMultiChoice(string parameter) => MultiChoiceParameters?.Contains(parameter, StringComparer.Ordinal) == true;
}

/// <summary>Identité Windows de l'utilisateur qui a validé, obtenue par le service via le canal nommé.</summary>
public sealed record CallerIdentity(string UserSid, string UserName)
{
    /// <summary>L'identité inscrite au journal quand SoluPulse agit seul.</summary>
    public static CallerIdentity Automatic { get; } = new("S-1-5-18", "SoluPulse (automatique)");
}

/// <param name="Backup">La sauvegarde faite juste avant l'exécution, transmise à l'action.</param>
public sealed record ActionContext(IReadOnlyDictionary<string, string> Parameters, CallerIdentity Caller, string? Backup = null)
{
    /// <summary>Sépare les valeurs cochées d'un paramètre à choix multiples.</summary>
    public const char Separator = '|';

    public string Get(string name) =>
        Parameters.TryGetValue(name, out var value)
            ? value
            : throw new ActionParameterException($"Paramètre « {name} » manquant.");

    /// <summary>Les valeurs cochées, sans doublon ; au moins une est exigée.</summary>
    public IReadOnlyList<string> GetAll(string name)
    {
        var values = Get(name).Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return values.Count > 0 ? values : throw new ActionParameterException($"Cochez au moins un élément pour « {name} ».");
    }
}

/// <summary>
/// Une action qui ne concerne que certains PC (Docker installé, veille prolongée active…). Les
/// alertes ne proposent que les actions utiles sur ce PC.
/// </summary>
public interface IApplicableAction
{
    bool IsApplicable();
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

public sealed record ParameterChoice(string Value, string Label, string? Detail = null);

/// <summary>
/// Une action à paramètres propose elle-même les valeurs possibles : l'utilisateur choisit dans une
/// liste, il ne saisit jamais de texte libre transmis au système.
/// </summary>
public interface IActionParameterChoices
{
    Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken);
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
    public override ActionDescriptor Descriptor { get; } = descriptor with { Kind = ActionKind.Guide, Reversible = false, Automatic = false };

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ActionOutcome.Ok("Recommandation consultée."));
}
