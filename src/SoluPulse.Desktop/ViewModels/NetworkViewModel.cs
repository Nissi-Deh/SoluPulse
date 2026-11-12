using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf.WellKnownTypes;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

public sealed record SpeedHistoryItem(string Date, string Download, string Upload, string Latency);

public sealed record WifiChannelItem(string Channel, int Networks, string Names, bool Current);

public sealed partial class LanDeviceItem(LanDevice device) : ObservableObject
{
    public LanDevice Device { get; } = device;

    public string Title => Device.Label ?? (Device.Name.Length > 0 ? Device.Name : Device.Kind);

    public string Detail => $"{Device.Address} · {Device.Kind}{(Device.IsThisPc ? " · ce PC" : "")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    private bool _known = device.Known;

    [ObservableProperty]
    private string _label = device.Label ?? device.Name;

    public bool IsNew => !Known;
}

/// <summary>
/// Page « Réseau » : test de débit avec son historique, réseaux Wi-Fi alentour et conseil de canal,
/// appareils connectés au réseau local avec les nouveaux mis en avant.
/// </summary>
public sealed partial class NetworkViewModel(AgentClient client) : PageViewModel
{
    public override string Title => "Réseau";

    public ObservableCollection<SpeedHistoryItem> SpeedHistory { get; } = [];

    public ObservableCollection<WifiChannelItem> Channels { get; } = [];

    public ObservableCollection<LanDeviceItem> Devices { get; } = [];

    [ObservableProperty]
    private string _speedStatus = "Le test télécharge puis envoie quelques dizaines de mégaoctets : évitez-le sur une connexion mobile limitée.";

    [ObservableProperty]
    private string _speedResult = "";

    [ObservableProperty]
    private string _wifiAdvice = "";

    [ObservableProperty]
    private bool _wifiNeedsLocation;

    [ObservableProperty]
    private string _devicesStatus = "Recherchez les appareils branchés sur votre box : un appareil inconnu peut être un voisin qui utilise votre Wi-Fi.";

    public bool HasNewDevices => Devices.Any(d => d.IsNew);

    public bool HasHistory => SpeedHistory.Count > 0;

    public override Task RefreshAsync() => RunAsync(LoadHistoryAsync);

    [RelayCommand]
    private Task RunSpeedTestAsync() => RunAsync(async () =>
    {
        SpeedResult = "";
        var progress = new Progress<string>(text => SpeedStatus = text);
        var result = await SpeedTest.RunAsync(progress, CancellationToken.None);
        await client.Api.RecordSpeedTestAsync(new SpeedTestResult { DownloadMbps = result.DownloadMbps, UploadMbps = result.UploadMbps, LatencyMs = result.LatencyMs });
        SpeedResult = $"Réception : {result.DownloadMbps:0.#} Mbit/s · Envoi : {result.UploadMbps:0.#} Mbit/s · Temps de réponse : {result.LatencyMs:0} ms";
        SpeedStatus = $"{Verdict(result)} Données utilisées : {result.BytesUsed / 1_000_000.0:0.#} Mo.";
        await LoadHistoryAsync();
    });

    [RelayCommand]
    private Task ScanWifiAsync() => RunAsync(async () =>
    {
        WifiNeedsLocation = false;
        WifiAdvice = "Recherche des réseaux Wi-Fi alentour…";
        try
        {
            var report = await WifiScanner.ScanAsync(CancellationToken.None);
            WifiAdvice = report.Advice;
            Channels.Clear();
            foreach (var group in report.Networks.GroupBy(n => (n.Band, n.Channel)).OrderBy(g => g.Key.Band).ThenBy(g => g.Key.Channel))
            {
                Channels.Add(new WifiChannelItem(
                    $"{group.Key.Band} · canal {group.Key.Channel}",
                    group.Count(),
                    string.Join(", ", group.Select(n => n.Ssid).Distinct().Take(4)),
                    group.Any(n => n.Connected)));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            WifiAdvice = ex.Message;
            WifiNeedsLocation = true;
        }
        catch (InvalidOperationException ex)
        {
            WifiAdvice = ex.Message;
        }
    });

    [RelayCommand]
    private void OpenLocationSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true })?.Dispose();
        WifiAdvice = "Après avoir autorisé l'accès à la position, relancez l'analyse.";
    }

    [RelayCommand]
    private Task ScanDevicesAsync() => RunAsync(async () =>
    {
        var progress = new Progress<string>(text => DevicesStatus = text);
        var devices = await LanScanner.ScanAsync(progress, CancellationToken.None);
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(new LanDeviceItem(device));
        }

        var unknown = devices.Count(d => !d.Known);
        DevicesStatus = unknown == 0
            ? $"{devices.Count} appareils trouvés, tous connus."
            : $"{devices.Count} appareils trouvés, dont {unknown} jamais vus. Un téléphone qui change d'adresse privée peut apparaître plusieurs fois.";
        OnPropertyChanged(nameof(HasNewDevices));
    });

    [RelayCommand]
    private void Remember(LanDeviceItem item)
    {
        if (item.Device.MacHash.Length == 0)
        {
            return;
        }

        LanScanner.Remember(item.Device.MacHash, item.Label);
        item.Known = true;
        OnPropertyChanged(nameof(HasNewDevices));
    }

    internal static string Verdict(SpeedResult result) => result.DownloadMbps switch
    {
        < 1 => "Connexion très lente : la navigation sera pénible.",
        < 5 => "Connexion lente : suffisante pour les e-mails et WhatsApp, pas pour la vidéo.",
        < 20 => "Connexion correcte : vidéo en qualité standard possible.",
        _ => "Bonne connexion.",
    } + (result.LatencyMs > 150 ? " Le temps de réponse est élevé : les appels vidéo peuvent couper." : "");

    private async Task LoadHistoryAsync()
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-90);
        HistoryReply Query(string metric) => client.Api.GetHistory(new HistoryRequest { Metric = metric, From = Timestamp.FromDateTimeOffset(from), To = Timestamp.FromDateTimeOffset(to), MaxPoints = 2000 });
        var (down, up, latency) = await Task.Run(() => (Query("reseau.test_debit.descendant_mbps"), Query("reseau.test_debit.montant_mbps"), Query("reseau.test_debit.latence_ms")));
        SpeedHistory.Clear();
        foreach (var point in down.Points.Reverse().Take(10))
        {
            var at = point.At.ToDateTimeOffset();
            var upload = up.Points.FirstOrDefault(p => p.At == point.At)?.Value;
            var ping = latency.Points.FirstOrDefault(p => p.At == point.At)?.Value;
            SpeedHistory.Add(new SpeedHistoryItem(
                at.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Format.French),
                $"{point.Value:0.#} Mbit/s",
                upload is { } u ? $"{u:0.#} Mbit/s" : "—",
                ping is { } l ? $"{l:0} ms" : "—"));
        }

        OnPropertyChanged(nameof(HasHistory));
    }
}
