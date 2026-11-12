using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// État de la batterie des portables (EF-COL-04) : capacité d'origine, capacité actuelle, cycles.
/// Sur un PC fixe, une batterie vue par Windows est celle d'un onduleur relié en USB : sa charge,
/// son autonomie et sa santé sont suivies à part. Un PC fixe sans onduleur n'est pas une erreur.
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

        var portable = Chassis.IsPortable();
        if (portable == false)
        {
            CollectUps(result, designed, full);
            return Task.FromResult(result.Build());
        }

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

        // Puissance de charge et de décharge : un chargeur trop faible charge lentement, même PC éteint.
        double? chargeWatts = null;
        result.Try("batterie.puissance", () =>
        {
            foreach (var status in Wmi.Query(WmiScope, "SELECT Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus"))
            {
                var charging = Wmi.Get<bool>(status, "Charging");
                var rate = charging ? Wmi.Get<int>(status, "ChargeRate") : Wmi.Get<int>(status, "DischargeRate");
                if (rate is > 0 and < 500_000)
                {
                    var watts = Math.Round(rate / 1000.0, 1);
                    result.Add(charging ? "batterie.puissance_charge_w" : "batterie.puissance_decharge_w", watts, "W");
                    chargeWatts = charging ? watts : chargeWatts;
                }
            }
        });

        result.Snapshot("batterie", new { Presente = true, CapaciteOrigineMwh = designedTotal, CapaciteActuelleMwh = fullTotal, Cycles = cycles, PuissanceChargeW = chargeWatts });
        return Task.FromResult(result.Build());
    }

    /// <summary>
    /// Onduleur relié en USB : Windows le présente comme une batterie. Son autonomie à pleine charge
    /// révèle une batterie morte avant la prochaine coupure de courant.
    /// </summary>
    private static void CollectUps(CollectorResultBuilder result, List<double> designed, List<double> full)
    {
        result.Try("onduleur", () =>
        {
            var batteries = Wmi.Query(@"\\.\root\cimv2", "SELECT Name, EstimatedChargeRemaining, EstimatedRunTime FROM Win32_Battery");
            result.Add("onduleur.present", batteries.Count > 0 ? 1 : 0, "");
            if (batteries.Count == 0)
            {
                result.Snapshot("onduleur", new { Present = false });
                return;
            }

            var ups = batteries[0];
            var charge = Wmi.Get<ushort>(ups, "EstimatedChargeRemaining");
            var runtime = Wmi.Get<uint>(ups, "EstimatedRunTime");
            if (charge is > 0 and <= 100)
            {
                result.Add("onduleur.charge_pct", charge, "%");
            }

            // 71 582 788 minutes : valeur « inconnue » renvoyée quand l'onduleur est sur secteur sans estimation.
            if (runtime is > 0 and < 10_000)
            {
                result.Add("onduleur.autonomie_min", runtime, "min");
            }

            double? health = null;
            if (designed.Sum() > 0 && full.Sum() > 0)
            {
                health = Math.Round(Math.Min(100, 100 * full.Sum() / designed.Sum()), 1);
                result.Add("onduleur.sante_pct", health.Value, "%");
            }

            result.Snapshot("onduleur", new { Present = true, Nom = Wmi.Get<string>(ups, "Name"), ChargePct = charge, AutonomieMin = runtime is > 0 and < 10_000 ? runtime : (uint?)null, SantePct = health });
        });
    }
}
