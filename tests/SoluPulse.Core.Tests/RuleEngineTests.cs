using SoluPulse.Core.Rules;

namespace SoluPulse.Core.Tests;

public sealed class RuleEngineTests
{
    private static readonly Rule DiskFull = Rules.Load("""
        id: HW-DISK-001
        domaine: materiel
        titre: Disque presque plein
        gravite: critique
        condition: { metrique: disque.*.espace_libre_pct, operateur: "<", seuil: 10, duree_min: 10m, hysteresis: 2 }
        cause_probable: Fichiers accumulés
        explication_simple: Votre disque est presque plein.
        """);

    private static readonly Rule HotAndSlowFan = Rules.Load("""
        id: HW-CPU-002
        domaine: materiel
        titre: Processeur chaud et ventilateur lent
        gravite: attention
        conditions:
          tous:
            - { metrique: cpu.temperature_c, operateur: ">", seuil: 85 }
            - { metrique: ventilateur.*.tours_min, operateur: "<", seuil: 600 }
        cause_probable: Ventilation encrassée
        explication_simple: Le processeur chauffe.
        """);

    [Fact]
    public void OpensOnlyAfterTheMinimumDuration()
    {
        var engine = new RuleEngine([DiskFull]);
        var low = Measurements.Of(("disque.c.espace_libre_pct", 5));

        Assert.Empty(engine.Evaluate(low, Measurements.T0));
        Assert.Empty(engine.Evaluate(low, Measurements.T0.AddMinutes(9)));
        var opened = Assert.Single(engine.Evaluate(low, Measurements.T0.AddMinutes(10)));

        Assert.Equal(TransitionKind.Opened, opened.Kind);
        Assert.Equal("c", opened.Subject);
        Assert.Equal(5, opened.Value);
    }

    [Fact]
    public void ABriefDipResetsTheDurationCounter()
    {
        var engine = new RuleEngine([DiskFull]);

        engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 5)), Measurements.T0);
        engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 50)), Measurements.T0.AddMinutes(5));

        Assert.Empty(engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 5)), Measurements.T0.AddMinutes(11)));
    }

    [Fact]
    public void NeverOpensTheSameAlertTwice()
    {
        var engine = new RuleEngine([DiskFull]);
        var low = Measurements.Of(("disque.c.espace_libre_pct", 5));
        engine.Evaluate(low, Measurements.T0);

        Assert.Single(engine.Evaluate(low, Measurements.T0.AddMinutes(10)));
        Assert.Empty(engine.Evaluate(low, Measurements.T0.AddMinutes(20)));
        Assert.Empty(engine.Evaluate(low, Measurements.T0.AddHours(5)));
    }

    [Fact]
    public void ClosesOnlyBeyondTheHysteresis()
    {
        var engine = new RuleEngine([DiskFull]);
        engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 5)), Measurements.T0);
        engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 5)), Measurements.T0.AddMinutes(10));

        Assert.Empty(engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 11)), Measurements.T0.AddMinutes(11)));
        var closed = Assert.Single(engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 12)), Measurements.T0.AddMinutes(12)));
        Assert.Equal(TransitionKind.Closed, closed.Kind);
    }

    [Fact]
    public void TracksEachDiskSeparately()
    {
        var engine = new RuleEngine([DiskFull]);
        var values = Measurements.Of(("disque.c.espace_libre_pct", 5), ("disque.d.espace_libre_pct", 80));
        engine.Evaluate(values, Measurements.T0);

        var opened = Assert.Single(engine.Evaluate(values, Measurements.T0.AddMinutes(10)));
        Assert.Equal("c", opened.Subject);
    }

    [Fact]
    public void AMissingSensorNeitherOpensNorClosesAnAlert()
    {
        var engine = new RuleEngine([DiskFull]);
        engine.Restore([new Alert(Guid.NewGuid(), DiskFull.Id, "c", HealthDomain.Materiel, Severity.Critique, AlertState.Active, Measurements.T0, null, 5)]);

        Assert.Empty(engine.Evaluate(Measurements.Of(), Measurements.T0.AddHours(1)));
        Assert.Single(engine.Evaluate(Measurements.Of(("disque.c.espace_libre_pct", 40)), Measurements.T0.AddHours(2)));
    }

    [Fact]
    public void CombinesConditions()
    {
        var engine = new RuleEngine([HotAndSlowFan]);

        Assert.Empty(engine.Evaluate(Measurements.Of(("cpu.temperature_c", 95), ("ventilateur.cpu.tours_min", 1500)), Measurements.T0));
        Assert.Single(engine.Evaluate(Measurements.Of(("cpu.temperature_c", 95), ("ventilateur.cpu.tours_min", 300)), Measurements.T0));
    }

    [Fact]
    public void AppliesUserThresholdOverrides()
    {
        var engine = new RuleEngine([DiskFull]);
        var values = Measurements.Of(("disque.c.espace_libre_pct", 15));
        var overrides = new Dictionary<string, double> { [DiskFull.Id] = 20 };
        engine.Evaluate(values, Measurements.T0, overrides);

        Assert.Single(engine.Evaluate(values, Measurements.T0.AddMinutes(10), overrides));
    }

    [Fact]
    public void RestoreReportsAlertsWhoseRuleNoLongerExists()
    {
        var engine = new RuleEngine([DiskFull]);
        var orphan = new Alert(Guid.NewGuid(), "OLD-RULE-001", "", HealthDomain.Systeme, Severity.Attention, AlertState.Active, Measurements.T0, null, null);

        Assert.Equal([orphan], engine.Restore([orphan]));
    }
}
