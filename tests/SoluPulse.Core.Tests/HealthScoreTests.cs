using SoluPulse.Core;

namespace SoluPulse.Core.Tests;

public sealed class HealthScoreTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void AcceptsScoresWithinRange(int value)
    {
        var score = new HealthScore(value);

        Assert.Equal(value, score.Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsScoresOutsideRange(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthScore(value));
    }
}
