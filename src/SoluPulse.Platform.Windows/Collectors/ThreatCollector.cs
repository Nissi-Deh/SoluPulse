using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Signes de compromission et portes ouvertes : exclusions anormales de Defender, Bureau à distance,
/// fichier hosts, proxy et DNS détournés, Wi-Fi ouverts enregistrés, programmes au démarrage et tâches
/// planifiées suspects, logiciels vulnérables, notifications et extensions des navigateurs.
/// </summary>
public sealed class ThreatCollector(TimeProvider timeProvider) : ICollector
{
    private const string DefenderScope = @"\\.\root\Microsoft\Windows\Defender";

    public string Name => "menaces";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var findings = new Dictionary<string, object>();

        result.Try("defender_exclusions", () =>
        {
            var preference = Wmi.QueryFirst(DefenderScope, "SELECT ExclusionPath, ExclusionExtension, ExclusionProcess FROM MSFT_MpPreference");
            if (preference is null)
            {
                return;
            }

            string[] Values(string property) => (preference[property] as string[] ?? [])
                .Where(v => !v.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var raw = preference["ExclusionPath"] as string[] ?? [];

            // Hors du service, Defender répond « N/A: Must be an administrator » : rien à conclure.
            if (raw.Any(v => v.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var dangerous = DefenderExclusions.Dangerous(Values("ExclusionPath"), Values("ExclusionExtension"), Values("ExclusionProcess"));
            result.Add("securite.defender.exclusions_dangereuses", dangerous.Count, "");
            findings["ExclusionsDefender"] = dangerous;
        });

        result.Try("bureau_a_distance", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            var enabled = key?.GetValue("fDenyTSConnections") is 0;
            result.Add("securite.bureau_a_distance_actif", enabled ? 1 : 0, "");
            findings["BureauADistance"] = enabled;
        });

        result.Try("hosts", () =>
        {
            var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            if (File.Exists(path))
            {
                var suspicious = HostsFile.Suspicious(File.ReadLines(path));
                result.Add("reseau.hosts_entrees_suspectes", suspicious.Count, "");
                findings["Hosts"] = suspicious;
            }
        });

        result.Try("proxy", () =>
        {
            var proxies = new List<string>();
            foreach (var profile in UserProfiles.All())
            {
                using var settings = profile.OpenKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                if (settings?.GetValue("ProxyEnable") is 1 && settings.GetValue("ProxyServer") is string server && server.Length > 0)
                {
                    proxies.Add(server);
                }

                if (settings?.GetValue("AutoConfigURL") is string pac && pac.Length > 0)
                {
                    proxies.Add(pac);
                }
            }

            result.Add("reseau.proxy_configure", proxies.Count, "");
            findings["Proxy"] = proxies;
        });

        result.Try("dns", () =>
        {
            using var interfaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
            var servers = new List<string>();
            foreach (var name in interfaces?.GetSubKeyNames() ?? [])
            {
                using var adapter = interfaces!.OpenSubKey(name);
                if (adapter?.GetValue("NameServer") is string configured && configured.Length > 0)
                {
                    servers.AddRange(configured.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
                }
            }

            var unknown = DnsServers.Unknown(servers);
            result.Add("reseau.dns_inconnus", unknown.Count, "");
            findings["DnsManuels"] = servers.Distinct().ToList();
        });

        result.Try("wifi_ouverts", () =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Wlansvc", "Profiles", "Interfaces");
            if (!Directory.Exists(folder))
            {
                return;
            }

            var open = Directory.EnumerateFiles(folder, "*.xml", SearchOption.AllDirectories)
                .Select(f => WifiProfile.OpenNetworkName(XDocument.Load(f)))
                .OfType<string>()
                .Distinct()
                .ToList();
            result.Add("reseau.wifi_ouverts_enregistres", open.Count, "");
            findings["WifiOuverts"] = open;
        });

        result.Try("demarrage_suspects", () =>
        {
            var suspicious = new List<string>();
            foreach (var entry in StartupInventory.List().Where(e => e.Enabled))
            {
                if (ProgramPaths.Executable(entry.Command) is { } exe && !ProgramPaths.IsWindows(exe) && ProgramPaths.IsUserWritable(exe)
                    && Authenticode.Check(exe) is SignatureState.NonSigne or SignatureState.Invalide)
                {
                    suspicious.Add($"{entry.Name} : {exe}");
                }
            }

            result.Add("securite.demarrage_suspects", suspicious.Count, "");
            findings["DemarrageSuspects"] = suspicious;
        });

        result.Try("taches_suspectes", () =>
        {
            var folder = Path.Combine(Environment.SystemDirectory, "Tasks");
            var suspicious = new List<string>();
            foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (file.Contains(@"\Tasks\Microsoft\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                XDocument xml;
                try
                {
                    xml = XDocument.Load(file);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var exec in xml.Descendants().Where(e => e.Name.LocalName == "Exec"))
                {
                    var command = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value ?? "";
                    var arguments = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "";
                    if (ScheduledTasks.IsSuspicious(command, arguments))
                    {
                        suspicious.Add($"{Path.GetRelativePath(folder, file)} : {command} {arguments}".Trim());
                    }
                }
            }

            result.Add("securite.taches_suspectes", suspicious.Count, "");
            findings["TachesSuspectes"] = suspicious.Take(20).ToList();
        });

        result.Try("logiciels_vulnerables", () =>
        {
            var vulnerable = VulnerableSoftware.Find(InstalledPrograms.All());
            result.Add("securite.logiciels_vulnerables", vulnerable.Count, "");
            findings["LogicielsVulnerables"] = vulnerable;
        });

        result.Try("navigateurs", () =>
        {
            var notifications = new List<string>();
            var extensions = new List<string>();
            foreach (var profile in UserProfiles.All())
            {
                foreach (var browser in Browsers.ProfileFolders(profile.LocalAppData))
                {
                    notifications.AddRange(Browsers.NotificationSites(ReadJson(Path.Combine(browser, "Preferences"))));
                    extensions.AddRange(Browsers.SuspiciousExtensions(ReadJson(Path.Combine(browser, "Secure Preferences"))));
                    extensions.AddRange(Browsers.SuspiciousExtensions(ReadJson(Path.Combine(browser, "Preferences"))));
                }
            }

            result.Add("navigateur.sites_notifications", notifications.Distinct().Count(), "");
            result.Add("navigateur.extensions_suspectes", extensions.Distinct().Count(), "");
            findings["SitesNotifications"] = notifications.Distinct().Take(30).ToList();
            findings["ExtensionsSuspectes"] = extensions.Distinct().ToList();
        });

        result.Snapshot("menaces", findings);
        return Task.FromResult(result.Build());
    }

    private static JsonDocument? ReadJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Le navigateur garde le fichier ouvert : lecture partagée.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Exclusions de Defender qui retirent de l'analyse une grande partie du disque ou des types de fichiers dangereux.</summary>
public static class DefenderExclusions
{
    private static readonly string[] DangerousExtensions = ["exe", "dll", "scr", "ps1", "bat", "cmd", "vbs", "vbe", "js", "jse", "wsf", "hta", "msi", "lnk", "com", "pif"];
    private static readonly string[] DangerousProcesses = ["powershell.exe", "pwsh.exe", "cmd.exe", "rundll32.exe", "regsvr32.exe", "wscript.exe", "cscript.exe", "mshta.exe", "explorer.exe", "svchost.exe"];
    private static readonly string[] DangerousFolders = [@"\Windows", @"\Users", @"\AppData", @"\Temp", @"\Downloads", @"\ProgramData", @"\Users\Public", @"\Program Files", @"\Program Files (x86)"];

    public static IReadOnlyList<string> Dangerous(IEnumerable<string> paths, IEnumerable<string> extensions, IEnumerable<string> processes)
    {
        var found = new List<string>();
        foreach (var path in paths)
        {
            var normalized = path.Trim().TrimEnd('\\', '*');
            var isRoot = normalized.Length <= 2 && normalized.EndsWith(':');
            var isBroadFolder = DangerousFolders.Any(f => normalized.EndsWith(f, StringComparison.OrdinalIgnoreCase))
                || normalized.EndsWith(@"\AppData\Local", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(@"\AppData\Roaming", StringComparison.OrdinalIgnoreCase)
                || (normalized.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) && normalized.Split('\\').Length == 3);
            if (isRoot || isBroadFolder || normalized is "*" or "")
            {
                found.Add($"Dossier : {path}");
            }
        }

        found.AddRange(extensions.Where(e => DangerousExtensions.Contains(e.Trim().TrimStart('*', '.'), StringComparer.OrdinalIgnoreCase)).Select(e => $"Extension : {e}"));
        found.AddRange(processes.Where(p => DangerousProcesses.Contains(Path.GetFileName(p.Trim()), StringComparer.OrdinalIgnoreCase)).Select(p => $"Programme : {p}"));
        return found;
    }
}

/// <summary>Fichier hosts : une entrée qui détourne un site connu ou bloque un éditeur d'antivirus.</summary>
public static class HostsFile
{
    private static readonly string[] Watched =
    [
        "google.", "gstatic.", "youtube.", "facebook.", "whatsapp.", "instagram.", "microsoft.", "windowsupdate.", "live.com", "office.com",
        "outlook.", "apple.", "paypal.", "bing.", "yahoo.", "orange.", "mtn.", "kaspersky.", "avast.", "avg.", "eset.", "bitdefender.",
        "malwarebytes.", "mcafee.", "norton.", "symantec.", "virustotal.", "sophos.", "trendmicro.", "drweb.", "f-secure.", "pandasecurity.",
    ];

    public static IReadOnlyList<string> Suspicious(IEnumerable<string> lines)
    {
        var found = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _))
            {
                continue;
            }

            foreach (var host in parts.Skip(1))
            {
                var name = host.ToLowerInvariant();
                if (Watched.Any(w => name.Contains(w, StringComparison.Ordinal)))
                {
                    found.Add($"{parts[0]} {host}");
                }
            }
        }

        return found;
    }
}

/// <summary>Serveurs DNS saisis à la main : ceux des grands résolveurs publics et les adresses locales sont connus.</summary>
public static class DnsServers
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "1.1.1.1", "1.0.0.1", "1.1.1.2", "1.0.0.2", "1.1.1.3", "1.0.0.3", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112",
        "9.9.9.10", "149.112.112.10", "9.9.9.11", "149.112.112.11", "208.67.222.222", "208.67.220.220", "208.67.222.123", "208.67.220.123",
        "94.140.14.14", "94.140.15.15", "94.140.14.15", "94.140.15.16", "185.228.168.9", "185.228.169.9", "185.228.168.168", "185.228.169.168",
        "76.76.2.0", "76.76.10.0", "64.6.64.6", "64.6.65.6", "8.26.56.26", "8.20.247.20", "84.200.69.80", "84.200.70.40",
        "2606:4700:4700::1111", "2606:4700:4700::1001", "2001:4860:4860::8888", "2001:4860:4860::8844", "2620:fe::fe", "2620:fe::9",
        "2620:119:35::35", "2620:119:53::53", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff",
    };

    public static IReadOnlyList<string> Unknown(IEnumerable<string> servers) => servers
        .Select(s => s.Trim())
        .Where(s => IPAddress.TryParse(s, out var address) && !Known.Contains(s) && !IsLocal(address))
        .Distinct()
        .ToList();

    private static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127));
    }
}

/// <summary>Profil Wi-Fi enregistré par Windows (fichier XML du service WLAN).</summary>
public static class WifiProfile
{
    /// <summary>
    /// Le nom du réseau s'il est ouvert (ni authentification ni chiffrement) et rejoint automatiquement :
    /// un faux point d'accès portant le même nom serait rejoint sans rien demander.
    /// </summary>
    public static string? OpenNetworkName(XDocument xml)
    {
        string? Value(string name) => xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        return string.Equals(Value("authentication"), "open", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Value("encryption"), "none", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Value("connectionMode"), "manual", StringComparison.OrdinalIgnoreCase)
            ? Value("name")
            : null;
    }
}

/// <summary>Tâches planifiées qui lancent un script caché, un téléchargement ou un programme depuis un dossier utilisateur.</summary>
public static class ScheduledTasks
{
    private static readonly string[] Scripted = ["powershell", "pwsh", "mshta", "wscript", "cscript", "rundll32", "regsvr32", "cmd.exe", "certutil", "bitsadmin"];
    private static readonly string[] Payloads = ["-enc", "-encodedcommand", "frombase64string", "downloadstring", "downloadfile", "invoke-webrequest", "iwr ", "iex", "http://", "https://", "-w hidden", "-windowstyle hidden", "\\appdata\\", "\\temp\\", "\\users\\public\\", ".vbs", ".js", ".jse", ".hta"];

    public static bool IsSuspicious(string command, string arguments)
    {
        var program = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        var args = Environment.ExpandEnvironmentVariables(arguments).ToLowerInvariant();
        var name = Path.GetFileName(program).ToLowerInvariant();
        if (Scripted.Any(s => name.StartsWith(s, StringComparison.Ordinal)))
        {
            return Payloads.Any(p => args.Contains(p, StringComparison.Ordinal));
        }

        return program.Length > 0
            && !ProgramPaths.IsWindows(program)
            && ProgramPaths.IsUserWritable(program)
            && Authenticode.Check(program) is SignatureState.NonSigne or SignatureState.Invalide;
    }
}

/// <summary>
/// Logiciels courants dont des versions anciennes ont des failles exploitées. Liste courte, à tenir à
/// jour : WinRAR (CVE-2025-8088, corrigée en 7.13), 7-Zip (CVE-2025-11001, corrigée en 25.00),
/// VLC (CVE-2024-46461, corrigée en 3.0.21).
/// </summary>
public static class VulnerableSoftware
{
    private static readonly (string Name, Version Fixed, string Flaw)[] Rules =
    [
        ("WinRAR", new Version(7, 13), "faille exploitée par des pièces jointes piégées (CVE-2025-8088)"),
        ("7-Zip", new Version(25, 0), "faille exploitée par des archives piégées (CVE-2025-11001)"),
        ("VLC media player", new Version(3, 0, 21), "faille exploitée par des vidéos piégées (CVE-2024-46461)"),
    ];

    public static IReadOnlyList<string> Find(IEnumerable<InstalledPrograms.Program> programs)
    {
        var found = new List<string>();
        foreach (var program in programs)
        {
            foreach (var (name, fixedVersion, flaw) in Rules)
            {
                if (program.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && Parse(program.Version) is { } version && version < fixedVersion)
                {
                    found.Add($"{program.Name} {program.Version} : {flaw}. Installez la version {fixedVersion} ou plus récente.");
                }
            }
        }

        return found.Distinct().ToList();
    }

    internal static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split('.', ' ', '-').TakeWhile(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)).Take(4).Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToList();
        return parts.Count switch
        {
            0 => null,
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3]),
        };
    }
}

/// <summary>Navigateurs basés sur Chromium (Chrome, Edge, Brave) : sites autorisés à notifier, extensions installées hors boutique.</summary>
public static class Browsers
{
    private static readonly string[] UserDataFolders =
    [
        Path.Combine("Google", "Chrome", "User Data"),
        Path.Combine("Microsoft", "Edge", "User Data"),
        Path.Combine("BraveSoftware", "Brave-Browser", "User Data"),
    ];

    public static IEnumerable<string> ProfileFolders(string localAppData)
    {
        foreach (var folder in UserDataFolders.Select(f => Path.Combine(localAppData, f)).Where(Directory.Exists))
        {
            foreach (var profile in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(profile);
                if (name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal))
                {
                    yield return profile;
                }
            }
        }
    }

    /// <summary>« profile.content_settings.exceptions.notifications » : un réglage à 1 autorise le site.</summary>
    public static IEnumerable<string> NotificationSites(JsonDocument? preferences)
    {
        using (preferences)
        {
            if (preferences is null || !TryGet(preferences.RootElement, out var sites, "profile", "content_settings", "exceptions", "notifications") || sites.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return sites.EnumerateObject()
                .Where(s => s.Value.TryGetProperty("setting", out var setting) && setting.ValueKind == JsonValueKind.Number && setting.GetInt32() == 1)
                .Select(s => s.Name.Split(',')[0])
                .ToList();
        }
    }

    /// <summary>
    /// Extensions chargées hors de la boutique : décompressées (4), passées en ligne de commande (8) ou
    /// installées par un programme sans venir de la boutique. Les extensions de l'entreprise (stratégie)
    /// et celles du navigateur lui-même sont exclues.
    /// </summary>
    public static IEnumerable<string> SuspiciousExtensions(JsonDocument? preferences)
    {
        using (preferences)
        {
            if (preferences is null || !TryGet(preferences.RootElement, out var settings, "extensions", "settings") || settings.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var found = new List<string>();
            foreach (var extension in settings.EnumerateObject())
            {
                var value = extension.Value;
                var location = value.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                var fromStore = value.TryGetProperty("from_webstore", out var w) && w.ValueKind == JsonValueKind.True;
                var name = TryGet(value, out var n, "manifest", "name") && n.ValueKind == JsonValueKind.String ? n.GetString() : extension.Name;
                if (location is 4 or 8 || (location is 2 or 3 or 6 && !fromStore))
                {
                    found.Add(name ?? extension.Name);
                }
            }

            return found;
        }
    }

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }
}
