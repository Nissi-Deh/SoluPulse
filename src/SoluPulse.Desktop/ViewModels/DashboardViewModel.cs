using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>Tableau de bord : score de santé, quatre domaines, alertes actives (EF-IHM-01).</summary>
public sealed partial class DashboardViewModel(AgentClient client, MainViewModel main) : PageViewModel
{
    public override string Title => "Tableau de bord";

    public ObservableCollection<DomainItem> Domains { get; } = [];

    public ObservableCollection<AlertItem> ActiveAlerts { get; } = [];

    public ObservableCollection<string> SensorIssues { get; } = [];

    [ObservableProperty]
    private string _scoreText = "—";

    [ObservableProperty]
    private double _scoreValue;

    [ObservableProperty]
    private IBrush _scoreBrush = new SolidColorBrush(Palette.Unknown);

    [ObservableProperty]
    private string _verdict = "Connexion au service SoluPulse…";

    [ObservableProperty]
    private string _lastCollection = "";

    [ObservableProperty]
    private bool _diagnosticRunning;

    [ObservableProperty]
    private bool _hasAlerts;

    [ObservableProperty]
    private bool _hasSensorIssues;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var status = await client.Api.GetStatusAsync(AgentClient.Empty);
        var alerts = await client.Api.ListAlertsAsync(new ListAlertsRequest());
        Apply(status);
        ActiveAlerts.Clear();
        foreach (var alert in alerts.Alerts)
        {
            ActiveAlerts.Add(new AlertItem(alert));
        }

        HasAlerts = ActiveAlerts.Count > 0;
    });

    public void Apply(StatusReply status)
    {
        ScoreValue = status.GlobalScore;
        ScoreText = status.GlobalScore.ToString(Format.French);
        ScoreBrush = new SolidColorBrush(Palette.ForScore(status.GlobalScore));
        Verdict = status.GlobalScore switch
        {
            >= 80 when status.ActiveAlerts == 0 => "Votre PC est en bonne santé.",
            >= 80 => "Votre PC va bien, quelques points sont à regarder.",
            >= 50 => "Votre PC a des problèmes à corriger.",
            _ => "Votre PC a des problèmes importants.",
        };
        LastCollection = $"Dernière mesure {Format.Ago(status.LastCollection)} · service {status.AgentVersion}";
        DiagnosticRunning = status.DiagnosticRunning;

        Domains.Clear();
        foreach (var domain in status.Domains)
        {
            Domains.Add(new DomainItem(domain));
        }

        SensorIssues.Clear();
        foreach (var issue in status.SensorIssues)
        {
            SensorIssues.Add(issue.Message);
        }

        HasSensorIssues = SensorIssues.Count > 0;
    }

    [RelayCommand]
    private Task RunDiagnosticAsync() => RunAsync(async () =>
    {
        DiagnosticRunning = true;
        try
        {
            var status = await client.Api.RunDiagnosticAsync(AgentClient.Empty, deadline: DateTime.UtcNow.AddMinutes(5));
            Apply(status);
        }
        finally
        {
            DiagnosticRunning = false;
        }

        await RefreshAsync();
    });

    [RelayCommand]
    private Task OpenAlertAsync(AlertItem alert) => main.OpenAlertAsync(alert.Id, fix: false);

    [RelayCommand]
    private void ShowAlerts() => main.Navigate(main.Alerts);
}
