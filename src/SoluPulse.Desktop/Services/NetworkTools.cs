using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoluPulse.Desktop.Services;

public sealed record SpeedResult(double DownloadMbps, double UploadMbps, double LatencyMs, long BytesUsed);

/// <summary>
/// Test de débit lancé par l'utilisateur, avec ses propres réglages réseau (proxy compris). Le volume
/// s'adapte à la connexion pour durer quelques secondes : environ 30 Mo sur une bonne connexion,
/// bien moins sur une connexion mobile lente.
/// </summary>
public static class SpeedTest
{
    private const string Server = "https://speed.cloudflare.com";

    public static async Task<SpeedResult> RunAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        long used = 0;

        progress.Report("Mesure du temps de réponse…");
        var pings = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync($"{Server}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            pings.Add(watch.Elapsed.TotalMilliseconds);
        }

        progress.Report("Mesure du débit descendant…");
        var probe = await DownloadAsync(http, 1_000_000, cancellationToken).ConfigureAwait(false);
        used += 1_000_000;
        var size = Size(probe, min: 1_000_000, max: 25_000_000);
        var download = await DownloadAsync(http, size, cancellationToken).ConfigureAwait(false);
        used += size;

        progress.Report("Mesure du débit montant…");
        var uploadSize = Size(Math.Min(download, 50), min: 250_000, max: 8_000_000);
        var upload = await UploadAsync(http, uploadSize, cancellationToken).ConfigureAwait(false);
        used += uploadSize;

        pings.Sort();
        return new SpeedResult(Math.Round(download, 1), Math.Round(upload, 1), Math.Round(pings[pings.Count / 2]), used);
    }

    /// <summary>Volume qui prend environ 5 secondes au débit mesuré, borné.</summary>
    internal static long Size(double mbps, long min, long max) => Math.Clamp((long)(mbps * 1_000_000 / 8 * 5), min, max);

    private static async Task<double> DownloadAsync(HttpClient http, long bytes, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{Server}/__down?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        var watch = Stopwatch.StartNew();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return Mbps(total, watch.Elapsed);
    }

    private static async Task<double> UploadAsync(HttpClient http, long bytes, CancellationToken cancellationToken)
    {
        var payload = new byte[bytes];
        RandomNumberGenerator.Fill(payload);
        using var content = new ByteArrayContent(payload);
        var watch = Stopwatch.StartNew();
        using var response = await http.PostAsync($"{Server}/__up", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Mbps(bytes, watch.Elapsed);
    }

    internal static double Mbps(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000;
}

public sealed record WifiNetwork(string Ssid, int Channel, string Band, int SignalPercent, bool Connected);

public sealed record WifiReport(IReadOnlyList<WifiNetwork> Networks, WifiNetwork? Current, int? RecommendedChannel, string Advice);

/// <summary>
/// Réseaux Wi-Fi alentour (API WLAN de Windows) : canal, bande et force du signal. Depuis Windows 11
/// 24H2, la liste demande l'accès à la position pour les applications de bureau.
/// </summary>
public static class WifiScanner
{
    private const int BssEntrySize = 360;
    private static readonly int[] NonOverlapping = [1, 6, 11];

    public static async Task<WifiReport> ScanAsync(CancellationToken cancellationToken)
    {
        if (WlanOpenHandle(2, 0, out _, out var handle) != 0)
        {
            throw new InvalidOperationException("Le service Wi-Fi de Windows ne répond pas : ce PC n'a peut-être pas de carte Wi-Fi.");
        }

        try
        {
            var interfaces = Interfaces(handle);
            if (interfaces.Count == 0)
            {
                throw new InvalidOperationException("Aucune carte Wi-Fi sur ce PC.");
            }

            foreach (var id in interfaces)
            {
                var guid = id;
                _ = WlanScan(handle, ref guid, 0, 0, 0);
            }

            // Windows met quelques secondes à rafraîchir la liste après une recherche.
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);

            var networks = new List<WifiNetwork>();
            byte[]? currentBssid = null;
            foreach (var id in interfaces)
            {
                var guid = id;
                currentBssid ??= CurrentBssid(handle, guid);
                var status = WlanGetNetworkBssList(handle, ref guid, 0, 3, false, 0, out var list);
                if (status == 5)
                {
                    throw new UnauthorizedAccessException("Windows demande l'accès à la position pour lister les réseaux Wi-Fi : Paramètres > Confidentialité et sécurité > Localisation, puis « Autoriser les applications de bureau à accéder à votre position ».");
                }

                if (status != 0 || list == 0)
                {
                    continue;
                }

                try
                {
                    var count = Marshal.ReadInt32(list, 4);
                    for (var i = 0; i < count; i++)
                    {
                        var entry = list + 8 + (i * BssEntrySize);
                        var ssidLength = Math.Min(32, Marshal.ReadInt32(entry));
                        var ssid = new byte[ssidLength];
                        Marshal.Copy(entry + 4, ssid, 0, ssidLength);
                        var bssid = new byte[6];
                        Marshal.Copy(entry + 40, bssid, 0, 6);
                        var quality = Marshal.ReadInt32(entry + 60);
                        var frequency = Marshal.ReadInt32(entry + 92);
                        var (channel, band) = Channel(frequency);
                        networks.Add(new WifiNetwork(
                            ssidLength == 0 ? "(réseau masqué)" : Encoding.UTF8.GetString(ssid),
                            channel,
                            band,
                            Math.Clamp(quality, 0, 100),
                            currentBssid is not null && bssid.AsSpan().SequenceEqual(currentBssid)));
                    }
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            }

            var current = networks.FirstOrDefault(n => n.Connected);
            var recommended = Recommend(networks);
            return new WifiReport(networks.OrderByDescending(n => n.SignalPercent).ToList(), current, recommended, Advice(networks, current, recommended));
        }
        finally
        {
            _ = WlanCloseHandle(handle, 0);
        }
    }

    /// <summary>Fréquence centrale en kHz vers numéro de canal et bande.</summary>
    internal static (int Channel, string Band) Channel(int kilohertz) => kilohertz switch
    {
        2_484_000 => (14, "2,4 GHz"),
        >= 2_412_000 and < 2_484_000 => ((kilohertz - 2_407_000) / 5_000, "2,4 GHz"),
        >= 5_150_000 and < 5_925_000 => ((kilohertz - 5_000_000) / 5_000, "5 GHz"),
        >= 5_925_000 and < 7_125_000 => ((kilohertz - 5_950_000) / 5_000, "6 GHz"),
        _ => (0, "?"),
    };

    /// <summary>
    /// En 2,4 GHz, seuls les canaux 1, 6 et 11 ne se chevauchent pas. Un réseau gêne les canaux à 4 de
    /// distance ou moins, d'autant plus que son signal est fort. Le canal le moins encombré est proposé.
    /// </summary>
    internal static int? Recommend(IReadOnlyList<WifiNetwork> networks)
    {
        var crowded = networks.Where(n => n.Band == "2,4 GHz" && !n.Connected).ToList();
        if (crowded.Count == 0)
        {
            return null;
        }

        return NonOverlapping
            .OrderBy(c => crowded.Where(n => Math.Abs(n.Channel - c) <= 4).Sum(n => n.SignalPercent * (5 - Math.Abs(n.Channel - c))))
            .First();
    }

    private static string Advice(IReadOnlyList<WifiNetwork> networks, WifiNetwork? current, int? recommended)
    {
        if (current is null)
        {
            return "Ce PC n'est pas connecté en Wi-Fi.";
        }

        var neighbours = networks.Count(n => !n.Connected && n.Band == current.Band && Math.Abs(n.Channel - current.Channel) <= (current.Band == "2,4 GHz" ? 4 : 0));
        var text = new StringBuilder();
        text.Append(CultureInfo.CurrentCulture, $"Connecté à « {current.Ssid} » en {current.Band}, canal {current.Channel}, signal {current.SignalPercent} %. ");
        text.Append(neighbours switch
        {
            0 => "Aucun réseau voisin ne gêne ce canal.",
            1 => "1 réseau voisin utilise le même canal ou un canal proche.",
            _ => $"{neighbours} réseaux voisins utilisent le même canal ou un canal proche.",
        });

        if (current.Band == "2,4 GHz")
        {
            if (recommended is { } best && best != current.Channel && neighbours >= 2)
            {
                text.Append(CultureInfo.CurrentCulture, $" Dans les réglages de la box, passez le Wi-Fi 2,4 GHz sur le canal {best}, le moins encombré ici.");
            }

            text.Append(" Si la box et le PC le permettent, la bande 5 GHz est bien moins encombrée.");
        }

        if (current.SignalPercent < 40)
        {
            text.Append(" Le signal est faible : rapprochez-vous de la box ou ajoutez un répéteur.");
        }

        return text.ToString();
    }

    private static List<Guid> Interfaces(nint handle)
    {
        var ids = new List<Guid>();
        if (WlanEnumInterfaces(handle, 0, out var list) != 0 || list == 0)
        {
            return ids;
        }

        try
        {
            var count = Marshal.ReadInt32(list);

            // WLAN_INTERFACE_INFO : identifiant (16 octets), description (512), état (4).
            for (var i = 0; i < count; i++)
            {
                var bytes = new byte[16];
                Marshal.Copy(list + 8 + (i * 532), bytes, 0, 16);
                ids.Add(new Guid(bytes));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return ids;
    }

    /// <summary>BSSID du réseau auquel la carte est connectée (WLAN_CONNECTION_ATTRIBUTES, octet 560).</summary>
    private static byte[]? CurrentBssid(nint handle, Guid id)
    {
        if (WlanQueryInterface(handle, ref id, 7, 0, out var size, out var data, out _) != 0 || data == 0)
        {
            return null;
        }

        try
        {
            if (size < 566 || Marshal.ReadInt32(data) != 1)
            {
                return null;
            }

            var bssid = new byte[6];
            Marshal.Copy(data + 560, bssid, 0, 6);
            return bssid;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(nint handle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(nint handle, nint reserved, out nint list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanScan(nint handle, ref Guid interfaceGuid, nint ssid, nint ieData, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetNetworkBssList(nint handle, ref Guid interfaceGuid, nint ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, nint reserved, out nint list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(nint handle, ref Guid interfaceGuid, int opCode, nint reserved, out int dataSize, out nint data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);
}

public sealed record LanDevice(string Address, string Name, string Kind, string MacHash, bool IsThisPc, bool IsGateway, bool Known, string? Label);

/// <summary>
/// Appareils du réseau local : un ping de chaque adresse du réseau (au plus 254), puis la table ARP de
/// Windows. Les adresses matérielles ne sont jamais enregistrées : seule une empreinte salée, gardée
/// dans le profil de l'utilisateur, permet de reconnaître un appareil déjà vu.
/// </summary>
public static class LanScanner
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F0:9F:C2"] = "Ubiquiti", ["00:1A:11"] = "Google", ["3C:5A:B4"] = "Google", ["F4:F5:D8"] = "Google", ["DC:A6:32"] = "Raspberry Pi",
        ["B8:27:EB"] = "Raspberry Pi", ["E4:5F:01"] = "Raspberry Pi", ["00:50:56"] = "VMware", ["08:00:27"] = "VirtualBox",
        ["50:C7:BF"] = "TP-Link", ["98:DA:C4"] = "TP-Link", ["C0:06:C3"] = "TP-Link", ["14:EB:B6"] = "TP-Link", ["C8:3A:35"] = "Tenda",
        ["00:E0:4C"] = "Realtek", ["00:1E:C9"] = "Dell", ["F8:B1:56"] = "Dell", ["3C:52:82"] = "HP", ["9C:B6:54"] = "HP",
        ["00:25:B3"] = "HP", ["C8:5B:76"] = "Lenovo", ["54:EE:75"] = "Wistron (PC portable)", ["AC:BC:32"] = "Apple", ["F0:18:98"] = "Apple",
        ["3C:22:FB"] = "Apple", ["A4:83:E7"] = "Apple", ["8C:85:90"] = "Apple", ["5C:F7:E6"] = "Apple", ["F8:E9:4E"] = "Apple",
        ["64:1C:AE"] = "Samsung", ["8C:77:12"] = "Samsung", ["D0:22:BE"] = "Samsung", ["F4:7B:09"] = "Samsung", ["A0:CB:FD"] = "Samsung",
        ["00:9E:C8"] = "Xiaomi", ["64:09:80"] = "Xiaomi", ["F8:A4:5F"] = "Xiaomi", ["28:6C:07"] = "Xiaomi", ["48:2C:A0"] = "Xiaomi",
        ["00:E0:FC"] = "Huawei", ["48:DB:50"] = "Huawei", ["E8:CD:2D"] = "Huawei", ["AC:E2:15"] = "Huawei", ["00:46:4B"] = "Huawei",
        ["BC:76:70"] = "Huawei", ["F4:63:1F"] = "Huawei", ["9C:28:F7"] = "Xiaomi", ["E0:B6:55"] = "Tecno / Infinix (Transsion)",
        ["38:E6:0A"] = "Tecno / Infinix (Transsion)", ["74:DA:88"] = "TP-Link", ["00:23:68"] = "Zebra", ["30:B5:C2"] = "TP-Link",
        ["00:11:32"] = "Synology", ["00:1B:A9"] = "Brother", ["00:26:AB"] = "Epson", ["64:EB:8C"] = "Epson", ["00:00:48"] = "Epson",
        ["30:CD:A7"] = "Samsung", ["2C:FD:A1"] = "Asus", ["04:D9:F5"] = "Asus", ["1C:87:2C"] = "Asus", ["00:90:4C"] = "Epigram (Broadcom)",
    };

    private static string KnownFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoluPulse", "appareils-connus.json");

    public static async Task<IReadOnlyList<LanDevice>> ScanAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var (local, gateway, mask) = LocalNetwork() ?? throw new InvalidOperationException("Ce PC n'est connecté à aucun réseau local.");
        var targets = Hosts(local, mask).ToList();
        progress.Report($"Recherche des appareils sur {targets.Count} adresses…");

        using var limiter = new SemaphoreSlim(64);
        await Task.WhenAll(targets.Select(async address =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var ping = new Ping();
                await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(500), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (PingException)
            {
                // Pas de réponse : l'appareil peut quand même apparaître dans la table ARP.
            }
            finally
            {
                limiter.Release();
            }
        })).ConfigureAwait(false);

        progress.Report("Identification des appareils…");
        var known = LoadKnown();
        var salt = Salt(known);
        var devices = new List<LanDevice>();
        foreach (var (address, mac) in ArpTable().Where(e => SameNetwork(e.Address, local, mask)))
        {
            var hash = Hash(mac, salt);
            var name = await NameAsync(address, cancellationToken).ConfigureAwait(false);
            var isThisPc = address.Equals(local);
            var isGateway = gateway is not null && address.Equals(gateway);
            known.Devices.TryGetValue(hash, out var label);
            devices.Add(new LanDevice(address.ToString(), name ?? "", Kind(mac, isGateway), hash, isThisPc, isGateway, isThisPc || isGateway || label is not null, label));
        }

        if (!devices.Any(d => d.IsThisPc))
        {
            devices.Add(new LanDevice(local.ToString(), Environment.MachineName, "Ce PC", "", true, false, true, null));
        }

        return devices.OrderBy(d => d.Known).ThenBy(d => IPAddress.Parse(d.Address).GetAddressBytes()[3]).ToList();
    }

    /// <summary>Retient un appareil sous le nom choisi par l'utilisateur.</summary>
    public static void Remember(string macHash, string label)
    {
        var known = LoadKnown();
        known.Devices[macHash] = label.Trim().Length == 0 ? "Appareil connu" : label.Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(KnownFile)!);
        File.WriteAllText(KnownFile, JsonSerializer.Serialize(known));
    }

    /// <summary>
    /// Fabricant d'après le début de l'adresse matérielle. Une adresse « privée » (bit local à 1) est
    /// générée au hasard par les téléphones récents : on ne peut pas en déduire le fabricant.
    /// </summary>
    internal static string Kind(byte[] mac, bool gateway)
    {
        if (gateway)
        {
            return "Box ou routeur";
        }

        if ((mac[0] & 0x02) != 0)
        {
            return "Téléphone ou tablette (adresse privée)";
        }

        var prefix = string.Join(':', mac.Take(3).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        return Vendors.TryGetValue(prefix, out var vendor) ? vendor : "Fabricant inconnu";
    }

    internal static bool SameNetwork(IPAddress address, IPAddress local, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var l = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        return a.Length == 4 && Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (l[i] & m[i]));
    }

    /// <summary>Adresses du réseau local, limitées aux 254 voisines du PC si le réseau est plus grand.</summary>
    internal static IEnumerable<IPAddress> Hosts(IPAddress local, IPAddress mask)
    {
        var l = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (m[0] != 255 || m[1] != 255 || m[2] != 255)
        {
            m = [255, 255, 255, 0];
        }

        for (var i = 1; i < 255; i++)
        {
            var candidate = new byte[] { l[0], l[1], l[2], (byte)i };
            if (SameNetwork(new IPAddress(candidate), local, new IPAddress(m)) && !candidate.SequenceEqual(l))
            {
                yield return new IPAddress(candidate);
            }
        }
    }

    private static (IPAddress Local, IPAddress? Gateway, IPAddress Mask)? LocalNetwork()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var unicast = properties.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is not null && unicast is not null)
            {
                return (unicast.Address, gateway, unicast.IPv4Mask);
            }
        }

        return null;
    }

    private static async Task<string?> NameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            return entry.HostName == address.ToString() ? null : entry.HostName.Split('.')[0];
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Table ARP IPv4 de Windows (MIB_IPNETROW : 24 octets par ligne), sans diffusion ni multidiffusion.</summary>
    private static List<(IPAddress Address, byte[] Mac)> ArpTable()
    {
        var size = 0;
        _ = GetIpNetTable(0, ref size, true);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, true) != 0)
            {
                return [];
            }

            var count = Marshal.ReadInt32(buffer);
            var rows = new List<(IPAddress, byte[])>();
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + (i * 24);
                var length = Marshal.ReadInt32(row + 4);
                var type = Marshal.ReadInt32(row + 20);
                if (length != 6 || type is not (3 or 4))
                {
                    continue;
                }

                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                var address = new IPAddress((uint)Marshal.ReadInt32(row + 16));
                var first = address.GetAddressBytes()[0];
                if ((mac[0] & 0x01) != 0 || first >= 224 || mac.All(b => b == 0xFF))
                {
                    continue;
                }

                rows.Add((address, mac));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string Hash(byte[] mac, string salt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + Convert.ToHexString(mac))))[..16];

    private static string Salt(KnownDevices known)
    {
        if (known.Salt.Length == 0)
        {
            known.Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KnownFile)!);
                File.WriteAllText(KnownFile, JsonSerializer.Serialize(known));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sans fichier, les appareils seront « nouveaux » à chaque recherche.
            }
        }

        return known.Salt;
    }

    private static KnownDevices LoadKnown()
    {
        try
        {
            return File.Exists(KnownFile) ? JsonSerializer.Deserialize<KnownDevices>(File.ReadAllText(KnownFile)) ?? new KnownDevices() : new KnownDevices();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new KnownDevices();
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order);

    private sealed class KnownDevices
    {
        public string Salt { get; set; } = "";

        public Dictionary<string, string> Devices { get; set; } = [];
    }
}
