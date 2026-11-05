using SoluPulse.Core.Collection;

namespace SoluPulse.Core.Rules;

public enum TransitionKind
{
    Opened,
    Closed,
}

public sealed record AlertTransition(TransitionKind Kind, Rule Rule, string Subject, double? Value);

/// <summary>Résultat à trois états : une métrique absente ne doit ni ouvrir ni fermer une alerte.</summary>
public enum Truth
{
    False,
    True,
    Unknown,
}

/// <summary>
/// Évalue les règles sur les dernières mesures. Le moteur garde l'état de chaque couple
/// (règle, sujet) : début du dépassement et alerte ouverte. Il applique la durée minimale de
/// dépassement, l'hystérésis à la fermeture, et n'ouvre jamais deux fois la même alerte (EF-DIA-06).
/// </summary>
/// <remarks>
/// Le sujet d'une alerte est la partie capturée par <c>*</c> dans la métrique de la première
/// condition (par exemple la lettre du disque). Les autres conditions contenant le même nombre de
/// <c>*</c> sont liées au même sujet ; sinon elles sont vraies si au moins une métrique correspond.
/// </remarks>
public sealed class RuleEngine
{
    private readonly Dictionary<(string RuleId, string Subject), RuleState> _states = [];
    private readonly Dictionary<string, Rule> _rules;

    public RuleEngine(IEnumerable<Rule> rules)
    {
        _rules = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<Rule> Rules => _rules.Values;

    public Rule? FindRule(string id) => _rules.GetValueOrDefault(id);

    /// <summary>
    /// Recharge les alertes ouvertes au démarrage. Renvoie celles dont la règle n'existe plus,
    /// que l'appelant doit fermer.
    /// </summary>
    public IReadOnlyList<Alert> Restore(IEnumerable<Alert> openAlerts)
    {
        var orphans = new List<Alert>();
        foreach (var alert in openAlerts)
        {
            if (_rules.ContainsKey(alert.RuleId))
            {
                _states[(alert.RuleId, alert.Subject)] = new RuleState { Open = true };
            }
            else
            {
                orphans.Add(alert);
            }
        }

        return orphans;
    }

    public IReadOnlyList<AlertTransition> Evaluate(
        IReadOnlyDictionary<string, Measurement> latest,
        DateTimeOffset now,
        IReadOnlyDictionary<string, double>? thresholdOverrides = null)
    {
        var transitions = new List<AlertTransition>();
        foreach (var rule in _rules.Values)
        {
            double? thresholdOverride = thresholdOverrides is not null && thresholdOverrides.TryGetValue(rule.Id, out var t) ? t : null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (subject, primaryValue) in Instances(rule, latest))
            {
                seen.Add(subject);
                var state = GetState(rule.Id, subject);
                EvaluateInstance(rule, subject, primaryValue, latest, now, thresholdOverride, state, transitions);
            }

            // Une alerte ouverte dont le sujet a disparu (disque débranché, capteur absent) reste
            // ouverte : on ne sait pas si le problème est résolu.
            foreach (var key in _states.Keys.Where(k => k.RuleId == rule.Id && !seen.Contains(k.Subject)).ToList())
            {
                if (!_states[key].Open)
                {
                    _states.Remove(key);
                }
            }
        }

        return transitions;
    }

    /// <summary>
    /// Vérifie immédiatement si une alerte ouverte peut être fermée, sans attendre le prochain
    /// cycle. Utilisé après une correction (EF-COR-06).
    /// </summary>
    public static Truth IsReleased(Rule rule, string subject, IReadOnlyDictionary<string, Measurement> latest, double? thresholdOverride = null) =>
        Released(rule, subject, latest, thresholdOverride);

    private static void EvaluateInstance(
        Rule rule,
        string subject,
        double? primaryValue,
        IReadOnlyDictionary<string, Measurement> latest,
        DateTimeOffset now,
        double? thresholdOverride,
        RuleState state,
        List<AlertTransition> transitions)
    {
        if (state.Open)
        {
            if (Released(rule, subject, latest, thresholdOverride) == Truth.True)
            {
                state.Open = false;
                state.BreachSince = null;
                transitions.Add(new AlertTransition(TransitionKind.Closed, rule, subject, primaryValue));
            }

            return;
        }

        switch (Breached(rule, subject, latest, thresholdOverride))
        {
            case Truth.True:
                state.BreachSince ??= now;
                if (now - state.BreachSince.Value >= rule.MinDuration)
                {
                    state.Open = true;
                    transitions.Add(new AlertTransition(TransitionKind.Opened, rule, subject, primaryValue));
                }

                break;
            case Truth.False:
                state.BreachSince = null;
                break;
        }
    }

    private RuleState GetState(string ruleId, string subject)
    {
        if (!_states.TryGetValue((ruleId, subject), out var state))
        {
            state = new RuleState();
            _states[(ruleId, subject)] = state;
        }

        return state;
    }

    private static IEnumerable<(string Subject, double? Value)> Instances(Rule rule, IReadOnlyDictionary<string, Measurement> latest)
    {
        var pattern = rule.PrimaryCondition.Metric;
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            yield return (string.Empty, latest.TryGetValue(pattern, out var m) ? m.Value : null);
            yield break;
        }

        foreach (var (metric, measurement) in latest.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (MetricName.Matches(pattern, metric, out var captured))
            {
                yield return (captured ?? string.Empty, measurement.Value);
            }
        }
    }

    private static Truth Breached(Rule rule, string subject, IReadOnlyDictionary<string, Measurement> latest, double? thresholdOverride)
    {
        var results = rule.Conditions.Select((c, i) =>
            Check(c, subject, latest, (cond, v) => cond.IsBreached(v, i == 0 ? thresholdOverride : null)));
        return rule.Mode == ConditionMode.Tous ? All(results) : Any(results);
    }

    /// <summary>
    /// L'expression est « levée » quand elle ne serait plus vraie même avec l'hystérésis :
    /// pour « tous », il suffit qu'une condition soit levée ; pour « au moins un », il faut que toutes le soient.
    /// </summary>
    private static Truth Released(Rule rule, string subject, IReadOnlyDictionary<string, Measurement> latest, double? thresholdOverride)
    {
        var results = rule.Conditions.Select((c, i) =>
            Check(c, subject, latest, (cond, v) => cond.IsReleased(v, i == 0 ? thresholdOverride : null)));
        return rule.Mode == ConditionMode.Tous ? Any(results) : All(results);
    }

    private static Truth Check(
        RuleCondition condition,
        string subject,
        IReadOnlyDictionary<string, Measurement> latest,
        Func<RuleCondition, double, bool> predicate)
    {
        var pattern = condition.Metric;
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return latest.TryGetValue(pattern, out var m) ? ToTruth(predicate(condition, m.Value)) : Truth.Unknown;
        }

        var bound = Bind(pattern, subject);
        if (bound is not null)
        {
            return latest.TryGetValue(bound, out var m) ? ToTruth(predicate(condition, m.Value)) : Truth.Unknown;
        }

        var values = latest.Where(kv => MetricName.Matches(pattern, kv.Key, out _)).Select(kv => kv.Value.Value).ToList();
        return values.Count == 0 ? Truth.Unknown : ToTruth(values.Any(v => predicate(condition, v)));
    }

    /// <summary>Remplace les <c>*</c> du motif par les segments du sujet quand leur nombre correspond.</summary>
    private static string? Bind(string pattern, string subject)
    {
        if (subject.Length == 0)
        {
            return null;
        }

        var captures = subject.Split('.');
        var parts = pattern.Split('.');
        if (parts.Count(p => p == "*") != captures.Length)
        {
            return null;
        }

        var index = 0;
        return string.Join('.', parts.Select(p => p == "*" ? captures[index++] : p));
    }

    private static Truth ToTruth(bool value) => value ? Truth.True : Truth.False;

    private static Truth All(IEnumerable<Truth> values)
    {
        var unknown = false;
        foreach (var value in values)
        {
            if (value == Truth.False)
            {
                return Truth.False;
            }

            unknown |= value == Truth.Unknown;
        }

        return unknown ? Truth.Unknown : Truth.True;
    }

    private static Truth Any(IEnumerable<Truth> values)
    {
        var unknown = false;
        foreach (var value in values)
        {
            if (value == Truth.True)
            {
                return Truth.True;
            }

            unknown |= value == Truth.Unknown;
        }

        return unknown ? Truth.Unknown : Truth.False;
    }

    private sealed class RuleState
    {
        public DateTimeOffset? BreachSince { get; set; }

        public bool Open { get; set; }
    }
}
