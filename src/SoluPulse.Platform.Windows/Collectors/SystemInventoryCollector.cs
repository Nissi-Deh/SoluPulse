using System.ServiceProcess;
using Microsoft.Win32;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Programmes au démarrage, services, pilotes en erreur et redémarrage en attente (EF-COL-07).
/// Seuls des noms et des états sont relevés : aucun contenu de fichier (ENF-CONF-02).
/// </summary>
public sealed class SystemInventoryCollector(TimeProvider timeProvider) : ICollector
{
    /// <summary>Services en démarrage automatique qui s'arrêtent d'eux-mêmes normalement.</summary>
    private static readonly HashSet<string> NormallyStoppedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "gupdate", "edgeupdate", "MapsBroker", "sppsvc", "RemoteRegistry", "wuauserv", "TrustedInstaller",
        "WbioSrvc", "tiledatamodelsvc", "GoogleUpdaterService", "GoogleUpdaterInternalService", "BITS",
        "ShellHWDetection", "CDPSvc", "MicrosoftEdgeElevationService", "sedsvc", "WSearch",
    };

    // Codes du gestionnaire de périphériques à ignorer : 22 désactivé par l'utilisateur, 45 non connecté.
    private static readonly HashSet<uint> IgnoredDeviceErrors = [22, 45];

    public string Name => "inventaire";

    public TimeSpan Interval => TimeSpan.FromHours(1);

    public static string ServiceIsTriggerStarted(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}\TriggerInfo");
        return key is null ? "" : "declencheur";
    }

    /// <summary>Services automatiques arrêtés qui devraient tourner, partagé avec l'action de redémarrage.</summary>
    public static IReadOnlyList<ServiceController> StoppedAutomaticServices()
    {
        if (Environment.TickCount64 < TimeSpan.FromMinutes(15).TotalMilliseconds)
        {
            return []; // Les services à démarrage différé n'ont pas encore démarré.
        }

        return ServiceController.GetServices()
            .Where(s =>
            {
                try
                {
                    return s.StartType == ServiceStartMode.Automatic
                        && s.Status == ServiceControllerStatus.Stopped
                        && !NormallyStoppedServices.Contains(s.ServiceName)
                        && ServiceIsTriggerStarted(s.ServiceName).Length == 0;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })
            .ToList();
    }

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);

        result.Try("demarrage", () =>
        {
            var entries = StartupInventory.List();
            result.Add("systeme.programmes_demarrage", entries.Count(e => e.Enabled), "");
            result.Snapshot("demarrage", entries.Select(e => new { e.Id, Nom = e.Name, Emplacement = e.Location.ToString(), Active = e.Enabled }));
        });

        result.Try("services", () =>
        {
            var stopped = StoppedAutomaticServices();
            result.Add("systeme.services_auto_arretes", stopped.Count, "");
            result.Snapshot("services_arretes", stopped.Select(s => new { Nom = s.ServiceName, Libelle = s.DisplayName }));
            foreach (var service in stopped)
            {
                service.Dispose();
            }
        });

        result.Try("pilotes", () =>
        {
            var devices = Wmi.Query(@"\\.\root\cimv2", "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
                .Select(d => new { Nom = Wmi.Get<string>(d, "Name") ?? Wmi.Get<string>(d, "DeviceID") ?? "?", Code = Wmi.Get<uint>(d, "ConfigManagerErrorCode") })
                .Where(d => !IgnoredDeviceErrors.Contains(d.Code))
                .ToList();
            result.Add("systeme.pilotes_en_erreur", devices.Count, "");
            result.Snapshot("peripheriques_en_erreur", devices);
        });

        result.Try("redemarrage", () =>
        {
            using var windowsUpdate = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            result.Add("systeme.redemarrage_en_attente", windowsUpdate is not null || servicing is not null ? 1 : 0, "");
        });

        result.Try("windows", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            result.Snapshot("windows", new
            {
                Edition = key?.GetValue("ProductName") as string,
                Version = key?.GetValue("DisplayVersion") as string,
                Build = $"{key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}",
                Machine = Environment.MachineName,
                Processeurs = Environment.ProcessorCount,
            });
        });

        return Task.FromResult(result.Build());
    }
}
