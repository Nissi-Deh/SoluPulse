using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

public sealed class NetworkProbeOptions
{
    /// <summary>
    /// Les sondes Internet envoient quelques pings et une requête DNS, sans aucune donnée du PC.
    /// Elles peuvent être désactivées dans les paramètres.
    /// </summary>
    public bool InternetProbes { get; set; } = true;

    public IReadOnlyList<IPAddress> InternetTargets { get; set; } = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    public string DnsProbeHost { get; set; } = "www.msftconnecttest.com";
}

/// <summary>
/// Latence, perte de paquets, résolution DNS, signal Wi-Fi et joignabilité de la passerelle (EF-COL-08).
/// Quelques paquets par mesure seulement, pour ne pas charger la connexion.
/// </summary>
public sealed partial class NetworkCollector(TimeProvider timeProvider, NetworkProbeOptions options) : ICollector
{
    private const int PingCount = 4;
    private const int PingTimeoutMs = 1000;

    private (long Received, long Sent, long Timestamp)? _previousBytes;

    public string Name => "reseau";

    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    public static IEnumerable<NetworkInterface> ActiveInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));

    public async Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var active = ActiveInterfaces().ToList();
        result.Add("reseau.connecte", active.Count > 0 ? 1 : 0, "");
        result.Snapshot("cartes_reseau", active.Select(n => new { Nom = n.Name, Type = n.NetworkInterfaceType.ToString(), VitesseMbps = n.Speed / 1_000_000 }));
        result.Try("debit", () => AddThroughput(result, active));

        var gateway = active.SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (gateway is null)
        {
            return result.Build();
        }

        await result.TryAsync("passerelle", async () =>
        {
            var (loss, latency) = await PingAsync([gateway], PingCount, cancellationToken).ConfigureAwait(false);
            result.Add("reseau.passerelle.perte_pct", loss, "%");
            if (latency is { } ms)
            {
                result.Add("reseau.passerelle.latence_ms", ms, "ms");
            }
        }).ConfigureAwait(false);

        if (options.InternetProbes)
        {
            await result.TryAsync("internet", async () =>
            {
                var (loss, latency) = await PingAsync(options.InternetTargets, PingCount / 2, cancellationToken).ConfigureAwait(false);
                result.Add("reseau.internet.perte_pct", loss, "%");
                if (latency is { } ms)
                {
                    result.Add("reseau.internet.latence_ms", ms, "ms");
                }
            }).ConfigureAwait(false);

            await result.TryAsync("dns", async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(options.DnsProbeHost, timeout.Token).ConfigureAwait(false);
                    result.Add("reseau.dns.echec", addresses.Length == 0 ? 1 : 0, "");
                    result.Add("reseau.dns.resolution_ms", stopwatch.ElapsedMilliseconds, "ms");
                }
                catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    result.Add("reseau.dns.echec", 1, "");
                }
            }).ConfigureAwait(false);
        }

        if (active.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            await result.TryAsync("wifi", async () =>
            {
                var output = await SystemTools.RunAsync("netsh.exe", ["wlan", "show", "interfaces"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                var match = SignalPattern().Match(output.Output);
                if (match.Success)
                {
                    result.Add("reseau.wifi.signal_pct", int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), "%");
                }
            }).ConfigureAwait(false);
        }

        return result.Build();
    }

    /// <summary>Débit moyen reçu et envoyé depuis le relevé précédent, toutes cartes actives confondues.</summary>
    private void AddThroughput(CollectorResultBuilder result, IReadOnlyList<NetworkInterface> active)
    {
        long received = 0, sent = 0;
        foreach (var statistics in active.Select(n => n.GetIPStatistics()))
        {
            received += statistics.BytesReceived;
            sent += statistics.BytesSent;
        }

        var now = Stopwatch.GetTimestamp();
        if (_previousBytes is { } previous && received >= previous.Received && sent >= previous.Sent)
        {
            var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
            if (seconds > 0)
            {
                result.Add("reseau.debit_recu_kbps", Math.Round((received - previous.Received) * 8 / 1000d / seconds, 1), "kbit/s");
                result.Add("reseau.debit_envoye_kbps", Math.Round((sent - previous.Sent) * 8 / 1000d / seconds, 1), "kbit/s");
            }
        }

        _previousBytes = (received, sent, now);
    }

    private static async Task<(double LossPercent, double? MedianLatency)> PingAsync(IReadOnlyList<IPAddress> targets, int countPerTarget, CancellationToken cancellationToken)
    {
        var latencies = new List<long>();
        var sent = 0;
        using var ping = new Ping();
        foreach (var target in targets)
        {
            for (var i = 0; i < countPerTarget; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sent++;
                try
                {
                    var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(PingTimeoutMs), cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        latencies.Add(reply.RoundtripTime);
                    }
                }
                catch (PingException)
                {
                    // Compté comme perdu.
                }
            }
        }

        var loss = sent == 0 ? 0 : Math.Round(100d * (sent - latencies.Count) / sent, 0);
        double? median = latencies.Count == 0 ? null : latencies.Order().ElementAt(latencies.Count / 2);
        return (loss, median);
    }

    // « Signal : 85% » en français comme en anglais.
    [GeneratedRegex(@"Signal\s*:\s*(\d+)\s*%")]
    private static partial Regex SignalPattern();
}
