using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>
/// Réactive l'environnement de récupération de Windows : l'outil de secours qui s'ouvre quand Windows
/// ne démarre plus, et qui permet de revenir à une capture sans démarrer Windows.
/// </summary>
public sealed class EnableRecoveryEnvironmentAction : SystemActionBase, IApplicableAction
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.EnableRecoveryEnvironment, Family.Repair,
        "Réactiver l'environnement de récupération",
        "Quand Windows ne démarre plus, il ouvre un outil de secours qui permet de réparer le démarrage ou de revenir à une capture. Cet outil est désactivé sur ce PC : sans lui, il faudrait une clé USB de récupération.",
        ["Réactiver l'environnement de récupération (reagentc /enable)", "Vérifier qu'il est prêt"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromMinutes(1));

    public bool IsApplicable() => State() == false;

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(State() == true ? "actif" : "inactif");

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var result = await SystemTools.RunAsync("reagentc.exe", ["/enable"], TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return result.Succeeded && State() == true
            ? ActionOutcome.Ok("L'environnement de récupération est actif : si Windows ne démarre plus, l'outil de secours s'ouvrira.")
            : ActionOutcome.Failed($"Windows n'a pas pu réactiver l'environnement de récupération (code {result.ExitCode}). Son image est peut-être absente : créez une clé USB de récupération.");
    }

    public override async Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        if (backup == "actif")
        {
            return ActionOutcome.Ok("L'environnement de récupération était déjà actif : rien à rétablir.");
        }

        var result = await SystemTools.RunAsync("reagentc.exe", ["/disable"], TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok("L'environnement de récupération est de nouveau désactivé.")
            : ActionOutcome.Failed($"Impossible de désactiver l'environnement de récupération (code {result.ExitCode}).");
    }

    internal static bool? State()
    {
        var file = Path.Combine(Environment.SystemDirectory, "Recovery", "ReAgent.xml");
        try
        {
            return File.Exists(file) ? ResilienceCollector.RecoveryEnvironmentEnabled(XDocument.Load(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}

/// <summary>
/// Coupe le Bureau à distance : il permet de prendre le contrôle du PC depuis le réseau, et des robots
/// essaient en permanence de deviner les mots de passe. « Annuler » le rallume.
/// </summary>
public sealed class DisableRemoteDesktopAction : SystemActionBase, IApplicableAction
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string Value = "fDenyTSConnections";

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DisableRemoteDesktop, Family.Security,
        "Couper le Bureau à distance",
        "Le Bureau à distance permet de prendre le contrôle du PC depuis le réseau. Si vous ne vous en servez pas, le couper ferme une porte que les pirates essaient d'ouvrir en devinant les mots de passe. Les sessions à distance en cours seront fermées.",
        ["Mémoriser le réglage actuel", "Refuser les connexions du Bureau à distance"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromSeconds(5));

    public bool IsApplicable()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(Value) is 0;
    }

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return Task.FromResult<string?>(key?.GetValue(Value) is int current ? current.ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent");
    }

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        key.SetValue(Value, 1, RegistryValueKind.DWord);
        return Task.FromResult(ActionOutcome.Ok("Le Bureau à distance est coupé : plus personne ne peut prendre le contrôle du PC depuis le réseau."));
    }

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        if (backup == "absent")
        {
            key.DeleteValue(Value, throwOnMissingValue: false);
        }
        else if (int.TryParse(backup, System.Globalization.CultureInfo.InvariantCulture, out var previous) && previous is 0 or 1)
        {
            key.SetValue(Value, previous, RegistryValueKind.DWord);
        }
        else
        {
            return Task.FromResult(ActionOutcome.Failed("Sauvegarde du réglage illisible."));
        }

        return Task.FromResult(ActionOutcome.Ok("Le réglage du Bureau à distance est rétabli."));
    }
}

/// <summary>
/// Nettoie une clé USB touchée par le virus « raccourci » : les raccourcis piégés, scripts cachés et
/// autorun.inf partent en quarantaine sur le PC (rien n'est supprimé), et les dossiers cachés par le
/// virus redeviennent visibles. « Annuler » remet tout en place.
/// </summary>
public sealed class CleanUsbDriveAction : SystemActionBase, IActionParameterChoices
{
    public const string DriveParameter = "lecteur";

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanUsbDrive, Family.Security,
        "Nettoyer la clé USB infectée",
        "Le virus « raccourci » cache vos dossiers et les remplace par des raccourcis qui l'installent sur chaque PC où la clé est branchée. SoluPulse met les raccourcis piégés et les scripts en quarantaine sur ce PC, puis rend vos dossiers visibles. Vos fichiers ne sont pas touchés.",
        ["Lister les fichiers piégés à la racine de la clé", "Les déplacer en quarantaine sur le PC", "Rendre visibles les dossiers cachés par le virus"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromSeconds(30),
        ParameterNames: [DriveParameter],
        TouchesSystem: false);

    private static string QuarantineRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoluPulse", "Quarantaine");

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ParameterChoice>>(() => parameter != DriveParameter
            ? []
            : UsbThreats.RemovableDrives()
                .Select(d => (Drive: d, Threats: UsbThreats.Scan(d)))
                .Where(d => d.Threats.Any(t => t.Kind != UsbThreatKind.DossierCache))
                .Select(d => new ParameterChoice(d.Drive, $"Clé {d.Drive.TrimEnd('\\')}", $"{d.Threats.Count(t => t.Kind != UsbThreatKind.DossierCache)} fichier(s) piégé(s), {d.Threats.Count(t => t.Kind == UsbThreatKind.DossierCache)} dossier(s) caché(s)"))
                .ToList(), cancellationToken);

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        var drive = context.Get(DriveParameter);
        var choices = await GetChoicesAsync(DriveParameter, context.Caller, cancellationToken).ConfigureAwait(false);
        if (choices.All(c => !string.Equals(c.Value, drive, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ActionParameterException("Cette clé n'est pas branchée ou n'est pas infectée.");
        }
    }

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var threats = UsbThreats.Scan(context.Get(DriveParameter));
        var files = threats.Where(t => t.Kind != UsbThreatKind.DossierCache).Select(t => Path.GetFileName(t.Path)).ToList();
        return new ActionEstimate($"En quarantaine : {string.Join(", ", files)}. Dossiers rendus visibles : {threats.Count(t => t.Kind == UsbThreatKind.DossierCache)}.");
    }, cancellationToken);

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run<string?>(() =>
    {
        var drive = context.Get(DriveParameter);
        var quarantine = Path.Combine(QuarantineRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{drive[0]}");
        var threats = UsbThreats.Scan(drive);
        var plan = new UsbCleanPlan(
            quarantine,
            threats.Where(t => t.Kind != UsbThreatKind.DossierCache).Select(t => new MovedFile(t.Path, Path.Combine(quarantine, Path.GetFileName(t.Path)))).ToList(),
            threats.Where(t => t.Kind == UsbThreatKind.DossierCache).Select(t => new HiddenFolder(t.Path, File.GetAttributes(t.Path))).ToList());
        return JsonSerializer.Serialize(plan);
    }, cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var plan = Read(context.Backup);
        if (plan is null)
        {
            return ActionOutcome.Failed("Plan de nettoyage introuvable.");
        }

        Directory.CreateDirectory(plan.Quarantine);
        int moved = 0, failed = 0;
        foreach (var file in plan.Files)
        {
            try
            {
                File.SetAttributes(file.Original, FileAttributes.Normal);
                File.Move(file.Original, file.Quarantined);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        foreach (var folder in plan.Folders.Where(f => Directory.Exists(f.Path)))
        {
            File.SetAttributes(folder.Path, folder.Attributes & ~(FileAttributes.Hidden | FileAttributes.System));
        }

        return failed == 0
            ? ActionOutcome.Ok($"Clé nettoyée : {moved} fichier(s) piégé(s) en quarantaine, {plan.Folders.Count} dossier(s) de nouveau visibles. Lancez une analyse Defender de la clé pour finir.")
            : ActionOutcome.Failed($"{moved} fichier(s) mis en quarantaine, {failed} n'ont pas pu être déplacés : la clé est peut-être protégée en écriture.");
    }, cancellationToken);

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var plan = Read(backup);
        if (plan is null)
        {
            return ActionOutcome.Failed("Sauvegarde du nettoyage illisible.");
        }

        foreach (var file in plan.Files.Where(f => File.Exists(f.Quarantined) && !File.Exists(f.Original)))
        {
            File.Move(file.Quarantined, file.Original);
        }

        foreach (var folder in plan.Folders.Where(f => Directory.Exists(f.Path)))
        {
            File.SetAttributes(folder.Path, folder.Attributes);
        }

        return ActionOutcome.Ok("Les fichiers ont retrouvé leur place sur la clé.");
    }, cancellationToken);

    private static UsbCleanPlan? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var plan = JsonSerializer.Deserialize<UsbCleanPlan>(json);

        // La quarantaine ne peut être que dans le dossier de SoluPulse.
        return plan is not null && Path.GetFullPath(plan.Quarantine).StartsWith(QuarantineRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? plan
            : null;
    }

    private sealed record UsbCleanPlan(string Quarantine, IReadOnlyList<MovedFile> Files, IReadOnlyList<HiddenFolder> Folders);

    private sealed record MovedFile(string Original, string Quarantined);

    private sealed record HiddenFolder(string Path, FileAttributes Attributes);
}
