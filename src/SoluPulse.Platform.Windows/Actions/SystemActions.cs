using System.Globalization;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>
/// Désactive un programme au démarrage comme le fait le Gestionnaire des tâches (valeur StartupApproved).
/// La commande d'origine reste en place : l'annulation remet simplement l'ancienne valeur.
/// </summary>
public sealed class DisableStartupProgramAction : SystemActionBase, IActionParameterChoices
{
    public const string EntryParameter = "entree";

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DisableStartupProgram, Family.Cleanup,
        "Désactiver un programme au démarrage",
        "Empêche le programme choisi de se lancer avec Windows. Il reste installé et vous pouvez toujours l'ouvrir vous-même.",
        ["Marquer le programme comme désactivé au démarrage, comme le Gestionnaire des tâches"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromSeconds(5),
        ParameterNames: [EntryParameter]);

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ParameterChoice>>(StartupInventory.List()
            .Where(e => e.Enabled && (e.UserSid is null || e.UserSid == caller.UserSid))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new ParameterChoice(e.Id, e.Name, e.UserSid is null ? "Tous les utilisateurs" : "Votre session"))
            .ToList());

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        Find(context);
    }

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var entry = Find(context);
        var (root, subKey, valueName) = entry.ApprovalKey(Registry.Users);
        using var key = root.OpenSubKey(subKey);
        var previous = key?.GetValue(valueName) as byte[];
        return Task.FromResult<string?>(JsonSerializer.Serialize(new Backup(entry.Id, previous is null ? null : Convert.ToBase64String(previous))));
    }

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var entry = Find(context);
        var (root, subKey, valueName) = entry.ApprovalKey(Registry.Users);
        using var key = root.CreateSubKey(subKey, writable: true);
        var value = new byte[12];
        value[0] = 0x03;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        key.SetValue(valueName, value, RegistryValueKind.Binary);
        return Task.FromResult(ActionOutcome.Ok($"« {entry.Name} » ne se lancera plus au démarrage."));
    }

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        var saved = JsonSerializer.Deserialize<Backup>(backup) ?? throw new InvalidDataException("Sauvegarde illisible.");
        var entry = StartupInventory.List().FirstOrDefault(e => e.Id == saved.EntryId)
            ?? throw new InvalidOperationException("Le programme n'existe plus au démarrage.");
        var (root, subKey, valueName) = entry.ApprovalKey(Registry.Users);
        using var key = root.CreateSubKey(subKey, writable: true);
        if (saved.PreviousValue is null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(valueName, Convert.FromBase64String(saved.PreviousValue), RegistryValueKind.Binary);
        }

        return Task.FromResult(ActionOutcome.Ok($"« {entry.Name} » se lancera de nouveau au démarrage."));
    }

    private static StartupEntry Find(ActionContext context)
    {
        var id = context.Get(EntryParameter);
        var entry = StartupInventory.List().FirstOrDefault(e => e.Id == id)
            ?? throw new ActionParameterException("Ce programme n'est plus dans la liste de démarrage.");
        if (entry.UserSid is not null && entry.UserSid != context.Caller.UserSid)
        {
            throw new ActionParameterException("Ce programme appartient à un autre utilisateur.");
        }

        if (!entry.Enabled)
        {
            throw new ActionParameterException("Ce programme est déjà désactivé.");
        }

        return entry;
    }

    private sealed record Backup(string EntryId, string? PreviousValue);
}

/// <summary>Repasse en mode « Utilisation normale » ; l'ancien plan est restauré par « Annuler ».</summary>
public sealed class BalancedPowerPlanAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.BalancedPowerPlan, Family.Cleanup,
        "Passer en mode d'alimentation normal",
        "Active le mode « Utilisation normale » de Windows, qui adapte les performances à ce que vous faites.",
        ["Mémoriser le mode d'alimentation actuel", "Activer le mode « Utilisation normale »"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromSeconds(5));

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NativeMethods.GetActivePowerScheme()?.ToString());

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NativeMethods.PowerSetActiveScheme(0, PowerSchemes.Balanced) == 0
            ? ActionOutcome.Ok("Mode « Utilisation normale » activé.")
            : ActionOutcome.Failed("Windows a refusé de changer le mode d'alimentation."));

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken) =>
        Task.FromResult(NativeMethods.PowerSetActiveScheme(0, Guid.Parse(backup)) == 0
            ? ActionOutcome.Ok("Mode d'alimentation précédent restauré.")
            : ActionOutcome.Failed("Windows a refusé de restaurer le mode d'alimentation."));
}

/// <summary>Optimisation du disque système : TRIM sur SSD, défragmentation sur HDD (outil defrag /O).</summary>
public sealed class OptimizeDriveAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.OptimizeDrive, Family.Cleanup,
        "Optimiser le disque système",
        "Lance l'optimisation intégrée à Windows : TRIM pour un SSD, défragmentation pour un disque dur. Le PC reste utilisable pendant l'opération.",
        ["Lancer l'optimiseur de lecteurs de Windows sur le disque système"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(20));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var result = await SystemTools.RunAsync("defrag.exe", [drive, "/O"], TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok($"Disque {drive} optimisé.")
            : ActionOutcome.Failed($"L'optimisation a échoué (code {result.ExitCode}).");
    }
}

public sealed class SystemFileCheckAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.SystemFileCheck, Family.Repair,
        "Vérifier et réparer les fichiers de Windows (SFC)",
        "Compare les fichiers de Windows à leur version d'origine et remplace ceux qui sont abîmés. Un point de restauration est créé avant.",
        ["Créer un point de restauration", "Lancer « sfc /scannow »"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(30));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var result = await SystemTools.RunAsync("sfc.exe", ["/scannow"], TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok("Vérification terminée : les fichiers de Windows sont intacts ou ont été réparés.")
            : ActionOutcome.Failed($"La vérification a signalé des fichiers non réparables (code {result.ExitCode}). Essayez la réparation DISM puis relancez SFC.");
    }
}

public sealed class DismRestoreHealthAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DismRestoreHealth, Family.Repair,
        "Réparer l'image de Windows (DISM)",
        "Répare la copie de référence de Windows utilisée par SFC, en téléchargeant les fichiers sains depuis Windows Update. Nécessite Internet.",
        ["Créer un point de restauration", "Lancer « DISM /Online /Cleanup-Image /RestoreHealth »"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(30));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var result = await SystemTools.RunAsync("dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"], TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok("Image de Windows réparée.")
            : ActionOutcome.Failed($"La réparation a échoué (code {result.ExitCode}). Vérifiez la connexion Internet.");
    }
}

/// <summary>Planifie une vérification du disque système au prochain démarrage (bit « dirty »).</summary>
public sealed class ScheduleChkdskAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.ScheduleChkdsk, Family.Repair,
        "Vérifier le disque au prochain démarrage",
        "Au prochain redémarrage, Windows vérifie et répare le système de fichiers avant de s'ouvrir. Cela peut prendre de quelques minutes à plus d'une heure.",
        ["Créer un point de restauration", "Planifier la vérification du disque système", "La vérification a lieu au prochain redémarrage"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: true, TimeSpan.FromMinutes(30));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var result = await SystemTools.RunAsync("fsutil.exe", ["dirty", "set", drive], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok($"Vérification de {drive} planifiée au prochain redémarrage.")
            : ActionOutcome.Failed($"Impossible de planifier la vérification (code {result.ExitCode}).");
    }
}

/// <summary>Démarre un service automatique arrêté ; « Annuler » le remet à l'arrêt.</summary>
public sealed class StartServiceAction : SystemActionBase, IActionParameterChoices
{
    public const string ServiceParameter = "service";

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.StartService, Family.Repair,
        "Redémarrer un service Windows",
        "Démarre le service choisi, qui devrait tourner mais s'est arrêté.",
        ["Démarrer le service", "Vérifier qu'il tourne"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromMinutes(1),
        ParameterNames: [ServiceParameter]);

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken)
    {
        var services = SystemInventoryCollector.StoppedAutomaticServices();
        var choices = services.Select(s => new ParameterChoice(s.ServiceName, s.DisplayName, s.ServiceName)).ToList();
        foreach (var service in services)
        {
            service.Dispose();
        }

        return Task.FromResult<IReadOnlyList<ParameterChoice>>(choices);
    }

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        var name = context.Get(ServiceParameter);
        var services = SystemInventoryCollector.StoppedAutomaticServices();
        var found = services.Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));
        foreach (var service in services)
        {
            service.Dispose();
        }

        if (!found)
        {
            throw new ActionParameterException("Ce service n'est pas un service automatique arrêté.");
        }
    }

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(context.Get(ServiceParameter));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var name = context.Get(ServiceParameter);
        await Services.StartAsync(name, cancellationToken).ConfigureAwait(false);
        using var service = new ServiceController(name);
        return service.Status == ServiceControllerStatus.Running
            ? ActionOutcome.Ok($"Le service « {service.DisplayName} » tourne.")
            : ActionOutcome.Failed($"Le service « {service.DisplayName} » n'a pas démarré.");
    }

    public override async Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        await Services.StopAsync(backup, cancellationToken).ConfigureAwait(false);
        return ActionOutcome.Ok($"Le service « {backup} » est de nouveau arrêté.");
    }
}

/// <summary>
/// Réinitialise les composants de Windows Update : les dossiers de cache sont renommés (et non
/// supprimés), ce qui permet de les remettre en place avec « Annuler ».
/// </summary>
public sealed class ResetWindowsUpdateAction : SystemActionBase
{
    private static readonly string[] ServiceNames = ["wuauserv", "cryptSvc", "bits", "msiserver"];

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.ResetWindowsUpdate, Family.Repair,
        "Réinitialiser Windows Update",
        "Remet à zéro les composants de Windows Update quand les mises à jour restent bloquées. Les anciens dossiers sont conservés pour pouvoir annuler.",
        ["Créer un point de restauration", "Arrêter les services de mise à jour", "Renommer les dossiers SoftwareDistribution et catroot2", "Relancer les services"],
        RiskLevel.Moyen, Reversible: true, RequiresReboot: false, TimeSpan.FromMinutes(3));

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string[] Folders => [Path.Combine(Windows, "SoftwareDistribution"), Path.Combine(Environment.SystemDirectory, "catroot2")];

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return Task.FromResult<string?>(JsonSerializer.Serialize(Folders.Select(f => new Renamed(f, $"{f}.solupulse-{suffix}")).ToList()));
    }

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var renames = JsonSerializer.Deserialize<List<Renamed>>(context.Backup ?? throw new InvalidOperationException("Sauvegarde manquante.")) ?? [];
        var running = await StopAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var rename in renames.Where(r => Directory.Exists(r.Original)))
            {
                Directory.Move(rename.Original, rename.Backup);
            }
        }
        finally
        {
            await StartAllAsync(running).ConfigureAwait(false);
        }

        return ActionOutcome.Ok("Windows Update a été réinitialisé. Relancez une recherche de mises à jour.");
    }

    public override async Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        var renames = JsonSerializer.Deserialize<List<Renamed>>(backup) ?? [];
        var running = await StopAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var rename in renames.Where(r => Directory.Exists(r.Backup)))
            {
                if (Directory.Exists(rename.Original))
                {
                    Directory.Move(rename.Original, rename.Original + ".solupulse-annule");
                }

                Directory.Move(rename.Backup, rename.Original);
            }
        }
        finally
        {
            await StartAllAsync(running).ConfigureAwait(false);
        }

        return ActionOutcome.Ok("Les dossiers d'origine de Windows Update ont été remis en place.");
    }

    private static async Task<List<string>> StopAllAsync(CancellationToken cancellationToken)
    {
        var running = new List<string>();
        foreach (var name in ServiceNames)
        {
            if (await Services.StopAsync(name, cancellationToken).ConfigureAwait(false))
            {
                running.Add(name);
            }
        }

        return running;
    }

    private static async Task StartAllAsync(IEnumerable<string> names)
    {
        foreach (var name in names.Reverse())
        {
            await Services.StartAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed record Renamed(string Original, string Backup);
}
