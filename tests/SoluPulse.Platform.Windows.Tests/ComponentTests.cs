using System.Text.Json;
using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows.Tests;

public sealed class ComponentTests
{
    [Theory]
    [InlineData(0x061100u, true, true)]   // Defender actif et à jour
    [InlineData(0x060100u, false, true)]  // désactivé
    [InlineData(0x061110u, true, false)]  // actif, définitions périmées
    [InlineData(0x041000u, true, true)]
    public void DecodesSecurityCenterProductState(uint state, bool enabled, bool upToDate)
    {
        var product = SecurityProduct.From("Antivirus", state);

        Assert.Equal(enabled, product.Actif);
        Assert.Equal(upToDate, product.AJour);
    }

    [Theory]
    [InlineData("20260505000000.000000+000", "05/05/2026")]
    [InlineData("2026", null)]
    [InlineData(null, null)]
    public void ReadsWmiDates(string? value, string? expected) =>
        Assert.Equal(expected, HardwareInventoryCollector.DmtfDate(value));

    [Theory]
    [InlineData(26u, "DDR4")]
    [InlineData(34u, "DDR5")]
    [InlineData(999u, "inconnu")]
    public void NamesMemoryTypes(uint smbios, string expected) =>
        Assert.Equal(expected, HardwareInventoryCollector.MemoryType(smbios));

    // S'exécute sur la machine de test : le processeur et la mémoire existent sur tout PC.
    [Fact]
    public async Task InventoryListsTheProcessorAndMemoryOfThisPc()
    {
        var result = await new HardwareInventoryCollector(TimeProvider.System).CollectAsync(default);

        Assert.Empty(result.Issues.Select(i => $"{i.Sensor}: {i.Message}"));
        var snapshot = Assert.Single(result.Snapshots, s => s.Kind == "materiel");
        using var json = JsonDocument.Parse(snapshot.Json);
        var processor = Assert.Single(json.RootElement.GetProperty("Processeurs").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(processor.GetProperty("nom").GetString()));
        Assert.True(processor.GetProperty("coeurs").GetInt32() > 0);
        Assert.True(json.RootElement.GetProperty("Memoire").GetProperty("totaleGo").GetDouble() > 0);
        Assert.Contains(result.Measurements, m => m.Metric == "memoire.barrettes" && m.Value > 0);
    }

    [Fact]
    public async Task SecurityCollectorReportsTheFirewallAndUac()
    {
        var result = await new SecurityCollector(TimeProvider.System).CollectAsync(default);

        Assert.Contains(result.Measurements, m => m.Metric == "securite.pare_feu.profils_desactives");
        Assert.Contains(result.Measurements, m => m.Metric == "securite.uac_actif");
        Assert.Single(result.Snapshots, s => s.Kind == "securite");
    }
}
