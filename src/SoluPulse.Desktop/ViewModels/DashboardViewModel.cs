using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>Tableau de bord : score de santé, quatre domaines, valeurs en direct, alertes actives (EF-IHM-01).</summary>
public sealed partial class DashboardViewModel(AgentClient client, MainViewModel main) : PageViewModel
{
    public override string Title => "Tableau de bord";

    public ObservableCollection<DomainItem> Domains { get; } = [];

    public ObservableCollection<AlertItem> ActiveAlerts { get; } = [];

    public ObservableCollection<GaugeItem> Live { get; } = [];

    public ObservableCollection<string> SensorIssues { get; } = [];

    [ObservableProperty]
    private string _scoreText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreSweep))]
    private double _scoreValue;

    /// <summary>Angle de l'anneau de score : 360° pour 100.</summary>
    public double ScoreSweep => ScoreValue * 3.6;

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

    [ObservableProperty]
    private bool _hasLive;

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
        await RefreshLiveAsync();
    });

    /// <summary>Met à jour les valeurs en direct ; une erreur ici ne doit pas masquer le reste du tableau de bord.</summary>
    public async Task RefreshLiveAsync()
    {
        try
        {
            var metrics = new MetricValues(await client.Api.GetLiveMetricsAsync(AgentClient.Empty));
            Live.Clear();
            foreach (var gauge in ComponentCards.LiveGauges(metrics))
            {
                Live.Add(gauge);
            }

            HasLive = Live.Count > 0;
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException)
        {
            // Les valeurs précédentes restent affichées jusqu'au prochain relevé.
        }
    }

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

    [RelayCommand]
    private void ShowComputer() => main.Navigate(main.Computer);
}
