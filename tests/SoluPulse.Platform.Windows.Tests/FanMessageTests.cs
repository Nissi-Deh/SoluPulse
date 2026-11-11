using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class FanMessageTests
{
    [Theory]
    [InlineData("HP", "HP")]
    [InlineData("Hewlett-Packard", "HP")]
    [InlineData("Dell Inc.", "Dell")]
    [InlineData("LENOVO", "Lenovo")]
    [InlineData("System manufacturer O.E.M.", "")]
    [InlineData("To be filled by O.E.M.", "")]
    [InlineData(null, "")]
    [InlineData("  Framework  ", "Framework")]
    public void ManufacturerNamesAreShortened(string? raw, string expected) =>
        Assert.Equal(expected, HardwareSensorsCollector.ShortManufacturer(raw));

    [Fact]
    public void ALaptopFanIsExplainedAsManagedByTheManufacturer()
    {
        var message = HardwareSensorsCollector.FanMessage("HP", portable: true, cpuTemperatureReadable: true);

        Assert.Contains("portable HP fonctionne", message);
        Assert.Contains("surveille la température du processeur", message);
        Assert.DoesNotContain("PawnIO", message);
    }

    [Fact]
    public void ADesktopFanMentionsTheMotherboard()
    {
        var message = HardwareSensorsCollector.FanMessage("", portable: false, cpuTemperatureReadable: true);

        Assert.StartsWith("Les ventilateurs de ce PC sont", message);
        Assert.Contains("carte mère", message);
    }

    [Fact]
    public void WithoutTemperatureTheDriverIsSuspected()
    {
        var message = HardwareSensorsCollector.FanMessage("Dell", portable: true, cpuTemperatureReadable: false);

        Assert.Contains("PawnIO", message);
        Assert.DoesNotContain("surveille", message);
    }
}
