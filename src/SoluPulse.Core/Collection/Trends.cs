namespace SoluPulse.Core.Collection;

/// <summary>Un point d'historique : date et valeur moyenne sur l'intervalle.</summary>
public readonly record struct TrendPoint(DateTimeOffset Time, double Value);

/// <summary>
/// Calculs de tendance sur l'historique. Une valeur isolée dit peu ; la vitesse à laquelle un
/// compteur augmente prédit mieux une panne (statistiques de Backblaze sur les attributs SMART).
/// Les résultats deviennent des mesures ordinaires, évaluées par les règles comme les autres.
/// </summary>
public static class Trends
{
    /// <summary>Hausse d'un compteur sur la période : dernière valeur moins la plus petite.</summary>
    public static double? Increase(IReadOnlyList<TrendPoint> points) =>
        points.Count == 0 ? null : Math.Max(0, points[^1].Value - points.Min(p => p.Value));

    /// <summary>
    /// Écart entre la moyenne récente et celle d'une période de référence plus ancienne. Il faut assez
    /// de points des deux côtés, sinon l'écart ne veut rien dire.
    /// </summary>
    public static double? MeanShift(IReadOnlyList<TrendPoint> recent, IReadOnlyList<TrendPoint> baseline, int minPoints)
    {
        if (recent.Count < minPoints || baseline.Count < minPoints)
        {
            return null;
        }

        return Math.Round(recent.Average(p => p.Value) - baseline.Average(p => p.Value), 1);
    }

    /// <summary>
    /// Pente en unités par jour (moindres carrés). <c>null</c> sans au moins <paramref name="minSpan"/>
    /// d'historique.
    /// </summary>
    public static double? SlopePerDay(IReadOnlyList<TrendPoint> points, TimeSpan minSpan)
    {
        if (points.Count < 3 || points[^1].Time - points[0].Time < minSpan)
        {
            return null;
        }

        var origin = points[0].Time;
        var xs = points.Select(p => (p.Time - origin).TotalDays).ToArray();
        var meanX = xs.Average();
        var meanY = points.Average(p => p.Value);
        double numerator = 0, denominator = 0;
        for (var i = 0; i < xs.Length; i++)
        {
            numerator += (xs[i] - meanX) * (points[i].Value - meanY);
            denominator += (xs[i] - meanX) * (xs[i] - meanX);
        }

        return Math.Abs(denominator) < 1e-12 ? null : numerator / denominator;
    }

    /// <summary>
    /// Jours avant qu'une valeur qui baisse atteigne zéro (espace libre, par exemple), plafonnés à
    /// <paramref name="cap"/>. Une valeur stable ou qui monte renvoie le plafond.
    /// </summary>
    public static double? DaysUntilZero(IReadOnlyList<TrendPoint> points, TimeSpan minSpan, double cap)
    {
        if (SlopePerDay(points, minSpan) is not { } slope)
        {
            return null;
        }

        var current = points[^1].Value;
        if (slope >= 0 || current <= 0)
        {
            return current <= 0 ? 0 : cap;
        }

        return Math.Round(Math.Min(cap, current / -slope), 0);
    }

    /// <summary>Part du temps (en %) où la valeur atteint au moins <paramref name="threshold"/>.</summary>
    public static double? ShareAtLeast(IReadOnlyList<TrendPoint> points, double threshold, int minPoints) =>
        points.Count < minPoints ? null : Math.Round(100.0 * points.Count(p => p.Value >= threshold) / points.Count, 0);

    /// <summary>
    /// Points de restauration effacés d'un coup : il y en avait, il n'y en a plus aucun. C'est ce que
    /// font les rançongiciels avant de chiffrer les fichiers.
    /// </summary>
    public static double Wiped(IReadOnlyList<TrendPoint> points) =>
        points.Count < 2 || points[^1].Value > 0 ? 0 : points.Max(p => p.Value);
}
