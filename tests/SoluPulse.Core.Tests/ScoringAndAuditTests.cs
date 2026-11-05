using SoluPulse.Core.Audit;
using SoluPulse.Core.Collection;
using SoluPulse.Core.Scoring;

namespace SoluPulse.Core.Tests;

public sealed class ScoringAndAuditTests
{
    private static readonly HashSet<HealthDomain> V1Domains = [HealthDomain.Materiel, HealthDomain.Systeme, HealthDomain.Reseau];

    private static Alert Active(HealthDomain domain, Severity severity) =>
        new(Guid.NewGuid(), "R-1", "", domain, severity, AlertState.Active, Measurements.T0, null, null);

    [Fact]
    public void AHealthyPcScores100()
    {
        var report = HealthScoreCalculator.Compute([], V1Domains);

        Assert.Equal(100, report.Global.Value);
        Assert.All(report.Domains.Where(d => d.Evaluated), d => Assert.Equal(100, d.Score.Value));
        Assert.False(report.Domains.Single(d => d.Domain == HealthDomain.Securite).Evaluated);
    }

    [Fact]
    public void ACriticalAlertWeighsOnTheGlobalScore()
    {
        var report = HealthScoreCalculator.Compute([Active(HealthDomain.Materiel, Severity.Critique)], V1Domains);

        Assert.Equal(65, report.Domains.Single(d => d.Domain == HealthDomain.Materiel).Score.Value);
        Assert.Equal(88, report.Global.Value);
        Assert.Equal(Severity.Critique, report.Domains.Single(d => d.Domain == HealthDomain.Materiel).WorstSeverity);
    }

    [Fact]
    public void TheGlobalScoreStaysCloseToTheWorstDomain()
    {
        var alerts = Enumerable.Range(0, 3).Select(_ => Active(HealthDomain.Materiel, Severity.Critique)).ToList();

        var report = HealthScoreCalculator.Compute(alerts, V1Domains);

        Assert.Equal(0, report.Domains.Single(d => d.Domain == HealthDomain.Materiel).Score.Value);
        Assert.Equal(25, report.Global.Value);
    }

    [Fact]
    public void ResolvedAlertsDoNotCount()
    {
        var resolved = Active(HealthDomain.Reseau, Severity.Critique) with { State = AlertState.Resolue };

        Assert.Equal(100, HealthScoreCalculator.Compute([resolved], V1Domains).Global.Value);
    }

    [Fact]
    public void AnIntactAuditChainVerifies()
    {
        var chain = BuildChain(3);

        Assert.Null(AuditChain.FindFirstBrokenEntry(chain));
    }

    [Fact]
    public void ATamperedAuditEntryIsDetected()
    {
        var chain = BuildChain(3);
        chain[1] = chain[1] with { Payload = chain[1].Payload.Replace("ACT-LOW", "ACT-HIGH", StringComparison.Ordinal) };

        Assert.Equal(2, AuditChain.FindFirstBrokenEntry(chain));
    }

    [Fact]
    public void ADeletedAuditEntryIsDetected()
    {
        var chain = BuildChain(3);
        chain.RemoveAt(1);

        Assert.Equal(2, AuditChain.FindFirstBrokenEntry(chain));
    }

    [Theory]
    [InlineData("Disque Système", "disque_systeme")]
    [InlineData("Wi-Fi 2", "wi_fi_2")]
    [InlineData("C:\\", "c")]
    public void NormalizesMetricSegments(string label, string expected) =>
        Assert.Equal(expected, MetricName.Segment(label));

    [Fact]
    public void MatchesWildcardMetrics()
    {
        Assert.True(MetricName.Matches("disque.*.espace_libre_pct", "disque.c.espace_libre_pct", out var captured));
        Assert.Equal("c", captured);
        Assert.False(MetricName.Matches("disque.*.espace_libre_pct", "disque.c.usure_pct", out _));
    }

    private static List<(long Sequence, string Payload, string Previous, string Hash)> BuildChain(int count)
    {
        var chain = new List<(long, string, string, string)>();
        var previous = AuditChain.GenesisHash;
        for (long i = 1; i <= count; i++)
        {
            var payload = AuditChain.Serialize(new AuditEntry(Measurements.T0, "Execution", "ACT-LOW", Guid.NewGuid(), null, "Utilisateur", "alice", Measurements.T0, "Réussie", null));
            var hash = AuditChain.ComputeHash(i, previous, payload);
            chain.Add((i, payload, previous, hash));
            previous = hash;
        }

        return chain;
    }
}
