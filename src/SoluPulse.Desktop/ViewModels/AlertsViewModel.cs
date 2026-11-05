using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>Alertes et leur fiche explicative, en français simple avec l'explication technique en option (EF-IHM-02).</summary>
public sealed partial class AlertsViewModel(AgentClient client, MainViewModel main) : PageViewModel
{
    public override string Title => "Alertes";

    public ObservableCollection<AlertItem> Items { get; } = [];

    [ObservableProperty]
    private AlertItem? _selected;

    [ObservableProperty]
    private bool _showResolved;

    [ObservableProperty]
    private bool _isEmpty;

    partial void OnShowResolvedChanged(bool value) => _ = RefreshAsync();

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var selectedId = Selected?.Id;
        var list = await client.Api.ListAlertsAsync(new ListAlertsRequest { IncludeResolved = ShowResolved, Limit = 200 });
        Items.Clear();
        foreach (var alert in list.Alerts)
        {
            Items.Add(new AlertItem(alert));
        }

        IsEmpty = Items.Count == 0;
        Selected = Items.FirstOrDefault(a => a.Id == selectedId) ?? (Items.Count > 0 ? Items[0] : null);
    });

    public async Task<AlertItem?> SelectAsync(string alertId)
    {
        await RefreshAsync();
        var alert = Items.FirstOrDefault(a => a.Id == alertId);
        if (alert is null)
        {
            try
            {
                alert = new AlertItem(await client.Api.GetAlertAsync(new AlertRequest { Id = alertId }));
                Items.Insert(0, alert);
            }
            catch (Exception ex)
            {
                Error = AgentClient.Describe(ex);
            }
        }

        Selected = alert;
        return alert;
    }

    [RelayCommand]
    private Task FixAsync(ActionItem action) => main.OpenCorrectionAsync(Selected, action);
}
