using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class DriverTests
{
    [Fact]
    public void ExplainsAMissingDriverAndNamesTheVendor()
    {
        var problem = DeviceProblems.Describe(@"PCI\VEN_8086&DEV_9DF0&SUBSYS_00348086\3&11583659&0&A3", null, 28, "Net",
            [@"PCI\VEN_8086&DEV_9DF0&SUBSYS_00348086&REV_30", @"PCI\VEN_8086&DEV_9DF0"]);

        Assert.Equal("Périphérique inconnu", problem.Name);
        Assert.Equal("Intel", problem.Vendor);
        Assert.Contains("Aucun pilote", problem.Explanation, StringComparison.Ordinal);
        Assert.Contains("Intel", problem.Advice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"USB\VID_0BDA&PID_8179&REV_0000", "Realtek")]
    [InlineData(@"PCI\VEN_10DE&DEV_1F91", "NVIDIA")]
    [InlineData(@"ACPI\PNP0C0A", null)]
    public void RecognisesVendorsFromHardwareIds(string id, string? vendor) => Assert.Equal(vendor, DeviceProblems.VendorOf([id]));

    [Fact]
    public void UnknownCodesStillGetAnExplanation()
    {
        var problem = DeviceProblems.Describe("X", "Capteur", 99, null, []);

        Assert.Contains("code 99", problem.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverMustMatchOneOfTheDeviceIdsExactly()
    {
        string[] ids = [@"PCI\VEN_8086&DEV_9DF0&SUBSYS_00348086&REV_30", @"PCI\VEN_8086&DEV_9DF0"];

        Assert.True(DriverUpdates.Matches(@"pci\ven_8086&dev_9df0", ids));
        Assert.False(DriverUpdates.Matches(@"PCI\VEN_8086&DEV_9DF1", ids));
        Assert.False(DriverUpdates.Matches(null, ids));
    }

    [Fact]
    public async Task DriverInstallRejectsADeviceThatIsNotInError()
    {
        var action = new InstallDriverAction();
        var context = new ActionContext(new Dictionary<string, string> { [InstallDriverAction.DeviceParameter] = @"ROOT\N_EXISTE_PAS\0000" }, new CallerIdentity("S-1-5-21-1", "test"));

        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(context, default));
    }

    [Fact]
    public void InstallingADriverNeedsARestorePoint()
    {
        Assert.True(new InstallDriverAction().Descriptor.RestorePointMandatory);
        Assert.False(new RescanDevicesAction().Descriptor.RequiresRestorePoint);
    }
}
