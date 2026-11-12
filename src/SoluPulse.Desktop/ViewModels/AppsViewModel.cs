using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

public sealed partial class AppItem : ObservableObject
{
    public required string Name { get; init; }

    public required string Detail { get; init; }

    public required bool Unused { get; init; }

    public string? UninstallCommand { get; init; }

    public bool CanUninstall => UninstallCommand is { Length: > 0 };

    /// <summary>Premier clic : demande de confirmation ; second clic : lancement du désinstalleur.</summary>
    [ObservableProperty]
    private bool _confirming;
}

public sealed partial class UpgradeItem(WingetUpgrade upgrade) : ObservableObject
{
    public WingetUpgrade Upgrade { get; } = upgrade;

    public string Title => Upgrade.Name;

    public string Detail => $"{Upgrade.Version} → {Upgrade.Available}";

    [ObservableProperty]
    private string _status = "";
}

/// <summary>
/// Page « Applications » : celles qui ne servent plus (dernier lancement connu de Windows), désinstallées
/// par leur propre désinstalleur, et les mises à jour proposées par winget.
/// </summary>
public sealed partial class AppsViewModel(AgentClient client) : PageViewModel
{
    public override string Title => "Applications";

    public ObservableCollection<AppItem> Unused { get; } = [];

    public ObservableCollection<AppItem> Others { get; } = [];

    public ObservableCollection<UpgradeItem> Upgrades { get; } = [];

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _upgradesStatus = "Cherchez les mises à jour de vos logiciels. Les logiciels à jour reçoivent les correctifs de sécurité.";

    [ObservableProperty]
    private string _message = "";

    public override Task RefreshAsync() => RunAsync(LoadAsync);

    [RelayCommand]
    private Task ReloadAsync() => RunAsync(async () =>
    {
        Message = "Relecture des applications installées…";
        await client.Api.RefreshApplicationsAsync(AgentClient.Empty, deadline: DateTime.UtcNow.AddMinutes(2));
        await LoadAsync();
        Message = "";
    });

    [RelayCommand]
    private void Uninstall(AppItem item)
    {
        if (!item.Confirming)
        {
            item.Confirming = true;
            return;
        }

        item.Confirming = false;
        try
        {
            Uninstaller.Start(item.UninstallCommand ?? "");
            Message = $"Le désinstalleur de « {item.Name} » s'ouvre. Suivez ses étapes, puis cliquez sur « Actualiser ».";
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private Task SearchUpgradesAsync() => RunAsync(async () =>
    {
        UpgradesStatus = "Recherche des mises à jour (jusqu'à une minute)…";
        var upgrades = await Winget.ListAsync(CancellationToken.None);
        Upgrades.Clear();
        foreach (var upgrade in upgrades)
        {
            Upgrades.Add(new UpgradeItem(upgrade));
        }

        UpgradesStatus = upgrades.Count == 0 ? "Tous vos logiciels connus de winget sont à jour." : $"{upgrades.Count} logiciel(s) peuvent être mis à jour.";
    });

    [RelayCommand]
    private Task UpgradeAsync(UpgradeItem item) => RunAsync(async () =>
    {
        item.Status = "Mise à jour en cours…";
        var (success, message) = await Winget.UpgradeAsync(item.Upgrade, CancellationToken.None);
        item.Status = message;
        if (success)
        {
            Upgrades.Remove(item);
            UpgradesStatus = message;
        }
    });

    private async Task LoadAsync()
    {
        var inventory = await client.Api.GetInventoryAsync(AgentClient.Empty);
        var snapshot = inventory.Items.FirstOrDefault(i => i.Kind == "applications");
        Unused.Clear();
        Others.Clear();
        if (snapshot is null)
        {
            Summary = "La liste des applications n'est pas encore prête : le service la prépare dans les minutes qui suivent son démarrage.";
            return;
        }

        using var document = JsonDocument.Parse(snapshot.Json);
        double unusedMb = 0;
        foreach (var app in document.RootElement.EnumerateArray())
        {
            var unused = app.TryGetProperty("unused", out var u) && u.GetBoolean();
            var size = app.TryGetProperty("sizeMb", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : (double?)null;
            var item = new AppItem
            {
                Name = app.GetProperty("name").GetString() ?? "?",
                Detail = Detail(app, size),
                Unused = unused,
                UninstallCommand = app.TryGetProperty("uninstallCommand", out var c) ? c.GetString() : null,
            };
            (unused ? Unused : Others).Add(item);
            unusedMb += unused ? size ?? 0 : 0;
        }

        Summary = Unused.Count == 0
            ? "Toutes vos applications ont servi ces six derniers mois."
            : $"{Unused.Count} application(s) n'ont pas servi depuis plus de six mois{(unusedMb > 0 ? $", pour environ {Files(unusedMb)}" : "")}.";
    }

    private static string Detail(JsonElement app, double? sizeMb)
    {
        var parts = new List<string>();
        if (app.TryGetProperty("publisher", out var p) && p.GetString() is { Length: > 0 } publisher)
        {
            parts.Add(publisher);
        }

        if (sizeMb is { } mb)
        {
            parts.Add(Files(mb));
        }

        parts.Add(app.TryGetProperty("lastUsed", out var l) && l.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(l.GetString(), Format.French, System.Globalization.DateTimeStyles.None, out var last)
            ? $"dernière utilisation {Format.Ago(Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(last))}"
            : "jamais lancée depuis son installation");
        return string.Join(" · ", parts);
    }

    private static string Files(double megabytes) => megabytes >= 1024 ? $"{megabytes / 1024:0.#} Go" : $"{megabytes:0} Mo";
}
