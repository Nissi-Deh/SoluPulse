namespace SoluPulse.Core.Scoring;

public sealed record DomainHealth(HealthDomain Domain, bool Evaluated, HealthScore Score, int ActiveAlerts, Severity? WorstSeverity);

public sealed record HealthReport(HealthScore Global, IReadOnlyList<DomainHealth> Domains);

/// <summary>
/// Score de santé global sur 100 et score par domaine (EF-DIA-07).
/// Chaque alerte active retire des points à son domaine selon sa gravité. Le score global est la
/// moyenne des domaines évalués, plafonnée à 25 points au-dessus du pire domaine : un PC dont le
/// disque est en fin de vie ne peut pas afficher « 90 » parce que le réseau va bien.
/// </summary>
public static class HealthScoreCalculator
{
    public const int CapAboveWorstDomain = 25;

    public static int Penalty(Severity severity) => severity switch
    {
        Severity.Information => 2,
        Severity.Attention => 10,
        Severity.Critique => 35,
        _ => 0,
    };

    public static HealthReport Compute(IEnumerable<Alert> alerts, IReadOnlySet<HealthDomain> evaluatedDomains)
    {
        var active = alerts.Where(a => a.State == AlertState.Active).ToList();
        var domains = Enum.GetValues<HealthDomain>()
            .Select(domain =>
            {
                var domainAlerts = active.Where(a => a.Domain == domain).ToList();
                var score = HealthScore.Clamp(100 - domainAlerts.Sum(a => Penalty(a.Severity)));
                Severity? worst = domainAlerts.Count == 0 ? null : domainAlerts.Max(a => a.Severity);
                return new DomainHealth(domain, evaluatedDomains.Contains(domain), score, domainAlerts.Count, worst);
            })
            .ToList();

        var evaluated = domains.Where(d => d.Evaluated).ToList();
        if (evaluated.Count == 0)
        {
            return new HealthReport(new HealthScore(100), domains);
        }

        var average = evaluated.Average(d => d.Score.Value);
        var worstDomain = evaluated.Min(d => d.Score.Value);
        return new HealthReport(HealthScore.Clamp(Math.Min(average, worstDomain + CapAboveWorstDomain)), domains);
    }
}
