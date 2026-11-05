using SoluPulse.Core.Collection;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void TheCatalogImplementsExactlyTheV1Actions()
    {
        var catalog = new ActionCatalog(WindowsPlatform.Actions());

        Assert.Equal(ActionIds.All.Order(), catalog.Ids.Order());
    }

    [Fact]
    public void EveryActionDescribesItselfForTheValidationCard()
    {
        foreach (var descriptor in new ActionCatalog(WindowsPlatform.Actions()).Descriptors)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Title), descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description), descriptor.Id);
            Assert.NotEmpty(descriptor.Steps);
            Assert.True(descriptor.EstimatedDuration > TimeSpan.Zero, descriptor.Id);
        }
    }

    [Fact]
    public void ActionsWithParametersOfferChoicesInsteadOfFreeText()
    {
        foreach (var action in WindowsPlatform.Actions().Where(a => a.Descriptor.ParameterNames is { Count: > 0 }))
        {
            Assert.IsAssignableFrom<IActionParameterChoices>(action);
        }
    }

    [Fact]
    public void ParsesSmartAttributes()
    {
        var data = new byte[512];
        WriteAttribute(data, 0, SmartAttributes.ReallocatedSectors, 8);
        WriteAttribute(data, 1, SmartAttributes.PendingSectors, 0x0102);
        WriteAttribute(data, 2, 9, 12345);

        var attributes = SmartAttributes.Parse(data);

        Assert.Equal(8, attributes[SmartAttributes.ReallocatedSectors]);
        Assert.Equal(0x0102, attributes[SmartAttributes.PendingSectors]);
        Assert.Equal(12345, attributes[9]);
        Assert.False(attributes.ContainsKey(SmartAttributes.UncorrectableSectors));
    }

    // Les collecteurs tournent sur la machine de test : ils ne doivent jamais lever d'exception,
    // même sans droits administrateur ou sans capteur (EF-COL-12, ENF-FIA-04).
    [Fact]
    public async Task CollectorsNeverThrowAndProduceNormalizedMetrics()
    {
        var collectors = WindowsPlatform.Collectors(TimeProvider.System, new NetworkProbeOptions { InternetProbes = false })
            .Where(c => c is not WindowsUpdateCollector and not HardwareSensorsCollector);

        foreach (var collector in collectors)
        {
            var result = await collector.CollectAsync(default);

            Assert.All(result.Measurements, m => Assert.Equal(MetricName.Normalize(m.Metric), m.Metric));
        }
    }

    [Fact]
    public async Task PerformanceCollectorMeasuresCpuOnTheSecondRun()
    {
        var collector = new PerformanceCollector(TimeProvider.System);

        await collector.CollectAsync(default);
        await Task.Delay(200);
        var result = await collector.CollectAsync(default);

        Assert.Contains(result.Measurements, m => m.Metric == "cpu.utilisation_pct" && m.Value is >= 0 and <= 100);
        Assert.Contains(result.Measurements, m => m.Metric == "memoire.utilisation_pct");
    }

    private static void WriteAttribute(byte[] data, int index, byte id, long raw)
    {
        var offset = 2 + (index * 12);
        data[offset] = id;
        for (var i = 0; i < 6; i++)
        {
            data[offset + 5 + i] = (byte)(raw >> (8 * i));
        }
    }
}
