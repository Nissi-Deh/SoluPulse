using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>
/// La fiche de correction (EF-COR-01, EF-COR-02) : actions prévues, risque, durée, redémarrage,
/// réversibilité, puis validation explicite. Le service ne délivre le jeton d'exécution qu'à
/// l'affichage de la fiche ; sans clic sur « Valider », rien n'est exécuté.
/// </summary>
public sealed partial class CorrectionViewModel : ObservableObject
{
    private readonly AgentClient _client;
    private readonly string? _alertId;
    private string? _token;

    public CorrectionViewModel(AgentClient client, ActionInfo action, string? alertId)
    {
        _client = client;
        _alertId = alertId;
        Action = action;
        Steps = action.Steps.ToList();
        RiskText = action.Guide ? "Aucun : rien n'est modifié sur votre PC" : ActionItem.Risk(action.Risk);
        DurationText = Format.Duration(action.EstimatedSeconds);
        RebootText = action.RequiresReboot ? "Oui, pour terminer la correction" : "Non";
        ReversibleText = action.Guide ? "Sans objet"
            : action.Reversible ? "Oui : un bouton « Annuler » restera disponible dans le journal"
            : "Non : cette opération ne peut pas être annulée";
        RestorePointText = !action.RestorePoint ? ""
            : action.RestorePointMandatory ? "Un point de restauration Windows sera créé avant ; sans lui, rien ne sera modifié."
            : "Un point de restauration Windows sera créé avant, si la protection du système est active.";
        ConfirmLabel = action.Guide ? "J'ai compris" : "Valider la correction";
        Parameters = new ObservableCollection<ParameterItem>(action.ParameterNames.Select(n => new ParameterItem(n, action.MultiChoiceParameters.Contains(n))));
        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParameterItem.Value))
                {
                    _ = PrepareSoonAsync();
                }
            };
        }
    }

    private int _pendingPrepare;
    private int _prepareVersion;

    /// <summary>Les cases cochées à la suite sont regroupées : une seule estimation après la dernière.</summary>
    private async Task PrepareSoonAsync()
    {
        var version = ++_pendingPrepare;
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        if (version == _pendingPrepare)
        {
            await PrepareAsync();
        }
    }

    public ActionInfo Action { get; }

    public string Title => Action.Title;

    public string Description => Action.Description;

    public IReadOnlyList<string> Steps { get; }

    public string RiskText { get; }

    public string DurationText { get; }

    public string RebootText { get; }

    public string ReversibleText { get; }

    public string RestorePointText { get; }

    public bool HasRestorePoint => RestorePointText.Length > 0;

    public string ConfirmLabel { get; }

    public ObservableCollection<ParameterItem> Parameters { get; }

    public bool HasParameters => Parameters.Count > 0;

    public bool Completed { get; private set; }

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _ready;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _running;

    [ObservableProperty]
    private bool _finished;

    [ObservableProperty]
    private string _estimate = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _resultMessage = "";

    [ObservableProperty]
    private bool _success;

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var parameter in Parameters)
            {
                var choices = await _client.Api.GetActionChoicesAsync(new ChoicesRequest { ActionId = Action.Id, Parameter = parameter.Name });
                parameter.Choices = choices.Choices.ToList();
                if (parameter.Choices.Count == 0)
                {
                    Error = $"Aucun choix disponible pour « {parameter.Label} » : le problème a peut-être déjà disparu.";
                }
            }
        }
        catch (Exception ex)
        {
            Error = AgentClient.Describe(ex);
            return;
        }

        if (!HasParameters)
        {
            await PrepareAsync();
        }
    }

    private async Task PrepareAsync()
    {
        var version = ++_prepareVersion;
        Ready = false;
        await ReleaseTokenAsync();
        if (Parameters.Any(p => p.Value is null))
        {
            Estimate = "";
            return;
        }

        Estimate = "Estimation en cours…";
        try
        {
            var request = new PrepareRequest { ActionId = Action.Id, AlertId = _alertId ?? "" };
            foreach (var parameter in Parameters)
            {
                request.Parameters[parameter.Name] = parameter.Value!;
            }

            var card = await _client.Api.PrepareCorrectionAsync(request);
            if (version != _prepareVersion)
            {
                // Le choix a changé pendant l'estimation : cette fiche est périmée.
                await CancelTokenAsync(card.Token);
                return;
            }

            _token = card.Token;
            Estimate = card.Estimate;
            Error = null;
            Ready = true;
        }
        catch (Exception ex)
        {
            if (version == _prepareVersion)
            {
                Estimate = "";
                Error = AgentClient.Describe(ex);
            }
        }
    }

    private bool CanConfirm() => Ready && !Running && _token is not null;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        Running = true;
        Error = null;
        try
        {
            var token = _token!;
            _token = null;
            var reply = await _client.Api.ConfirmCorrectionAsync(new TokenRequest { Token = token }, deadline: DateTime.UtcNow.AddHours(3));
            Completed = true;
            Success = reply.State == "Reussie";
            ResultMessage = reply.Message
                + (reply.Verification switch
                {
                    "Resolue" => "\nVérification : le problème est résolu.",
                    "Persistante" => "\nVérification : le problème est toujours présent.",
                    _ => "",
                })
                + (reply.RequiresReboot ? "\nRedémarrez le PC pour terminer." : "");
            Finished = true;
        }
        catch (Exception ex)
        {
            Error = AgentClient.Describe(ex);
        }
        finally
        {
            Running = false;
        }
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (Running)
        {
            return;
        }

        await ReleaseTokenAsync();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fiche fermée sans validation : le jeton est détruit côté service et le refus journalisé.</summary>
    public async Task ReleaseTokenAsync()
    {
        if (_token is { } token)
        {
            _token = null;
            await CancelTokenAsync(token);
        }
    }

    private async Task CancelTokenAsync(string token)
    {
        try
        {
            await _client.Api.CancelCorrectionAsync(new TokenRequest { Token = token });
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException)
        {
            // Le jeton expirera de lui-même.
        }
    }
}
