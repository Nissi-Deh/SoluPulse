using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>Une capture, c'est-à-dire un point de restauration de Windows.</summary>
public sealed class SnapshotItem(SnapshotInfo info)
{
    public SnapshotInfo Info { get; } = info;

    public string Title { get; } = info.Origin switch
    {
        "manuelle" => "Capture manuelle",
        "hebdomadaire" => "Capture hebdomadaire",
        "correction" => $"Avant la correction « {info.Description.Replace("SoluPulse - ", "", StringComparison.Ordinal)} »",
        _ => info.Description.Length > 0 ? info.Description : "Point de restauration de Windows",
    };

    public string Date { get; } = info.CreatedAt.ToDateTimeOffset().ToLocalTime().ToString("dddd d MMMM yyyy à HH:mm", Format.French);

    public string Origin { get; } = info.Origin switch
    {
        "manuelle" => "Créée à votre demande",
        "hebdomadaire" => "Créée automatiquement par SoluPulse",
        "correction" => "Créée par SoluPulse avant une correction",
        _ => "Créée par Windows (mise à jour, installation d'un pilote ou d'un logiciel…)",
    };

    /// <summary>Ce que l'outil de restauration de Windows affiche pour ce point, pour le retrouver.</summary>
    public string WindowsLabel { get; } = $"{info.CreatedAt.ToDateTimeOffset().ToLocalTime().ToString("dd/MM/yyyy HH:mm", Format.French)} — {info.Description}";
}

/// <summary>
/// Captures du système : points de restauration Windows créés à la demande ou chaque semaine. Revenir à
/// une capture ouvre l'outil de restauration de Windows, qui demande confirmation et redémarre le PC.
/// </summary>
public sealed partial class SnapshotsViewModel(AgentClient client, MainViewModel main) : PageViewModel
{
    private const string EnableProtectionAction = "ACT-SYS-RESTORE-ON";
    private bool _applying;

    public override string Title => "Captures du système";

    public ObservableCollection<SnapshotItem> Items { get; } = [];

    [ObservableProperty]
    private bool _protectionActive;

    [ObservableProperty]
    private bool _protectionOff;

    [ObservableProperty]
    private bool _protectionBlocked;

    [ObservableProperty]
    private bool _weeklyEnabled = true;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _restoreHelp = "";

    public override Task RefreshAsync() => RunAsync(async () => Apply(await client.Api.ListSnapshotsAsync(AgentClient.Empty)));

    [RelayCommand]
    private Task CaptureAsync() => RunAsync(async () =>
    {
        Message = "Création de la capture… Windows peut prendre une à deux minutes.";
        var reply = await client.Api.CreateSnapshotAsync(AgentClient.Empty, deadline: DateTime.UtcNow.AddMinutes(15));
        Message = reply.Message;
        Apply(reply.List);
    });

    [RelayCommand]
    private Task EnableProtectionAsync() => RunAsync(async () =>
    {
        var actions = await client.Api.ListActionsAsync(AgentClient.Empty);
        if (actions.Actions.FirstOrDefault(a => a.Id == EnableProtectionAction) is not { } action)
        {
            Message = "Cette version du service ne sait pas activer la protection du système.";
            return;
        }

        await main.OpenCorrectionAsync(null, new ActionItem(action));
        Apply(await client.Api.ListSnapshotsAsync(AgentClient.Empty));
    });

    /// <summary>
    /// Ouvre l'outil de restauration de Windows. Il ne permet pas de présélectionner un point : la page
    /// indique la ligne à choisir.
    /// </summary>
    /// <summary>L'assistant « Créer un lecteur de récupération » de Windows ; il demande l'accord d'un administrateur.</summary>
    [RelayCommand]
    private void CreateRecoveryDrive()
    {
        Error = null;
        try
        {
            Process.Start(new ProcessStartInfo("RecoveryDrive.exe") { UseShellExecute = true })?.Dispose();
            Message = "Suivez l'assistant de Windows. La clé USB sera entièrement effacée.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Message = "Création annulée : Windows demande l'accord d'un administrateur.";
        }
        catch (Win32Exception ex)
        {
            Error = $"Impossible d'ouvrir l'assistant de récupération de Windows : {ex.Message}";
        }
    }

    [RelayCommand]
    private void Restore(SnapshotItem item)
    {
        Error = null;
        RestoreHelp = $"Dans la fenêtre de Windows : cliquez sur « Suivant » (ou « Choisir un autre point de restauration »), sélectionnez la ligne « {item.WindowsLabel} », puis « Suivant » et « Terminer ». Windows redémarre le PC pour revenir à cet état. Vos documents ne sont pas touchés ; les logiciels et pilotes installés après cette date seront retirés.";
        try
        {
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RestoreHelp = "";
            Message = "Restauration annulée : Windows demande l'accord d'un administrateur pour l'ouvrir.";
        }
        catch (Win32Exception ex)
        {
            RestoreHelp = "";
            Error = $"Impossible d'ouvrir la restauration de Windows : {ex.Message}";
        }
    }

    partial void OnWeeklyEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _ = RunAsync(async () =>
            {
                await client.Api.UpdateSettingsAsync(new SettingsUpdate { WeeklySnapshots = value });
                Message = value ? "Une capture sera créée chaque semaine." : "Captures hebdomadaires désactivées.";
            });
        }
    }

    private void Apply(SnapshotList list)
    {
        _applying = true;
        try
        {
            ProtectionActive = list.Protection == "active";
            ProtectionBlocked = list.Protection == "bloquee";
            ProtectionOff = !ProtectionActive && !ProtectionBlocked;
            WeeklyEnabled = list.WeeklyEnabled;
            Items.Clear();
            foreach (var snapshot in list.Snapshots)
            {
                Items.Add(new SnapshotItem(snapshot));
            }

            IsEmpty = Items.Count == 0;
        }
        finally
        {
            _applying = false;
        }
    }
}
