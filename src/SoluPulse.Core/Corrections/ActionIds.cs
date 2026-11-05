namespace SoluPulse.Core.Corrections;

/// <summary>Identifiants du catalogue V1 (cahier des charges §3.3).</summary>
public static class ActionIds
{
    // Nettoyage et optimisation
    public const string CleanTemp = "ACT-CLEAN-TEMP";
    public const string CleanWindowsUpdateCache = "ACT-CLEAN-WU-CACHE";
    public const string CleanDeliveryOptimizationCache = "ACT-CLEAN-DO-CACHE";
    public const string EmptyRecycleBin = "ACT-CLEAN-RECYCLE-BIN";
    public const string DisableStartupProgram = "ACT-STARTUP-DISABLE";
    public const string BalancedPowerPlan = "ACT-POWER-BALANCED";
    public const string OptimizeDrive = "ACT-DRIVE-OPTIMIZE";

    // Réparations système
    public const string SystemFileCheck = "ACT-SYS-SFC";
    public const string DismRestoreHealth = "ACT-SYS-DISM";
    public const string ScheduleChkdsk = "ACT-SYS-CHKDSK";
    public const string StartService = "ACT-SYS-SERVICE-START";
    public const string ResetWindowsUpdate = "ACT-SYS-WU-RESET";

    // Réseau
    public const string FlushDns = "ACT-NET-FLUSH-DNS";
    public const string RenewIp = "ACT-NET-RENEW-IP";
    public const string ResetWinsock = "ACT-NET-RESET-WINSOCK";
    public const string ResetTcpIp = "ACT-NET-RESET-TCPIP";
    public const string SetDns = "ACT-NET-SET-DNS";
    public const string RestartAdapter = "ACT-NET-RESTART-ADAPTER";

    // Matériel : recommandations guidées
    public const string GuideCooling = "ACT-HW-GUIDE-COOLING";
    public const string GuideDiskReplacement = "ACT-HW-GUIDE-DISK";
    public const string GuideBattery = "ACT-HW-GUIDE-BATTERY";
    public const string GuideBackup = "ACT-HW-GUIDE-BACKUP";

    public static IReadOnlySet<string> All { get; } = typeof(ActionIds)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}

/// <summary>Les recommandations matérielles : rien n'est exécuté, la fiche guide l'utilisateur.</summary>
public static class HardwareGuides
{
    private const string Family = "Matériel";

    public static IReadOnlyList<GuidanceAction> All { get; } =
    [
        new(new ActionDescriptor(
            ActionIds.GuideCooling, Family,
            "Améliorer le refroidissement",
            "Le PC chauffe trop. La poussière bouche souvent les ventilateurs et les grilles d'aération.",
            [
                "Éteignez le PC et débranchez-le.",
                "Posez un portable sur une surface dure et plane, jamais sur un lit ou un coussin.",
                "Soufflez la poussière des grilles avec une bombe d'air sec, par petites pressions.",
                "Si la chauffe continue, faites changer la pâte thermique par un technicien.",
            ],
            RiskLevel.Faible, false, false, TimeSpan.FromMinutes(15))),
        new(new ActionDescriptor(
            ActionIds.GuideDiskReplacement, Family,
            "Prévoir le remplacement du disque",
            "Le disque montre des signes d'usure. Il peut tomber en panne et vos fichiers devenir illisibles.",
            [
                "Sauvegardez dès aujourd'hui vos documents, photos et fichiers importants sur un disque externe ou un service en ligne.",
                "Faites remplacer le disque par un technicien.",
                "Évitez les gros transferts de fichiers d'ici là.",
            ],
            RiskLevel.Faible, false, false, TimeSpan.FromMinutes(30))),
        new(new ActionDescriptor(
            ActionIds.GuideBattery, Family,
            "Préserver ou remplacer la batterie",
            "La batterie a perdu une grande partie de sa capacité d'origine.",
            [
                "Évitez de laisser le portable branché en permanence à 100 % quand c'est possible.",
                "Évitez la chaleur : ne laissez pas le portable au soleil ou dans une voiture.",
                "Si l'autonomie ne suffit plus, faites remplacer la batterie par un technicien.",
                "Si la batterie gonfle (coque déformée, pavé tactile qui se soulève), arrêtez d'utiliser le PC et consultez un technicien.",
            ],
            RiskLevel.Faible, false, false, TimeSpan.FromMinutes(5))),
        new(new ActionDescriptor(
            ActionIds.GuideBackup, Family,
            "Sauvegarder vos données",
            "Une sauvegarde récente vous protège si le disque tombe en panne.",
            [
                "Branchez un disque externe ou une clé USB assez grande.",
                "Copiez vos dossiers Documents, Images, Bureau et Téléchargements.",
                "Vérifiez que les fichiers copiés s'ouvrent.",
            ],
            RiskLevel.Faible, false, false, TimeSpan.FromMinutes(30))),
    ];
}
