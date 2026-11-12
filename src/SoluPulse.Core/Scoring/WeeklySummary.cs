namespace SoluPulse.Core.Scoring;

/// <summary>Le résumé « votre PC cette semaine », affiché en notification une fois par semaine.</summary>
public sealed record WeeklySummary(DateTimeOffset CreatedAt, string Title, string Text)
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(7);

    /// <summary>Un texte court, en français simple : le score, ce qui s'est passé, ce qui reste à faire.</summary>
    public static WeeklySummary Compose(
        DateTimeOffset now,
        int score,
        int? previousScore,
        int opened,
        int resolved,
        int corrections,
        int stillActive,
        int stillCritical)
    {
        var trend = previousScore switch
        {
            { } before when score > before => $", en hausse de {score - before} points",
            { } before when score < before => $", en baisse de {before - score} points",
            _ => "",
        };

        var events = opened == 0 && corrections == 0
            ? "Rien à signaler cette semaine."
            : $"Cette semaine : {Count(opened, "problème détecté", "problèmes détectés")}, {Count(resolved, "réglé", "réglés")}, {Count(corrections, "correction réussie", "corrections réussies")}.";

        var remaining = stillActive == 0
            ? "Aucun problème en cours."
            : stillCritical > 0
                ? $"À traiter : {Count(stillActive, "alerte", "alertes")}, dont {Count(stillCritical, "critique", "critiques")}."
                : $"À traiter : {Count(stillActive, "alerte", "alertes")}, sans urgence.";

        var title = stillCritical > 0 ? "Votre PC cette semaine : à surveiller" : "Votre PC cette semaine";
        return new WeeklySummary(now, title, $"Score de santé : {score}/100{trend}. {events} {remaining}");
    }

    private static string Count(int value, string one, string many) => $"{value} {(value is 0 or 1 ? one : many)}";
}
