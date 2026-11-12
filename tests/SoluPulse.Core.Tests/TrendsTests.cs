using SoluPulse.Core.Collection;
using SoluPulse.Core.Corrections;

namespace SoluPulse.Core.Tests;

public sealed class TrendsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static List<TrendPoint> Series(params double[] values) =>
        values.Select((v, i) => new TrendPoint(Start.AddDays(i), v)).ToList();

    [Fact]
    public void IncreaseMeasuresHowMuchACounterGrewOverThePeriod()
    {
        Assert.Equal(4, Trends.Increase(Series(8, 8, 9, 12)));
        Assert.Equal(0, Trends.Increase(Series(5, 5, 5)));
        Assert.Null(Trends.Increase([]));
    }

    [Fact]
    public void MeanShiftNeedsEnoughPointsOnBothSides()
    {
        var recent = Enumerable.Repeat(new TrendPoint(Start, 70), 60).ToList();
        var baseline = Enumerable.Repeat(new TrendPoint(Start, 60), 60).ToList();

        Assert.Equal(10, Trends.MeanShift(recent, baseline, minPoints: 50));
        Assert.Null(Trends.MeanShift(recent, baseline.Take(10).ToList(), minPoints: 50));
    }

    [Fact]
    public void DaysUntilZeroProjectsTheFillingSpeed()
    {
        // 2 points de pourcentage perdus par jour, 20 % restants : 10 jours.
        Assert.Equal(10, Trends.DaysUntilZero(Series(30, 28, 26, 24, 22, 20), TimeSpan.FromDays(3), cap: 365));
        Assert.Equal(365, Trends.DaysUntilZero(Series(20, 21, 20, 21, 22), TimeSpan.FromDays(3), cap: 365));
        Assert.Null(Trends.DaysUntilZero(Series(30, 28), TimeSpan.FromDays(3), cap: 365));
    }

    [Fact]
    public void DaysUntilProjectsWhenATargetIsReached()
    {
        // La santé de la batterie perd 0,1 point par jour : de 70,1 à 60 en 101 jours.
        var health = Enumerable.Range(0, 60).Select(i => new TrendPoint(Start.AddDays(i), 76 - (i * 0.1))).ToList();

        Assert.Equal(101, Trends.DaysUntil(health, 60, TimeSpan.FromDays(30), cap: 1825));
        Assert.Equal(0, Trends.DaysUntil(Series(59, 58, 57, 56), 60, TimeSpan.FromDays(3), cap: 1825));
        Assert.Equal(1825, Trends.DaysUntil(Series(80, 80, 81, 80), 60, TimeSpan.FromDays(3), cap: 1825));
        Assert.Null(Trends.DaysUntil(Series(80, 79), 60, TimeSpan.FromDays(30), cap: 1825));
    }

    [Fact]
    public void ShareAtLeastCountsTheTimeSpentFull()
    {
        var points = Series([.. Enumerable.Repeat(100.0, 80), .. Enumerable.Repeat(60.0, 20)]);

        Assert.Equal(80, Trends.ShareAtLeast(points, 97, minPoints: 50));
        Assert.Null(Trends.ShareAtLeast(points.Take(10).ToList(), 97, minPoints: 50));
    }

    [Fact]
    public void WipedSpotsRestorePointsThatAllDisappeared()
    {
        Assert.Equal(6, Trends.Wiped(Series(5, 6, 6, 0)));
        Assert.Equal(0, Trends.Wiped(Series(5, 6, 4)));
        Assert.Equal(0, Trends.Wiped(Series(0)));
    }

    [Fact]
    public void PreventionGuidesNeverModifyTheSystemAndAreInTheCatalog()
    {
        Assert.All(PreventionGuides.All, g =>
        {
            Assert.Equal(ActionKind.Guide, g.Descriptor.Kind);
            Assert.Contains(g.Descriptor.Id, ActionIds.All);
            Assert.NotEmpty(g.Descriptor.Steps);
        });
        Assert.Equal(PreventionGuides.All.Count, PreventionGuides.All.Select(g => g.Descriptor.Id).Distinct().Count());
    }
}
