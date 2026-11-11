using System.Globalization;
using System.Management;
using Microsoft.Win32;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;

namespace SoluPulse.Platform.Windows;

/// <summary>Un point de restauration Windows, tel que l'outil de restauration du système l'affiche.</summary>
public sealed record RestorePointInfo(uint Sequence, string Description, DateTimeOffset CreatedAt);

public enum SystemProtection
{
    Active,
    Desactivee,
    BloqueeParStrategie,
}

/// <summary>
/// Points de restauration via la classe WMI SystemRestore (EF-COR-03). Windows n'en crée pas plus d'un
/// par 24 heures par défaut : dans ce cas l'appel réussit sans en créer un nouveau, et le point récent
/// reste disponible. La sauvegarde propre à chaque action reste donc nécessaire. Une capture demandée
/// par l'utilisateur lève cette limite le temps de l'appel.
/// </summary>
public sealed class RestorePointService : IRestorePointService, IDisposable
{
    /// <summary>Préfixe des descriptions : il distingue les captures de SoluPulse dans l'outil de Windows.</summary>
    public const string Prefix = "SoluPulse";

    private const string Scope = @"\\.\root\default";
    private const uint ModifySettings = 12;
    private const uint BeginSystemChange = 100;
    private const string SettingsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";
    private const string ClientsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string SystemRestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";

    // Une seule capture à la fois : Windows refuse les créations simultanées.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Create(description), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Capture immédiate, même si un point a été créé il y a moins de 24 heures. Le résultat n'est un
    /// succès que si un nouveau point apparaît réellement dans la liste de Windows.
    /// </summary>
    public async Task<RestorePointResult> CreateNowAsync(string description, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var before = List().Select(p => p.Sequence).DefaultIfEmpty(0u).Max();
                using var key = Registry.LocalMachine.CreateSubKey(SettingsKey, writable: true);
                var previous = key.GetValue(FrequencyValue);
                key.SetValue(FrequencyValue, 0, RegistryValueKind.DWord);
                RestorePointResult result;
                try
                {
                    result = Create(description);
                }
                finally
                {
                    if (previous is int minutes)
                    {
                        key.SetValue(FrequencyValue, minutes, RegistryValueKind.DWord);
                    }
                    else
                    {
                        key.DeleteValue(FrequencyValue, throwOnMissingValue: false);
                    }
                }

                // Les messages de Create sont des fragments (« code d'erreur 5 ») : on les complète.
                if (!result.Success)
                {
                    return new RestorePointResult(false, $"Windows n'a pas créé la capture : {result.Message}.");
                }

                return List().Any(p => p.Sequence > before)
                    ? new RestorePointResult(true, "Capture créée.")
                    : new RestorePointResult(false, "Windows n'a pas créé la capture : l'espace réservé aux points de restauration est peut-être plein.");
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public const string ManualDescription = "SoluPulse : capture manuelle";
    public const string WeeklyDescription = "SoluPulse : capture hebdomadaire";
    public static readonly TimeSpan WeeklyGap = TimeSpan.FromDays(7);

    /// <summary>Délai entre deux captures manuelles : chaque capture prend de la place sur C:.</summary>
    public static readonly TimeSpan ManualGap = TimeSpan.FromMinutes(5);

    /// <summary>Qui a créé le point : manuelle, hebdomadaire, correction (SoluPulse) ou windows.</summary>
    public static string Origin(string description) => description switch
    {
        ManualDescription => "manuelle",
        WeeklyDescription => "hebdomadaire",
        _ when description.StartsWith(Prefix, StringComparison.Ordinal) => "correction",
        _ => "windows",
    };

    /// <summary>
    /// Une capture hebdomadaire est due quand le dernier point, quelle que soit son origine (mise à jour
    /// de Windows, correction, capture manuelle), a au moins 7 jours.
    /// </summary>
    public static bool IsWeeklyDue(DateTimeOffset? latest, DateTimeOffset now) => latest is not { } last || now - last >= WeeklyGap;

    /// <summary>Le point de restauration le plus récent, s'il y en a un.</summary>
    public static RestorePointInfo? Latest() => List() is { Count: > 0 } points ? points[0] : null;

    /// <summary>Points de restauration présents sur le PC, du plus récent au plus ancien.</summary>
    public static IReadOnlyList<RestorePointInfo> List()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(Scope), new ObjectQuery("SELECT SequenceNumber, Description, CreationTime FROM SystemRestore"));
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>()
                .Select(o =>
                {
                    using (o)
                    {
                        var created = o["CreationTime"] is string time ? new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(time)) : DateTimeOffset.MinValue;
                        return new RestorePointInfo(Convert.ToUInt32(o["SequenceNumber"], CultureInfo.InvariantCulture), o["Description"] as string ?? "", created);
                    }
                })
                .OrderByDescending(p => p.CreatedAt)
                .ToList();
        }
        catch (ManagementException)
        {
            // Protection du système désactivée partout : la classe ne renvoie rien ou échoue.
            return [];
        }
    }

    /// <summary>État de la protection du système sur le disque de Windows.</summary>
    public static SystemProtection Protection()
    {
        using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey))
        {
            if (policy?.GetValue("DisableSR") is 1 || policy?.GetValue("DisableConfig") is 1)
            {
                return SystemProtection.BloqueeParStrategie;
            }
        }

        using var clients = Registry.LocalMachine.OpenSubKey(ClientsKey);
        if (clients?.GetValue(SystemRestoreClient) is string[] volumes)
        {
            return IsSystemDriveProtected(volumes, Drives.SystemRoot) ? SystemProtection.Active : SystemProtection.Desactivee;
        }

        // Liste des volumes absente : Windows la supprime quand la protection est coupée partout.
        return SystemProtection.Desactivee;
    }

    /// <summary>Les volumes protégés s'écrivent « \\?\Volume{…}\:(C%3A) ».</summary>
    internal static bool IsSystemDriveProtected(IEnumerable<string> volumes, string systemRoot)
    {
        var tag = $"({char.ToUpperInvariant(systemRoot[0])}%3A)";
        return volumes.Any(v => v.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Active la protection du système sur le disque de Windows (méthode WMI SystemRestore.Enable).</summary>
    public static uint Enable() => Invoke("Enable", Drives.SystemRoot);

    private static uint Invoke(string method, string drive)
    {
        using var restore = new ManagementClass(new ManagementScope(Scope), new ManagementPath("SystemRestore"), null);
        using var parameters = restore.GetMethodParameters(method);
        parameters["Drive"] = drive;
        using var output = restore.InvokeMethod(method, parameters, null);
        return Convert.ToUInt32(output?["ReturnValue"] ?? 1u, CultureInfo.InvariantCulture);
    }

    private static RestorePointResult Create(string description)
    {
        try
        {
            using var restore = new ManagementClass(new ManagementScope(Scope), new ManagementPath("SystemRestore"), null);
            using var parameters = restore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description.Length > 64 ? description[..64] : description;
            parameters["RestorePointType"] = ModifySettings;
            parameters["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", parameters, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u, CultureInfo.InvariantCulture);
            return code switch
            {
                0 => new RestorePointResult(true, "Point de restauration créé."),
                1058 => new RestorePointResult(false, "la protection du système est désactivée sur ce PC"),
                _ => new RestorePointResult(false, $"code d'erreur {code}"),
            };
        }
        catch (ManagementException ex)
        {
            return new RestorePointResult(false, ex.Message);
        }
    }
}
