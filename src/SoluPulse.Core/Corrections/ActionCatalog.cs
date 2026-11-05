namespace SoluPulse.Core.Corrections;

public sealed class UnknownActionException(string actionId)
    : Exception($"L'action « {actionId} » n'existe pas dans le catalogue.");

/// <summary>
/// Le catalogue fermé : il est construit une fois au démarrage et ne contient que des actions
/// compilées dans SoluPulse. Aucune commande arbitraire ne peut y être ajoutée (EF-COR-07).
/// </summary>
public sealed class ActionCatalog
{
    private readonly IReadOnlyDictionary<string, ISystemAction> _actions;

    public ActionCatalog(IEnumerable<ISystemAction> actions)
    {
        var map = new Dictionary<string, ISystemAction>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (!map.TryAdd(action.Descriptor.Id, action))
            {
                throw new InvalidOperationException($"Action en double dans le catalogue : {action.Descriptor.Id}");
            }
        }

        _actions = map;
    }

    public IReadOnlySet<string> Ids => _actions.Keys.ToHashSet(StringComparer.Ordinal);

    public IEnumerable<ActionDescriptor> Descriptors => _actions.Values.Select(a => a.Descriptor).OrderBy(d => d.Id, StringComparer.Ordinal);

    public bool Contains(string id) => _actions.ContainsKey(id);

    public ISystemAction Get(string id) =>
        _actions.TryGetValue(id, out var action) ? action : throw new UnknownActionException(id);
}
