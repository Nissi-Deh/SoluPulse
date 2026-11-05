using SoluPulse.Core.Rules;

namespace SoluPulse.Core.Tests;

public sealed class RuleLoaderTests
{
    private static readonly HashSet<string> Actions = ["ACT-CLEAN-TEMP", "ACT-CLEAN-WU-CACHE"];

    // L'exemple de règle du cahier des charges (§6.6) doit se charger tel quel.
    private const string SpecExample = """
        id: HW-DISK-001
        domaine: materiel
        titre: Disque système presque plein
        gravite: critique
        condition:
          metrique: disque.systeme.espace_libre_pct
          operateur: "<"
          seuil: 10
          duree_min: 10m
        cause_probable: Fichiers temporaires et caches accumulés
        explication_simple: >
          Votre disque principal est presque plein.
          Windows peut ralentir et les mises à jour peuvent échouer.
        corrections: [ACT-CLEAN-TEMP, ACT-CLEAN-WU-CACHE]
        """;

    [Fact]
    public void LoadsTheSpecificationExample()
    {
        var set = RuleLoader.LoadFiles([("disque.yaml", SpecExample)], Actions);

        Assert.Empty(set.Errors);
        var rule = Assert.Single(set.Rules);
        Assert.Equal("HW-DISK-001", rule.Id);
        Assert.Equal(HealthDomain.Materiel, rule.Domain);
        Assert.Equal(Severity.Critique, rule.Severity);
        Assert.Equal(TimeSpan.FromMinutes(10), rule.MinDuration);
        Assert.Equal(ComparisonOperator.LessThan, rule.PrimaryCondition.Operator);
        Assert.Equal(10, rule.PrimaryCondition.Threshold);
        Assert.Equal(["ACT-CLEAN-TEMP", "ACT-CLEAN-WU-CACHE"], rule.Corrections);
        Assert.StartsWith("Votre disque principal", rule.SimpleExplanation, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsSeveralRulesFromOneFileWithCombinedConditions()
    {
        const string yaml = """
            regles:
              - id: HW-CPU-002
                domaine: materiel
                titre: Processeur chaud et ventilateur lent
                gravite: attention
                conditions:
                  tous:
                    - { metrique: cpu.temperature_c, operateur: ">", seuil: 85, duree_min: 2m, hysteresis: 5 }
                    - { metrique: ventilateur.*.tours_min, operateur: "<", seuil: 600 }
                cause_probable: Ventilation encrassée
                explication_simple: Le processeur chauffe et le ventilateur tourne lentement.
              - id: NET-GW-001
                domaine: reseau
                titre: Box injoignable
                gravite: attention
                conditions:
                  au_moins_un:
                    - { metrique: reseau.passerelle.perte_pct, operateur: ">=", seuil: 50 }
                cause_probable: Box éteinte ou Wi-Fi coupé
                explication_simple: Votre PC n'arrive pas à joindre votre box.
            """;

        var set = RuleLoader.LoadFiles([("multi.yaml", yaml)], Actions);

        Assert.Empty(set.Errors);
        Assert.Equal(2, set.Rules.Count);
        Assert.Equal(ConditionMode.Tous, set.Rules[0].Mode);
        Assert.Equal(2, set.Rules[0].Conditions.Count);
        Assert.Equal(5, set.Rules[0].Conditions[0].Hysteresis);
        Assert.Equal(ConditionMode.AuMoinsUn, set.Rules[1].Mode);
    }

    [Theory]
    [InlineData("gravite: grave", "gravite")]
    [InlineData("domaine: cuisine", "domaine")]
    [InlineData("operateur: \"=~\"", "Opérateur")]
    [InlineData("duree_min: bientot", "Durée")]
    [InlineData("corrections: [ACT-FORMAT-C]", "ACT-FORMAT-C")]
    public void RejectsInvalidFields(string replacement, string expectedMessagePart)
    {
        var key = replacement.Split(':')[0];
        var yaml = string.Join('\n', SpecExample.Split('\n').Select(line =>
            line.TrimStart().StartsWith(key + ":", StringComparison.Ordinal)
                ? new string(' ', line.Length - line.TrimStart().Length) + replacement
                : line));

        var set = RuleLoader.LoadFiles([("bad.yaml", yaml)], Actions);

        Assert.Empty(set.Rules);
        Assert.Contains(set.Errors, e => e.Message.Contains(expectedMessagePart, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnknownKeysSoThatTyposAreNotSilentlyIgnored()
    {
        var set = RuleLoader.LoadFiles([("typo.yaml", SpecExample + "\nexplication_simpel: oups\n")], Actions);

        Assert.Empty(set.Rules);
        Assert.Single(set.Errors);
    }

    [Fact]
    public void RejectsDuplicateIdentifiers()
    {
        var set = RuleLoader.LoadFiles([("a.yaml", SpecExample), ("b.yaml", SpecExample)], Actions);

        Assert.Single(set.Rules);
        Assert.Contains(set.Errors, e => e.Message.Contains("déjà utilisé", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1j", 86400)]
    public void ParsesDurations(string text, int expectedSeconds)
    {
        Assert.True(RuleLoader.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    [Fact]
    public void ShippedRuleFilesAreValid()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "rules");
        Assert.True(Directory.Exists(directory), $"Dossier introuvable : {directory}");

        var set = RuleLoader.LoadDirectory(directory, SoluPulse.Core.Corrections.ActionIds.All);

        Assert.Empty(set.Errors);
        Assert.True(set.Rules.Count >= 15, $"Seulement {set.Rules.Count} règles livrées.");
        Assert.Contains(set.Rules, r => r.Domain == HealthDomain.Materiel);
        Assert.Contains(set.Rules, r => r.Domain == HealthDomain.Systeme);
        Assert.Contains(set.Rules, r => r.Domain == HealthDomain.Reseau);
    }
}
