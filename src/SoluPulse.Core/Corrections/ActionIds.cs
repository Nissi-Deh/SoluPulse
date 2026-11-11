namespace SoluPulse.Core.Corrections;

/// <summary>Identifiants du catalogue (cahier des charges §3.3), complété par les actions de sécurité.</summary>
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
    public const string DisableFastStartup = "ACT-POWER-FASTSTARTUP-OFF";

    // Espace disque : actions qui libèrent réellement de la place
    public const string MoveUserFolder = "ACT-DISK-MOVE-FOLDER";
    public const string DeleteLargeFiles = "ACT-DISK-LARGE-FILES";
    public const string CleanDeveloperCaches = "ACT-CLEAN-DEV-CACHES";
    public const string CleanUpdateLeftovers = "ACT-CLEAN-UPDATE-LEFTOVERS";
    public const string CleanComponentStore = "ACT-CLEAN-COMPONENTS";
    public const string DisableHibernation = "ACT-POWER-HIBERNATE-OFF";
    public const string GuideDocker = "ACT-DISK-GUIDE-DOCKER";

    // Réparations système
    public const string SystemFileCheck = "ACT-SYS-SFC";
    public const string DismRestoreHealth = "ACT-SYS-DISM";
    public const string ScheduleChkdsk = "ACT-SYS-CHKDSK";
    public const string StartService = "ACT-SYS-SERVICE-START";
    public const string ResetWindowsUpdate = "ACT-SYS-WU-RESET";
    public const string EnableSystemProtection = "ACT-SYS-RESTORE-ON";

    // Réseau
    public const string FlushDns = "ACT-NET-FLUSH-DNS";
    public const string RenewIp = "ACT-NET-RENEW-IP";
    public const string ResetWinsock = "ACT-NET-RESET-WINSOCK";
    public const string ResetTcpIp = "ACT-NET-RESET-TCPIP";
    public const string SetDns = "ACT-NET-SET-DNS";
    public const string RestartAdapter = "ACT-NET-RESTART-ADAPTER";

    // Sécurité
    public const string EnableFirewall = "ACT-SEC-FIREWALL-ON";
    public const string UpdateDefenderSignatures = "ACT-SEC-DEFENDER-UPDATE";
    public const string DefenderQuickScan = "ACT-SEC-DEFENDER-SCAN";

    // Sécurité : recommandations guidées
    public const string GuideAntivirus = "ACT-SEC-GUIDE-ANTIVIRUS";
    public const string GuideUac = "ACT-SEC-GUIDE-UAC";
    public const string GuideSecureBoot = "ACT-SEC-GUIDE-SECUREBOOT";
    public const string GuideEncryption = "ACT-SEC-GUIDE-ENCRYPTION";

    // Matériel : recommandations guidées
    public const string GuideCooling = "ACT-HW-GUIDE-COOLING";
    public const string GuideDiskReplacement = "ACT-HW-GUIDE-DISK";
    public const string GuideBattery = "ACT-HW-GUIDE-BATTERY";
    public const string GuideBackup = "ACT-HW-GUIDE-BACKUP";

    // Prévention : actions
    public const string EnableRecoveryEnvironment = "ACT-SYS-WINRE-ON";
    public const string DisableRemoteDesktop = "ACT-SEC-RDP-OFF";
    public const string CleanUsbDrive = "ACT-SEC-USB-CLEAN";

    // Prévention : électricité et matériel
    public const string GuideUps = "ACT-HW-GUIDE-UPS";
    public const string GuideUpsBattery = "ACT-HW-GUIDE-UPS-BATTERY";
    public const string GuidePowerSupply = "ACT-HW-GUIDE-POWER-SUPPLY";
    public const string GuideCharger = "ACT-HW-GUIDE-CHARGER";
    public const string GuideCmosBattery = "ACT-HW-GUIDE-CMOS";
    public const string GuideChargeLimit = "ACT-HW-GUIDE-CHARGE-LIMIT";
    public const string GuideMemoryTest = "ACT-HW-GUIDE-MEMTEST";
    public const string GuideSsd = "ACT-HW-GUIDE-SSD";
    public const string GuideSataCable = "ACT-HW-GUIDE-SATA-CABLE";
    public const string GuideDiskFirmware = "ACT-HW-GUIDE-FIRMWARE";
    public const string GuideBios = "ACT-HW-GUIDE-BIOS";
    public const string GuideGraphicsDriver = "ACT-HW-GUIDE-GPU-DRIVER";
    public const string GuideWifiDriver = "ACT-HW-GUIDE-WIFI-DRIVER";

    // Prévention : sécurité et données
    public const string GuideDefenderExclusions = "ACT-SEC-GUIDE-EXCLUSIONS";
    public const string GuideRansomware = "ACT-SEC-GUIDE-RANSOMWARE";
    public const string GuideSuspiciousProgram = "ACT-SEC-GUIDE-SUSPICIOUS";
    public const string GuideUpdateSoftware = "ACT-SEC-GUIDE-UPDATE-SOFTWARE";
    public const string GuideBrowser = "ACT-SEC-GUIDE-BROWSER";
    public const string GuideBitLockerKey = "ACT-SEC-GUIDE-BITLOCKER-KEY";
    public const string GuideSecureBootCertificates = "ACT-SEC-GUIDE-SECUREBOOT-2023";
    public const string GuideNetworkHijack = "ACT-SEC-GUIDE-HIJACK";
    public const string GuideOpenWifi = "ACT-SEC-GUIDE-OPEN-WIFI";

    // Prévention : Windows
    public const string GuideEndOfSupport = "ACT-SYS-GUIDE-END-OF-SUPPORT";
    public const string GuideRecoveryDrive = "ACT-SYS-GUIDE-RECOVERY-DRIVE";
    public const string GuideMemoryLeak = "ACT-SYS-GUIDE-MEMORY-LEAK";

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

/// <summary>Les recommandations de sécurité qui demandent un réglage par l'utilisateur ou dans le BIOS.</summary>
public static class SecurityGuides
{
    private const string Family = "Sécurité";

    public static IReadOnlyList<GuidanceAction> All { get; } =
    [
        new(new ActionDescriptor(
            ActionIds.GuideAntivirus, Family,
            "Rétablir la protection antivirus",
            "Aucun antivirus ne protège le PC en ce moment, ou ses signatures ne sont plus à jour.",
            [
                "Ouvrez le menu Démarrer et lancez « Sécurité Windows ».",
                "Cliquez sur « Protection contre les virus et menaces ».",
                "Si un autre antivirus est installé mais expiré, renouvelez-le ou désinstallez-le : Microsoft Defender reprend alors la protection tout seul.",
                "Activez « Protection en temps réel ».",
                "Si le réglage est grisé, il est géré par votre entreprise : contactez votre support informatique.",
            ],
            RiskLevel.Faible, false, false, TimeSpan.FromMinutes(5))),
        new(new ActionDescriptor(
            ActionIds.GuideUac, Family,
            "Réactiver le contrôle de compte d'utilisateur",
            "Le contrôle de compte d'utilisateur demande votre accord avant qu'un programme modifie Windows. Il est désactivé.",
            [
                "Ouvrez le menu Démarrer et tapez « UAC ».",
                "Ouvrez « Modifier les paramètres de contrôle de compte d'utilisateur ».",
                "Placez le curseur sur le deuxième cran en partant du haut (réglage par défaut).",
                "Cliquez sur OK, puis redémarrez le PC.",
            ],
            RiskLevel.Faible, false, true, TimeSpan.FromMinutes(5))),
        new(new ActionDescriptor(
            ActionIds.GuideSecureBoot, Family,
            "Activer le démarrage sécurisé",
            "Le démarrage sécurisé empêche un programme malveillant de se lancer avant Windows. Il se règle dans le BIOS du PC.",
            [
                "Si vous n'êtes pas à l'aise avec le BIOS, demandez à un technicien : un mauvais réglage peut empêcher Windows de démarrer.",
                "Redémarrez le PC et appuyez sur la touche du BIOS dès l'allumage (F10 sur HP, F2 sur Dell, Lenovo ou Asus, Suppr sur les PC fixes).",
                "Dans l'onglet « Sécurité » ou « Démarrage », mettez « Secure Boot » sur « Enabled ».",
                "Enregistrez et quittez (souvent avec F10).",
            ],
            RiskLevel.Faible, false, true, TimeSpan.FromMinutes(10))),
        new(new ActionDescriptor(
            ActionIds.GuideEncryption, Family,
            "Chiffrer le disque du PC",
            "Le disque n'est pas chiffré : en cas de vol du PC, vos fichiers peuvent être lus en retirant le disque.",
            [
                "Windows Professionnel : ouvrez le menu Démarrer, tapez « Gérer BitLocker » et cliquez sur « Activer BitLocker ».",
                "Windows Famille : ouvrez Paramètres > Confidentialité et sécurité > Chiffrement de l'appareil.",
                "Enregistrez la clé de récupération dans votre compte Microsoft ou imprimez-la, et gardez-la en lieu sûr.",
                "Laissez le PC allumé et branché pendant le chiffrement.",
            ],
            RiskLevel.Faible, false, false, TimeSpan.FromMinutes(15))),
    ];
}
