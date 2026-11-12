using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class AppUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static InstalledPrograms.Program Program(string icon, DateOnly? installed = null) =>
        new("Jeu de cartes", "1.0", "Éditeur", null, icon, 512_000, installed ?? new DateOnly(2025, 1, 10), @"""C:\Jeu\unins000.exe""", "Jeu");

    [Fact]
    public void AProgramNotLaunchedForSixMonthsIsUnused()
    {
        var runs = new Dictionary<string, DateTimeOffset> { ["CARTES.EXE"] = Now.AddDays(-200) };

        var usage = AppUsageCollector.Describe(Program(@"C:\Jeu\cartes.exe"), runs, Now)!;

        Assert.True(usage.Unused);
        Assert.Equal(500, usage.SizeMb);
        Assert.Equal(Now.AddDays(-200), usage.LastUsed);
    }

    [Fact]
    public void ARecentlyUsedProgramIsNotUnused()
    {
        var runs = new Dictionary<string, DateTimeOffset> { ["CARTES.EXE"] = Now.AddDays(-3) };

        Assert.False(AppUsageCollector.Describe(Program(@"C:\Jeu\cartes.exe"), runs, Now)!.Unused);
    }

    [Fact]
    public void ANewProgramNeverLaunchedIsNotYetUnused()
    {
        var empty = new Dictionary<string, DateTimeOffset>();

        Assert.False(AppUsageCollector.Describe(Program(@"C:\Jeu\cartes.exe", new DateOnly(2026, 11, 1)), empty, Now)!.Unused);
        Assert.True(AppUsageCollector.Describe(Program(@"C:\Jeu\cartes.exe", new DateOnly(2026, 1, 1)), empty, Now)!.Unused);
    }

    [Fact]
    public void WithoutAKnownProgramFileNothingIsConcluded() =>
        Assert.Null(AppUsageCollector.Describe(Program(@"C:\Jeu\unins000.exe"), new Dictionary<string, DateTimeOffset>(), Now));
}
