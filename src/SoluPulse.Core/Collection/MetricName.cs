using System.Globalization;
using System.Text;

namespace SoluPulse.Core.Collection;

/// <summary>
/// Les métriques sont des noms à segments séparés par des points, en minuscules et sans accents,
/// par exemple <c>disque.systeme.espace_libre_pct</c>.
/// </summary>
public static class MetricName
{
    public static string Normalize(string metric) =>
        string.Join('.', metric.Split('.').Select(Segment));

    /// <summary>Transforme un libellé libre (nom de disque, de carte réseau…) en segment de métrique.</summary>
    public static string Segment(string label)
    {
        var decomposed = label.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '*' ? c : '_');
        }

        var segment = builder.ToString().Trim('_');
        while (segment.Contains("__", StringComparison.Ordinal))
        {
            segment = segment.Replace("__", "_", StringComparison.Ordinal);
        }

        return segment.Length == 0 ? "_" : segment;
    }

    /// <summary>Vérifie si une métrique correspond à un motif où <c>*</c> remplace un segment.</summary>
    public static bool Matches(string pattern, string metric, out string? captured)
    {
        captured = null;
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, metric, StringComparison.Ordinal);
        }

        var patternParts = pattern.Split('.');
        var metricParts = metric.Split('.');
        if (patternParts.Length != metricParts.Length)
        {
            return false;
        }

        List<string>? captures = null;
        for (var i = 0; i < patternParts.Length; i++)
        {
            if (patternParts[i] == "*")
            {
                (captures ??= []).Add(metricParts[i]);
            }
            else if (!string.Equals(patternParts[i], metricParts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        captured = captures is null ? null : string.Join('.', captures);
        return true;
    }
}
