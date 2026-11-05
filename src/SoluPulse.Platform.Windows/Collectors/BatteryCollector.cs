using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// État de la batterie des portables (EF-COL-04) : capacité d'origine, capacité actuelle, cycles.
/// Un PC fixe n'a pas de batterie : ce n'est pas une erreur, aucune mesure n'est produite.
/// </summary>
public sealed class BatteryCollector(TimeProvider timeProvider) : ICollector
{
    private const string WmiScope = @"\\.\root\WMI";

    public string Name => "batterie";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);

        result.Try("alimentation", () =>
        {
            if (NativeMethods.GetSystemPowerStatus(out var status))
            {
                // 128 : aucune batterie dans le système.
                var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
                result.Add("alimentation.secteur", status.AcLineStatus == 1 || !hasBattery ? 1 : 0, "");
                if (hasBattery && status.BatteryLifePercent <= 100)
                {
                    result.Add("batterie.charge_pct", status.BatteryLifePercent, "%");
                }
            }
        });

        var designed = new List<double>();
        var full = new List<double>();
        result.Try("batterie.capacites", () =>
        {
            designed.AddRange(Wmi.Query(WmiScope, "SELECT DesignedCapacity FROM BatteryStaticData").Select(b => (double)Wmi.Get<uint>(b, "DesignedCapacity")));
            full.AddRange(Wmi.Query(WmiScope, "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Select(b => (double)Wmi.Get<uint>(b, "FullChargedCapacity")));
        });

        if (designed.Count == 0 || full.Count == 0 || designed.Sum() <= 0)
        {
            result.Snapshot("batterie", new { Presente = false });
            return Task.FromResult(result.Build());
        }

        var designedTotal = designed.Sum();
        var fullTotal = full.Sum();
        result.Add("batterie.capacite_origine_mwh", designedTotal, "mWh");
        result.Add("batterie.capacite_actuelle_mwh", fullTotal, "mWh");
        result.Add("batterie.sante_pct", Math.Round(Math.Min(100, 100 * fullTotal / designedTotal), 1), "%");

        uint? cycles = null;
        result.Try("batterie.cycles", () =>
        {
            var counts = Wmi.Query(WmiScope, "SELECT CycleCount FROM BatteryCycleCount").Select(b => Wmi.Get<uint>(b, "CycleCount")).ToList();
            if (counts.Count > 0 && counts.Max() > 0)
            {
                cycles = counts.Max();
                result.Add("batterie.cycles", cycles.Value, "");
            }
        });

        result.Snapshot("batterie", new { Presente = true, CapaciteOrigineMwh = designedTotal, CapaciteActuelleMwh = fullTotal, Cycles = cycles });
        return Task.FromResult(result.Build());
    }
}
