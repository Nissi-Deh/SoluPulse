using SoluPulse.Core.Collection;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;
using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows;

/// <summary>Point d'entrée de la couche Windows : collecteurs et catalogue d'actions.</summary>
public static class WindowsPlatform
{
    public static IReadOnlyList<ICollector> Collectors(TimeProvider timeProvider, NetworkProbeOptions networkOptions) =>
    [
        new HardwareSensorsCollector(timeProvider),
        new PerformanceCollector(timeProvider),
        new DiskSpaceCollector(timeProvider),
        new NetworkCollector(timeProvider, networkOptions),
        new EventLogCollector(timeProvider),
        new BatteryCollector(timeProvider),
        new SystemInventoryCollector(timeProvider),
        new StorageHealthCollector(timeProvider),
        new WindowsUpdateCollector(timeProvider),
        new SecurityCollector(timeProvider),
        new HardwareInventoryCollector(timeProvider),
        new DiskUsageCollector(timeProvider),
        new ResilienceCollector(timeProvider),
        new ThreatCollector(timeProvider),
        new ProcessActivityCollector(timeProvider),
        new UsbScanCollector(timeProvider),
        new RansomwareCollector(timeProvider),
    ];

    public static IReadOnlyList<ISystemAction> Actions() =>
    [
        new CleanTempAction(),
        new CleanWindowsUpdateCacheAction(),
        new CleanDeliveryOptimizationCacheAction(),
        new EmptyRecycleBinAction(),
        new DisableStartupProgramAction(),
        new BalancedPowerPlanAction(),
        new OptimizeDriveAction(),
        new DisableFastStartupAction(),
        new MoveUserFolderAction(),
        new DeleteLargeFilesAction(),
        new CleanDeveloperCachesAction(),
        new CleanUpdateLeftoversAction(),
        new ComponentStoreCleanupAction(),
        new DisableHibernationAction(),
        new DockerDiskGuide(),
        new EnableSystemProtectionAction(),
        new EnableRecoveryEnvironmentAction(),
        new RescanDevicesAction(),
        new InstallDriverAction(),
        new SystemFileCheckAction(),
        new DismRestoreHealthAction(),
        new ScheduleChkdskAction(),
        new StartServiceAction(),
        new ResetWindowsUpdateAction(),
        new FlushDnsAction(),
        new RenewIpAction(),
        new ResetWinsockAction(),
        new ResetTcpIpAction(),
        new SetDnsAction(),
        new RestartAdapterAction(),
        new EnableFirewallAction(),
        new UpdateDefenderSignaturesAction(),
        new DefenderQuickScanAction(),
        new DisableRemoteDesktopAction(),
        new CleanUsbDriveAction(),
        .. HardwareGuides.All,
        .. SecurityGuides.All,
        .. PreventionGuides.All,
    ];
}
