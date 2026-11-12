using SoluPulse.Core.Collection;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

/// <summary>
/// Transforme l'historique en mesures de tendance : hausse des compteurs d'erreurs des disques sur
/// 30 jours, réchauffement du processeur au repos d'un mois sur l'autre, jours avant que le disque de Windows
/// soit plein, temps passé batterie pleine sur secteur, points de restauration effacés d'un coup.
/// </summary>
public sealed class TrendCollector(MeasurementStore measurements, TimeProvider time) : ICollector
{
    public const string CollectorName = "tendances";

    // Compteurs de disque dont la hausse annonce une panne (Backblaze surveille 5, 187, 197 et 198).
    private static readonly string[] DiskCounters =
    [
        ".secteurs_realloues", ".secteurs_en_attente", ".secteurs_irrecuperables", ".erreurs_non_corrigees", ".erreurs_cable", ".erreurs_media",
    ];

    public string Name => CollectorName;

    public TimeSpan Interval => TimeSpan.FromHours(3);

    public async Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, time);
        var now = time.GetUtcNow();
        var metrics = await measurements.ListMetricsAsync(cancellationToken).ConfigureAwait(false);

        await result.TryAsync("compteurs_disque", async () =>
        {
            foreach (var metric in metrics.Select(m => m.Metric).Where(m => m.StartsWith("disque_physique.", StringComparison.Ordinal) && DiskCounters.Any(c => m.EndsWith(c, StringComparison.Ordinal))))
            {
                if (Trends.Increase(await PointsAsync(metric, now.AddDays(-30), now, cancellationToken).ConfigureAwait(false)) is { } increase)
                {
                    result.Add($"{metric}_hausse_30j", increase, "");
                }
            }
        }).ConfigureAwait(false);

        await result.TryAsync("temperature", async () =>
        {
            // Température au repos seulement : un usage plus intensif ne passe pas pour de la poussière.
            var recent = await PointsAsync("cpu.temperature_repos_c", now.AddDays(-7), now, cancellationToken).ConfigureAwait(false);
            var baseline = await PointsAsync("cpu.temperature_repos_c", now.AddDays(-90), now.AddDays(-30), cancellationToken).ConfigureAwait(false);
            if (Trends.MeanShift(recent, baseline, minPoints: 50) is { } shift)
            {
                result.Add("cpu.temperature_repos_hausse_c", shift, "°C");
            }
        }).ConfigureAwait(false);

        await result.TryAsync("remplissage", async () =>
        {
            var free = await PointsAsync("disque.systeme.espace_libre_pct", now.AddDays(-14), now, cancellationToken).ConfigureAwait(false);
            // Une semaine d'historique au moins : un gros téléchargement isolé ne fait pas une tendance.
            if (Trends.DaysUntilZero(free, TimeSpan.FromDays(7), cap: 365) is { } days)
            {
                result.Add("disque.systeme.jours_avant_plein", days, "jours");
            }
        }).ConfigureAwait(false);

        await result.TryAsync("batterie", async () =>
        {
            // Pleine en permanence : le portable reste branché à 100 %, ce qui use et fait gonfler la batterie.
            var charge = await PointsAsync("batterie.charge_pct", now.AddDays(-14), now, cancellationToken).ConfigureAwait(false);
            if (charge.Count > 0 && charge[^1].Time - charge[0].Time >= TimeSpan.FromDays(5) && Trends.ShareAtLeast(charge, 97, minPoints: 50) is { } share)
            {
                result.Add("batterie.temps_a_100_pct", share, "%");
            }
        }).ConfigureAwait(false);

        await result.TryAsync("points_restauration", async () =>
        {
            var points = await PointsAsync("systeme.points_restauration", now.AddDays(-3), now, cancellationToken).ConfigureAwait(false);
            if (points.Count > 0)
            {
                result.Add("systeme.points_restauration_effaces", Trends.Wiped(points), "");
            }
        }).ConfigureAwait(false);

        return result.Build();
    }

    private async Task<IReadOnlyList<TrendPoint>> PointsAsync(string metric, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        (await measurements.HistoryAsync(metric, from, to, 500, cancellationToken).ConfigureAwait(false))
            .Select(p => new TrendPoint(p.Timestamp, p.Value))
            .ToList();
}
