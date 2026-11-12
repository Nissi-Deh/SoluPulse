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
            .AddText(Loc.T(Prefix(alert.Severity)) + Loc.T(alert.Title))
            .AddText(FirstSentence(Loc.T(alert.SimpleExplanation)))
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
        if (!Once("resume-affiche.txt", stamp))
        {
            return;
        }

        new ToastContentBuilder()
            .AddArgument("action", ViewAction)
            .AddText(Loc.T(summary.Title))
            .AddText(Loc.T(summary.Text))
            .Show();
    }

    /// <summary>Nouvelle version de SoluPulse : une seule notification par version.</summary>
    public static void ShowUpdate(UpdateInfo update)
    {
        if (!update.Available || !Once("version-annoncee.txt", update.Version))
        {
            return;
        }

        new ToastContentBuilder()
            .AddArgument("action", ViewAction)
            .AddText(Loc.T($"SoluPulse {update.Version} est disponible"))
            .AddText(Loc.T("Ouvrez les paramètres de SoluPulse pour l'installer."))
            .Show();
    }

    /// <summary>Vrai la première fois qu'une valeur est vue, d'une session à l'autre (fichier dans le profil).</summary>
    private static bool Once(string file, string value)
    {
        var marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoluPulse", file);
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == value)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sans fichier de suivi, la notification peut revenir : ce n'est pas grave.
        }

        return true;
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
