using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class SnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RestorePointService.ManualDescription, "manuelle")]
    [InlineData(RestorePointService.WeeklyDescription, "hebdomadaire")]
    [InlineData("SoluPulse - Réinitialiser Windows Update", "correction")]
    [InlineData("Windows Update", "windows")]
    [InlineData("Installed NVIDIA Graphics Driver", "windows")]
    [InlineData("", "windows")]
    public void OriginIsRecognisedFromTheDescription(string description, string origin) =>
        Assert.Equal(origin, RestorePointService.Origin(description));

    [Fact]
    public void AWeeklyCaptureIsDueAfterSevenDaysOrWithoutAnyPoint()
    {
        Assert.True(RestorePointService.IsWeeklyDue(null, Now));
        Assert.True(RestorePointService.IsWeeklyDue(Now.AddDays(-7), Now));
        Assert.False(RestorePointService.IsWeeklyDue(Now.AddDays(-6.9), Now));
    }

    [Theory]
    [InlineData(@"\\?\Volume{e2a1b5c4-0000-0000-0000-100000000000}\:(C%3A)", @"C:\", true)]
    [InlineData(@"\\?\Volume{e2a1b5c4-0000-0000-0000-100000000000}\:(c%3A)", @"C:\", true)]
    [InlineData(@"\\?\Volume{e2a1b5c4-0000-0000-0000-100000000000}\:(D%3A)", @"C:\", false)]
    public void TheSystemDriveIsFoundInTheProtectedVolumes(string volume, string root, bool expected) =>
        Assert.Equal(expected, RestorePointService.IsSystemDriveProtected([volume], root));

    [Fact]
    public void DescriptionsFitWindowsLimit()
    {
        Assert.True(RestorePointService.ManualDescription.Length <= 64);
        Assert.True(RestorePointService.WeeklyDescription.Length <= 64);
    }

    [Fact]
    public void EnablingProtectionIsHarmlessAndNotAutomatic()
    {
        var d = new EnableSystemProtectionAction().Descriptor;

        Assert.Equal(ActionIds.EnableSystemProtection, d.Id);
        Assert.Equal(RiskLevel.Faible, d.Risk);
        Assert.False(d.Automatic);
        Assert.False(d.Reversible);
        Assert.Empty(d.ParameterNames ?? []);
    }
}
