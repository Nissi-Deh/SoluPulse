using SoluPulse.Core.Corrections;
using SoluPulse.Core.Scoring;

namespace SoluPulse.Core.Tests;

public sealed class SimpleModeTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456789012", true)]
    [InlineData("123", false)]
    [InlineData("1234567890123", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    public void TechnicianCodesAreFourToTwelveDigits(string code, bool valid) => Assert.Equal(valid, TechnicianLock.IsValidFormat(code));

    [Fact]
    public void TheCodeIsHashedAndVerified()
    {
        var stored = TechnicianLock.Hash("2468");

        Assert.DoesNotContain("2468", stored, StringComparison.Ordinal);
        Assert.NotEqual(stored, TechnicianLock.Hash("2468"));
        Assert.True(TechnicianLock.Matches("2468", stored));
        Assert.False(TechnicianLock.Matches("2469", stored));
        Assert.False(TechnicianLock.Matches("2468", "abimé"));
        Assert.False(TechnicianLock.Matches(null, stored));
    }

    [Fact]
    public void FiveWrongCodesLockTheTechnicianCodeForFifteenMinutes()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gate = new TechnicianLock(time);
        var stored = TechnicianLock.Hash("2468");

        for (var i = 0; i < TechnicianLock.MaxFailures - 1; i++)
        {
            Assert.Equal(TechnicianCheck.Refuse, gate.Verify("0000", stored));
        }

        Assert.Equal(TechnicianCheck.Bloque, gate.Verify("0000", stored));
        Assert.Equal(TechnicianCheck.Bloque, gate.Verify("2468", stored));

        time.Advance(TechnicianLock.LockDuration);
        Assert.Equal(TechnicianCheck.Accepte, gate.Verify("2468", stored));
    }

    [Fact]
    public void OnlyRiskySystemChangesAreReservedToTheTechnician()
    {
        var gate = new TechnicianLock(TimeProvider.System) { SimpleMode = true };
        ActionDescriptor Descriptor(RiskLevel risk, ActionKind kind = ActionKind.Systeme) =>
            new("ACT-TEST", "Test", "Test", "Test", ["étape"], risk, false, false, TimeSpan.FromMinutes(1), kind);

        Assert.True(gate.IsTechnicianOnly(Descriptor(RiskLevel.Moyen)));
        Assert.True(gate.IsTechnicianOnly(Descriptor(RiskLevel.Eleve)));
        Assert.False(gate.IsTechnicianOnly(Descriptor(RiskLevel.Faible)));
        Assert.False(gate.IsTechnicianOnly(Descriptor(RiskLevel.Moyen, ActionKind.Guide)));

        gate.SimpleMode = false;
        Assert.False(gate.IsTechnicianOnly(Descriptor(RiskLevel.Eleve)));
    }

    [Fact]
    public void TheWeeklySummaryIsShortAndPlain()
    {
        var now = DateTimeOffset.UtcNow;

        var busy = WeeklySummary.Compose(now, 78, 85, opened: 3, resolved: 2, corrections: 1, stillActive: 2, stillCritical: 1);
        var quiet = WeeklySummary.Compose(now, 96, 96, 0, 0, 0, 0, 0);

        Assert.Equal("Votre PC cette semaine : à surveiller", busy.Title);
        Assert.Equal("Score de santé : 78/100, en baisse de 7 points. Cette semaine : 3 problèmes détectés, 2 réglés, 1 correction réussie. À traiter : 2 alertes, dont 1 critique.", busy.Text);
        Assert.Equal("Score de santé : 96/100. Rien à signaler cette semaine. Aucun problème en cours.", quiet.Text);
    }
}
