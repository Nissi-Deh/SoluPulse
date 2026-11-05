namespace SoluPulse.Core;

public sealed record HealthScore
{
    public HealthScore(int value)
    {
        if (value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A health score must be between 0 and 100.");
        }

        Value = value;
    }

    public int Value { get; }

    public static HealthScore Clamp(double value) => new((int)Math.Round(Math.Clamp(value, 0, 100)));
}
