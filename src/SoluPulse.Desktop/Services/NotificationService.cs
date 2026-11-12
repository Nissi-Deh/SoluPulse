using Microsoft.Toolkit.Uwp.Notifications;
using SoluPulse.Contracts;

namespace SoluPulse.Desktop.Services;

public sealed record NotificationActivation(string Action, string AlertId);

/// <summary>
/// Notifications Windows avec les boutons « Voir » et « Corriger » (EF-IHM-03). « Corriger » ouvre
/// directement la fiche de validation : notification, puis « Valider », soit 2 clics (ENF-ERG-02).
/// </summary>
public sealed class NotificationService
{
    public const string ViewAction = "voir";
    public const string FixAction = "corriger";

    private readonly HashSet<string> _notified = [];

    public event EventHandler<NotificationActivation>? Activated;

    public void Initialize()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var arguments = ToastArguments.Parse(e.Argument);
            if (arguments.TryGetValue("alerte", out var alertId))
            {
                var action = arguments.TryGetValue("action", out var a) ? a : ViewAction;
                Activated?.Invoke(this, new NotificationActivation(action, alertId));
            }
        };
    }

    public static bool WasLaunchedByNotification => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    public void ShowAlert(AlertInfo alert)
    {
        // Une alerte n'est notifiée qu'une fois par session de l'application.
        if (!_notified.Add(alert.Id))
        {
            return;
        }

        var builder = new ToastContentBuilder()
            .AddArgument("action", ViewAction)
            .AddArgument("alerte", alert.Id)
            .AddText(Prefix(alert.Severity) + alert.Title)
            .AddText(FirstSentence(alert.SimpleExplanation))
            .AddButton(new ToastButton().SetContent("Voir").AddArgument("action", ViewAction).AddArgument("alerte", alert.Id));

        if (alert.Corrections.Count > 0)
        {
            builder.AddButton(new ToastButton().SetContent("Corriger").AddArgument("action", FixAction).AddArgument("alerte", alert.Id));
        }

        if (alert.Severity == "critique")
        {
            builder.SetToastScenario(ToastScenario.Reminder);
        }

        builder.Show();
    }

    /// <summary>
    /// Résumé hebdomadaire : affiché une seule fois, même si l'application redémarre. La date du dernier
    /// résumé montré est gardée dans le profil de l'utilisateur.
    /// </summary>
    public static void ShowSummary(WeeklySummaryReply summary)
    {
        if (!summary.Available)
        {
            return;
        }

        var stamp = summary.CreatedAt.ToDateTimeOffset().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoluPulse", "resume-affiche.txt");
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans fichier de suivi, le résumé peut réapparaître : ce n'est pas grave.
        }

        new ToastContentBuilder()
            .AddArgument("action", ViewAction)
            .AddText(summary.Title)
            .AddText(summary.Text)
            .Show();
    }

    public static void Uninstall() => ToastNotificationManagerCompat.Uninstall();

    private static string Prefix(string severity) => severity switch
    {
        "critique" => "Critique : ",
        "attention" => "Attention : ",
        _ => "",
    };

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? text[..(end + 1)] : text;
    }
}
