using System.Collections.Concurrent;
using SoluPulse.Core;
using SoluPulse.Core.Collection;
using SoluPulse.Core.Rules;

namespace SoluPulse.Agent.Monitoring;

/// <summary>État en mémoire du service : dernières mesures par collecteur, règles, état du diagnostic.</summary>
public sealed class AgentState
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<Measurement>> _byCollector = new(StringComparer.Ordinal);

    public RuleEngine Engine { get; set; } = new([]);

    public IReadOnlyList<RuleError> RuleErrors { get; set; } = [];

    public DateTimeOffset? LastCollection { get; private set; }

    public bool DiagnosticRunning { get; set; }

    public bool Ready { get; set; }

    /// <summary>
    /// Chaque collecteur remplace ses propres mesures : une mesure qui disparaît (disque débranché,
    /// capteur devenu illisible) n'est plus évaluée.
    /// </summary>
    public void Update(string collector, IReadOnlyList<Measurement> measurements, DateTimeOffset at)
    {
        _byCollector[collector] = measurements;
        LastCollection = at;
    }

    public IReadOnlyDictionary<string, Measurement> Latest()
    {
        var latest = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        foreach (var measurements in _byCollector.Values)
        {
            foreach (var measurement in measurements)
            {
                latest[measurement.Metric] = measurement;
            }
        }

        return latest;
    }

    /// <summary>Collecteurs qui produisent au moins une métrique correspondant au motif.</summary>
    public IReadOnlyList<string> CollectorsFor(IEnumerable<string> metricPatterns)
    {
        var patterns = metricPatterns.ToList();
        return _byCollector
            .Where(kv => kv.Value.Any(m => patterns.Any(p => MetricName.Matches(p, m.Metric, out _))))
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>Un domaine est évalué s'il a au moins une règle dont une métrique est mesurée sur ce PC.</summary>
    public IReadOnlySet<HealthDomain> EvaluatedDomains()
    {
        var metrics = Latest().Keys.ToList();
        return Engine.Rules
            .Where(r => r.Conditions.Any(c => metrics.Any(m => MetricName.Matches(c.Metric, m, out _))))
            .Select(r => r.Domain)
            .ToHashSet();
    }
}
