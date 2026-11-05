using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _busy;

    public abstract Task RefreshAsync();

    protected async Task RunAsync(Func<Task> work)
    {
        Busy = true;
        Error = null;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Error = AgentClient.Describe(ex);
        }
        finally
        {
            Busy = false;
        }
    }
}

public sealed record NavigationItem(string Title, string Glyph, PageViewModel Page);

/// <summary>Fenêtre principale : navigation, connexion au service, notifications, ouverture des fiches.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AgentClient _client;
    private readonly NotificationService _notifications;
    private SettingsReply? _settings;

    public MainViewModel(AgentClient client, NotificationService notifications)
    {
        _client = client;
        _notifications = notifications;
        Dashboard = new DashboardViewModel(client, this);
        Alerts = new AlertsViewModel(client, this);
        History = new HistoryViewModel(client);
        Journal = new JournalViewModel(client);
        Inventory = new InventoryViewModel(client);
        Settings = new SettingsViewModel(client, this);
        Navigation =
        [
            new("Tableau de bord", "◉", Dashboard),
            new("Alertes", "⚠", Alerts),
            new("Historique", "📈", History),
            new("Journal des corrections", "✔", Journal),
            new("Inventaire", "☰", Inventory),
            new("Paramètres", "⚙", Settings),
        ];
        _selectedNavigation = Navigation[0];

        client.EventReceived += (_, e) => Dispatcher.UIThread.Post(() => OnAgentEvent(e));
        client.ConnectionChanged += (_, connected) => Dispatcher.UIThread.Post(() => OnConnectionChanged(connected));
        notifications.Activated += (_, activation) => Dispatcher.UIThread.Post(() => _ = OpenAlertAsync(activation.AlertId, activation.Action == NotificationService.FixAction));
    }

    public DashboardViewModel Dashboard { get; }

    public AlertsViewModel Alerts { get; }

    public HistoryViewModel History { get; }

    public JournalViewModel Journal { get; }

    public InventoryViewModel Inventory { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavigationItem> Navigation { get; }

    public ObservableCollection<string> Toasts { get; } = [];

    /// <summary>Ouvre la fiche de correction dans une fenêtre modale ; fourni par la vue.</summary>
    public Func<CorrectionViewModel, Task>? ShowCorrection { get; set; }

    /// <summary>Ramène la fenêtre au premier plan ; fourni par l'application.</summary>
    public Action? BringToFront { get; set; }

    public event EventHandler<StatusReply>? StatusChanged;

    [ObservableProperty]
    private NavigationItem _selectedNavigation;

    [ObservableProperty]
    private bool _connected;

    [ObservableProperty]
    private bool _disconnected = true;

    public PageViewModel CurrentPage => SelectedNavigation.Page;

    partial void OnSelectedNavigationChanged(NavigationItem value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        _ = value.Page.RefreshAsync();
    }

    public void Navigate(PageViewModel page) => SelectedNavigation = Navigation.First(n => n.Page == page);

    public async Task OpenAlertAsync(string alertId, bool fix)
    {
        BringToFront?.Invoke();
        Navigate(Alerts);
        var alert = await Alerts.SelectAsync(alertId);
        if (fix && alert?.Corrections.FirstOrDefault(c => !c.Info.Guide) is { } action)
        {
            await OpenCorrectionAsync(alert, action);
        }
        else if (fix && alert is { Corrections.Count: > 0 })
        {
            await OpenCorrectionAsync(alert, alert.Corrections[0]);
        }
    }

    public async Task OpenCorrectionAsync(AlertItem? alert, ActionItem action)
    {
        if (ShowCorrection is null)
        {
            return;
        }

        var correction = new CorrectionViewModel(_client, action.Info, alert?.Id);
        await ShowCorrection(correction);
        if (correction.Completed)
        {
            await Task.WhenAll(Dashboard.RefreshAsync(), Alerts.RefreshAsync(), Journal.RefreshAsync());
        }
    }

    [RelayCommand]
    private async Task RunDiagnosticAsync()
    {
        Navigate(Dashboard);
        await Dashboard.RunDiagnosticCommand.ExecuteAsync(null);
    }

    public void ApplySettings(SettingsReply settings) => _settings = settings;

    private void OnConnectionChanged(bool connected)
    {
        Connected = connected;
        Disconnected = !connected;
        if (connected)
        {
            _ = LoadSettingsAsync();
            _ = CurrentPage.RefreshAsync();
        }
    }

    private void OnAgentEvent(AgentEvent agentEvent)
    {
        switch (agentEvent.Kind)
        {
            case "etat" when agentEvent.Status is not null:
                Dashboard.Apply(agentEvent.Status);
                StatusChanged?.Invoke(this, agentEvent.Status);
                break;
            case "alerte_ouverte" when agentEvent.Alert is not null:
                if (ShouldNotify(agentEvent.Alert))
                {
                    _notifications.ShowAlert(agentEvent.Alert);
                }

                _ = Dashboard.RefreshAsync();
                _ = Alerts.RefreshAsync();
                break;
            case "alerte_resolue":
                _ = Dashboard.RefreshAsync();
                _ = Alerts.RefreshAsync();
                break;
        }
    }

    private bool ShouldNotify(AlertInfo alert)
    {
        if (_settings is { NotificationsEnabled: false })
        {
            return false;
        }

        static int Rank(string severity) => severity switch { "critique" => 2, "attention" => 1, _ => 0 };
        return Rank(alert.Severity) >= Rank(_settings?.NotificationMinSeverity ?? "attention");
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _client.Api.GetSettingsAsync(AgentClient.Empty);
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException)
        {
            // Les paramètres par défaut s'appliquent jusqu'à la prochaine connexion.
        }
    }
}
