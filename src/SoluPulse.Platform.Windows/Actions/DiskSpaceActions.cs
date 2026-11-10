using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>Un dossier personnel connu de Windows (Documents, Téléchargements…) et son emplacement actuel.</summary>
public sealed record UserFolder(string Key, string Label, string ValueName, string AliasValueName, string Path);

/// <summary>
/// Les dossiers personnels d'un utilisateur, lus comme le fait l'Explorateur dans « User Shell Folders ».
/// Le service tourne sous SYSTEM : %USERPROFILE% est remplacé par le profil de l'utilisateur concerné.
/// </summary>
public static class UserFolders
{
    internal const string UserShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    internal const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

    // Nom de la valeur historique, puis celui de l'alias récent que l'onglet « Emplacement » met aussi à jour.
    private static readonly (string Key, string Label, string ValueName, string Alias, string Leaf)[] Known =
    [
        ("documents", "Documents", "Personal", "{F42EE2D3-909F-4907-8871-4C22FC0BF756}", "Documents"),
        ("telechargements", "Téléchargements", "{374DE290-123F-4565-9164-39C4925E467B}", "{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}", "Downloads"),
        ("bureau", "Bureau", "Desktop", "{754AC886-DF64-4CBA-86B5-F7FBF4FBCEF5}", "Desktop"),
        ("images", "Images", "My Pictures", "{0DDD015D-B06C-45D5-8C4C-F59713854639}", "Pictures"),
        ("videos", "Vidéos", "My Video", "{35286A68-3C57-41A1-BBB1-0EAE73D76C95}", "Videos"),
        ("musique", "Musique", "My Music", "{A0C69A99-21C8-4671-8703-7934162FCF1D}", "Music"),
    ];

    public static IReadOnlyList<UserFolder> List(string sid, string profile)
    {
        RegistryKey? key = null;
        try
        {
            key = StartupInventory.IsUserSid(sid) ? Registry.Users.OpenSubKey($@"{sid}\{UserShellFoldersKey}") : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        using (key)
        {
            return Known
                .Select(k => new UserFolder(
                    k.Key, k.Label, k.ValueName, k.Alias,
                    Expand(key?.GetValue(k.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? System.IO.Path.Combine(profile, k.Leaf), profile)))
                .ToList();
        }
    }

    /// <summary>Les dossiers de l'utilisateur qui valide ; vide pour un compte qui n'est pas un utilisateur.</summary>
    public static IReadOnlyList<UserFolder> ForCaller(CallerIdentity caller) =>
        StartupInventory.IsUserSid(caller.UserSid) && StartupInventory.ProfilePath(caller.UserSid) is { } profile
            ? List(caller.UserSid, profile)
            : [];

    internal static string Expand(string raw, string profile)
    {
        var text = raw.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
        return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(text));
    }

    /// <summary>Un dossier synchronisé par OneDrive : le déplacer ou y supprimer touche aussi la copie en ligne.</summary>
    public static bool IsSyncedByOneDrive(string path) =>
        path.Split(System.IO.Path.DirectorySeparatorChar).Any(s => s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));

    /// <summary>Un dossier que SoluPulse peut déplacer ou alléger : sur le disque système et hors OneDrive.</summary>
    public static bool IsLocalSystemFolder(UserFolder folder) =>
        Directory.Exists(folder.Path) && Drives.IsOnSystemDrive(folder.Path) && !IsSyncedByOneDrive(folder.Path);
}

/// <summary>
/// Déplace un dossier personnel vers un autre disque, comme l'onglet « Emplacement » de l'Explorateur :
/// les fichiers sont déplacés un par un, puis Windows est informé du nouvel emplacement. « Annuler »
/// ramène les fichiers et l'ancien emplacement.
/// </summary>
public sealed class MoveUserFolderAction : SystemActionBase, IActionParameterChoices, IApplicableAction
{
    public const string FolderParameter = "dossier";
    public const string DestinationParameter = "destination";

    // Place gardée libre sur le disque de destination après le déplacement.
    private const long Margin = 2L << 30;

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.MoveUserFolder, Family.Space,
        "Déplacer un dossier personnel vers un autre disque",
        "Déplace un dossier comme Documents ou Téléchargements vers un autre disque du PC, avec la méthode de Windows (onglet « Emplacement »). Les fichiers ne sont pas modifiés et le dossier reste au même endroit dans l'Explorateur et dans vos programmes.",
        [
            "Vérifier la place disponible sur le disque choisi",
            "Déplacer les fichiers un par un, sans les modifier",
            "Indiquer à Windows le nouvel emplacement du dossier",
            "Fermer la session ou redémarrer pour que tous les programmes l'utilisent",
        ],
        RiskLevel.Moyen, Reversible: true, RequiresReboot: true, TimeSpan.FromMinutes(30),
        ParameterNames: [FolderParameter, DestinationParameter], TouchesSystem: false);

    public bool IsApplicable() => Drives.OtherFixedDrives(Margin * 2).Count > 0;

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<ParameterChoice>>(() => parameter switch
    {
        FolderParameter => UserFolders.ForCaller(caller)
            .Where(UserFolders.IsLocalSystemFolder)
            .Select(f => (Folder: f, Bytes: Files.SizeOf(f.Path)))
            .Where(f => f.Bytes >= 1L << 20)
            .OrderByDescending(f => f.Bytes)
            .Select(f => new ParameterChoice(f.Folder.Key, f.Folder.Label, $"{Files.Size(f.Bytes)} · {f.Folder.Path}"))
            .ToList(),
        DestinationParameter => Drives.OtherFixedDrives(Margin * 2)
            .Select(d => new ParameterChoice(d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')} {d.VolumeLabel}".Trim(), $"{Files.Size(d.AvailableFreeSpace)} libres"))
            .ToList(),
        _ => [],
    }, cancellationToken);

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        Plan(context);
    }

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var plan = Plan(context);
        var (count, bytes) = Files.Measure([plan.Folder.Path]);
        var free = Drives.FreeBytes(plan.DestinationRoot);
        if (bytes + Margin > free)
        {
            throw new ActionParameterException($"Pas assez de place sur {plan.DestinationRoot} : le dossier fait {Files.Size(bytes)} et il reste {Files.Size(free)}.");
        }

        return new ActionEstimate($"Environ {Files.Size(bytes)} libérés sur {Drives.SystemRoot.TrimEnd('\\')} ({count} fichiers). Nouvel emplacement : {plan.Target}.");
    }, cancellationToken);

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var plan = Plan(context);
        var sid = context.Caller.UserSid;
        using var userShell = Registry.Users.OpenSubKey($@"{sid}\{UserFolders.UserShellFoldersKey}");
        using var shell = Registry.Users.OpenSubKey($@"{sid}\{UserFolders.ShellFoldersKey}");
        var backup = new Backup(
            sid, plan.Folder.Key, plan.Folder.ValueName, plan.Folder.AliasValueName,
            userShell?.GetValue(plan.Folder.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            userShell?.GetValue(plan.Folder.AliasValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            shell?.GetValue(plan.Folder.ValueName) as string,
            plan.Folder.Path, plan.Target);
        return Task.FromResult<string?>(JsonSerializer.Serialize(backup));
    }

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var backup = Read(context.Backup ?? throw new InvalidOperationException("Sauvegarde manquante."));
        if (Directory.Exists(backup.NewPath) && Directory.EnumerateFileSystemEntries(backup.NewPath).Any())
        {
            return ActionOutcome.Failed($"Le dossier {backup.NewPath} existe déjà et n'est pas vide : rien n'a été déplacé.");
        }

        var (count, bytes) = Files.Measure([backup.OldPath]);
        if (bytes + Margin > Drives.FreeBytes(Path.GetPathRoot(backup.NewPath)!))
        {
            return ActionOutcome.Failed("Le disque de destination n'a plus assez de place : rien n'a été déplacé.");
        }

        Directory.CreateDirectory(backup.NewPath);
        GrantOwner(backup.NewPath, backup.Sid);
        var (moved, movedBytes, skipped) = Files.MoveTree(backup.OldPath, backup.NewPath);
        if (moved == 0 && count > 0)
        {
            return ActionOutcome.Failed("Aucun fichier n'a pu être déplacé : ils sont peut-être ouverts dans un programme.");
        }

        WriteLocation(backup.Sid, backup.ValueName, backup.AliasValueName, backup.NewPath, aliasExisted: backup.OldAlias is not null);
        var message = $"{moved} fichiers déplacés vers {backup.NewPath} : {Files.Size(movedBytes)} libérés sur {Drives.SystemRoot.TrimEnd('\\')}.";
        if (skipped > 0)
        {
            message += $" {skipped} fichiers ouverts dans un programme sont restés dans {backup.OldPath} : fermez ce programme puis déplacez-les à la main.";
        }

        return ActionOutcome.Ok(message + " Fermez votre session ou redémarrez pour que tous les programmes utilisent le nouvel emplacement.");
    }, cancellationToken);

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var saved = Read(backup);
        var bytes = Directory.Exists(saved.NewPath) ? Files.SizeOf(saved.NewPath) : 0;
        if (bytes > Drives.FreeBytes(Path.GetPathRoot(saved.OldPath)!))
        {
            return ActionOutcome.Failed($"Pas assez de place sur {Path.GetPathRoot(saved.OldPath)} pour y remettre les {Files.Size(bytes)} du dossier : libérez de la place puis réessayez.");
        }

        Directory.CreateDirectory(saved.OldPath);
        var (moved, _, skipped) = Files.MoveTree(saved.NewPath, saved.OldPath);
        RestoreLocation(saved);
        if (Directory.Exists(saved.NewPath) && !Directory.EnumerateFileSystemEntries(saved.NewPath).Any())
        {
            Directory.Delete(saved.NewPath);
        }

        return skipped == 0
            ? ActionOutcome.Ok($"Le dossier est revenu à {saved.OldPath} ({moved} fichiers).")
            : ActionOutcome.Ok($"Le dossier est revenu à {saved.OldPath}. {skipped} fichiers ouverts dans un programme sont restés dans {saved.NewPath}.");
    }, cancellationToken);

    private static (UserFolder Folder, string DestinationRoot, string Target) Plan(ActionContext context)
    {
        if (StartupInventory.ProfilePath(context.Caller.UserSid) is not { } profile || !StartupInventory.IsUserSid(context.Caller.UserSid))
        {
            throw new ActionParameterException("Utilisateur inconnu.");
        }

        var key = context.Get(FolderParameter);
        var folder = UserFolders.List(context.Caller.UserSid, profile).FirstOrDefault(f => f.Key == key)
            ?? throw new ActionParameterException("Ce dossier n'existe pas.");
        if (UserFolders.IsSyncedByOneDrive(folder.Path))
        {
            throw new ActionParameterException($"Le dossier {folder.Label} est synchronisé par OneDrive : déplacez-le depuis les paramètres de OneDrive.");
        }

        if (!Directory.Exists(folder.Path) || !Drives.IsOnSystemDrive(folder.Path))
        {
            throw new ActionParameterException($"Le dossier {folder.Label} n'est plus sur le disque système.");
        }

        var root = context.Get(DestinationParameter);
        var destination = Drives.OtherFixedDrives(0).FirstOrDefault(d => string.Equals(d.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase))
            ?? throw new ActionParameterException("Ce disque n'est pas un autre disque fixe du PC au format NTFS.");

        // E:\<nom du profil>\Documents : un dossier par utilisateur, même nom qu'à l'origine.
        var target = Path.Combine(destination.RootDirectory.FullName, Path.GetFileName(profile.TrimEnd('\\')), Path.GetFileName(folder.Path.TrimEnd('\\')));
        return (folder, destination.RootDirectory.FullName, target);
    }

    /// <summary>Le dossier créé par le service appartient à l'utilisateur, avec les mêmes droits que son profil.</summary>
    private static void GrantOwner(string path, string sid)
    {
        var user = new SecurityIdentifier(sid);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var directory = new DirectoryInfo(path);
        directory.SetAccessControl(security);
        try
        {
            var owner = new DirectorySecurity();
            owner.SetOwner(user);
            directory.SetAccessControl(owner);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PrivilegeNotHeldException)
        {
            // Les droits suffisent : le propriétaire reste le compte du service.
        }
    }

    private static void WriteLocation(string sid, string valueName, string aliasValueName, string path, bool aliasExisted)
    {
        using (var userShell = Registry.Users.CreateSubKey($@"{sid}\{UserFolders.UserShellFoldersKey}", writable: true))
        {
            userShell.SetValue(valueName, path, RegistryValueKind.ExpandString);
            if (aliasExisted)
            {
                userShell.SetValue(aliasValueName, path, RegistryValueKind.ExpandString);
            }
        }

        using var shell = Registry.Users.CreateSubKey($@"{sid}\{UserFolders.ShellFoldersKey}", writable: true);
        shell.SetValue(valueName, path, RegistryValueKind.String);
    }

    private static void RestoreLocation(Backup saved)
    {
        using (var userShell = Registry.Users.CreateSubKey($@"{saved.Sid}\{UserFolders.UserShellFoldersKey}", writable: true))
        {
            Restore(userShell, saved.ValueName, saved.OldUserShell, RegistryValueKind.ExpandString);
            Restore(userShell, saved.AliasValueName, saved.OldAlias, RegistryValueKind.ExpandString);
        }

        using var shell = Registry.Users.CreateSubKey($@"{saved.Sid}\{UserFolders.ShellFoldersKey}", writable: true);
        Restore(shell, saved.ValueName, saved.OldShell, RegistryValueKind.String);
    }

    private static void Restore(RegistryKey key, string name, string? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, kind);
        }
    }

    private static Backup Read(string json) => JsonSerializer.Deserialize<Backup>(json) ?? throw new InvalidDataException("Sauvegarde illisible.");

    private sealed record Backup(
        string Sid, string Key, string ValueName, string AliasValueName,
        string? OldUserShell, string? OldAlias, string? OldShell,
        string OldPath, string NewPath);
}

/// <summary>
/// Les plus gros fichiers des dossiers personnels sur le disque système. L'utilisateur coche ceux
/// qu'il veut supprimer ; seuls des fichiers de la liste peuvent l'être, jamais un chemin envoyé par l'application.
/// </summary>
public sealed class DeleteLargeFilesAction : SystemActionBase, IActionParameterChoices
{
    public const string FilesParameter = "fichiers";
    private const long MinimumSize = 100L << 20;
    private const int MaxChoices = 60;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DeleteLargeFiles, Family.Space,
        "Supprimer de gros fichiers dont vous n'avez plus besoin",
        "Liste les fichiers de plus de 100 Mo de vos dossiers personnels (Téléchargements, Bureau, Documents, Vidéos…) sur le disque système. Cochez ceux dont vous n'avez plus besoin : ils sont supprimés définitivement, sans passer par la corbeille. Les dossiers synchronisés par OneDrive ne sont pas parcourus.",
        ["Lister vos fichiers de plus de 100 Mo, du plus gros au plus petit", "Supprimer définitivement les fichiers cochés, et seulement eux"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(2),
        ParameterNames: [FilesParameter], TouchesSystem: false, MultiChoiceParameters: [FilesParameter]);

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<ParameterChoice>>(() =>
        parameter != FilesParameter
            ? []
            : Candidates(caller)
                .Take(MaxChoices)
                .Select(c => new ParameterChoice(c.Id, c.File.Name, $"{Files.Size(c.File.Length)} · {c.Folder} · modifié le {c.File.LastWriteTime.ToString("d MMMM yyyy", French)}"))
                .ToList(), cancellationToken);

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        Selected(context);
    }

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var files = Selected(context);
        return new ActionEstimate($"{files.Count} fichier{(files.Count > 1 ? "s" : "")} supprimé{(files.Count > 1 ? "s" : "")} définitivement : {Files.Size(files.Sum(f => f.Length))} libérés.");
    }, cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        int deleted = 0, skipped = 0;
        long bytes = 0;
        foreach (var file in Selected(context))
        {
            var length = file.Length;
            if (Files.TryDelete(file.FullName))
            {
                deleted++;
                bytes += length;
            }
            else
            {
                skipped++;
            }
        }

        return deleted == 0
            ? ActionOutcome.Failed("Aucun fichier n'a pu être supprimé : ils sont peut-être ouverts dans un programme.")
            : ActionOutcome.Ok($"{deleted} fichiers supprimés, {Files.Size(bytes)} libérés." + (skipped > 0 ? $" {skipped} fichiers ouverts dans un programme n'ont pas été supprimés." : ""));
    }, cancellationToken);

    private static List<FileInfo> Selected(ActionContext context)
    {
        var ids = context.GetAll(FilesParameter);
        var candidates = Candidates(context.Caller).ToDictionary(c => c.Id, c => c.File, StringComparer.Ordinal);
        var missing = ids.Count(id => !candidates.ContainsKey(id));
        if (missing > 0)
        {
            throw new ActionParameterException($"{missing} fichier(s) coché(s) ne sont plus dans la liste : rouvrez la fiche.");
        }

        return ids.Select(id => candidates[id]).ToList();
    }

    private static IEnumerable<(string Id, FileInfo File, string Folder)> Candidates(CallerIdentity caller)
    {
        if (!StartupInventory.IsUserSid(caller.UserSid))
        {
            throw new ActionParameterException("Utilisateur inconnu.");
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(string, FileInfo, string)>();
        foreach (var folder in UserFolders.ForCaller(caller).Where(UserFolders.IsLocalSystemFolder))
        {
            foreach (var file in new DirectoryInfo(folder.Path).EnumerateFiles("*", options).Where(f => f.Length >= MinimumSize))
            {
                if (seen.Add(file.FullName))
                {
                    found.Add((Files.Id(file.FullName), file, folder.Label));
                }
            }
        }

        return found.OrderByDescending(f => f.Item2.Length);
    }
}

/// <summary>
/// Caches des outils de développement de l'utilisateur qui valide : ils se reconstituent tout seuls,
/// au prix d'un nouveau téléchargement. Les paquets NuGet globaux (.nuget\packages) sont gardés : ils
/// servent à compiler même sans réseau.
/// </summary>
public sealed class CleanDeveloperCachesAction : SystemActionBase, IApplicableAction
{
    internal static readonly (string Tool, string RelativePath)[] Caches =
    [
        ("pip", @"AppData\Local\pip\cache"),
        ("uv", @"AppData\Local\uv\cache"),
        ("npm", @"AppData\Local\npm-cache"),
        ("Yarn", @"AppData\Local\Yarn\Cache"),
        ("node-gyp", @"AppData\Local\node-gyp\Cache"),
        ("Electron", @"AppData\Local\electron\Cache"),
        ("electron-builder", @"AppData\Local\electron-builder\Cache"),
        ("NuGet", @"AppData\Local\NuGet\v3-cache"),
        ("NuGet", @"AppData\Local\NuGet\http-cache"),
        ("NuGet", @"AppData\Local\NuGet\plugins-cache"),
        ("Go", @"AppData\Local\go-build"),
        ("Composer", @"AppData\Local\Composer"),
        ("Gradle", @".gradle\caches"),
        ("Cargo", @".cargo\registry\cache"),
    ];

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanDeveloperCaches, Family.Space,
        "Vider les caches des outils de développement",
        "Supprime les fichiers téléchargés et gardés en réserve par pip, npm, Yarn, NuGet, Gradle et d'autres outils. Ils seront retéléchargés à la prochaine installation de paquets. Vos projets ne sont pas touchés.",
        ["Repérer les caches des outils installés dans votre profil", "Supprimer leur contenu"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(3));

    public bool IsApplicable() => StartupInventory.UserProfiles().Any(p => Caches.Any(c => Directory.Exists(Path.Combine(p.Path, c.RelativePath))));

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var sizes = Folders(context.Caller)
            .GroupBy(f => f.Tool)
            .Select(g => (Tool: g.Key, Bytes: g.Sum(f => Files.SizeOf(f.Path))))
            .Where(t => t.Bytes > 0)
            .OrderByDescending(t => t.Bytes)
            .ToList();
        return sizes.Count == 0
            ? new ActionEstimate("Les caches sont déjà vides.")
            : new ActionEstimate($"Environ {Files.Size(sizes.Sum(s => s.Bytes))} libérés : {string.Join(", ", sizes.Select(s => $"{s.Tool} {Files.Size(s.Bytes)}"))}.");
    }, cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes, skipped) = Files.Delete(Folders(context.Caller).Select(f => f.Path));
        return ActionOutcome.Ok($"{count} fichiers supprimés, {Files.Size(bytes)} libérés." + (skipped > 0 ? $" {skipped} fichiers utilisés par un outil en cours ont été ignorés." : ""));
    }, cancellationToken);

    private static List<(string Tool, string Path)> Folders(CallerIdentity caller)
    {
        if (!StartupInventory.IsUserSid(caller.UserSid) || StartupInventory.ProfilePath(caller.UserSid) is not { } profile)
        {
            throw new ActionParameterException("Utilisateur inconnu.");
        }

        return Caches.Select(c => (c.Tool, Path: Path.Combine(profile, c.RelativePath))).Where(c => Directory.Exists(c.Path)).ToList();
    }
}

/// <summary>
/// Les applications Electron (Notion, Canva, draw.io…) gardent leurs installeurs de mise à jour dans
/// AppData\Local\&lt;application&gt;-updater. Ils sont retéléchargés si besoin : action sans risque, automatique.
/// </summary>
public sealed class CleanUpdateLeftoversAction : SystemActionBase, IApplicableAction
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanUpdateLeftovers, Family.Space,
        "Supprimer les installeurs de mises à jour déjà téléchargés",
        "Des applications comme Notion, Canva ou draw.io gardent une copie de leurs installeurs de mise à jour, parfois plusieurs versions. Elles les retéléchargent si besoin ; les applications elles-mêmes ne sont pas touchées.",
        ["Repérer les dossiers « …-updater » des profils utilisateurs", "Supprimer les installeurs qu'ils contiennent"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(1), Automatic: true);

    public bool IsApplicable() => Folders().Any();

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var sizes = Folders()
            .Select(f => (Name: Path.GetFileName(f)[..^"-updater".Length], Bytes: Files.SizeOf(f)))
            .Where(f => f.Bytes > 0)
            .OrderByDescending(f => f.Bytes)
            .ToList();
        return new ActionEstimate(sizes.Count == 0
            ? "Aucun installeur à supprimer."
            : $"Environ {Files.Size(sizes.Sum(s => s.Bytes))} libérés : {string.Join(", ", sizes.Take(5).Select(s => $"{s.Name} {Files.Size(s.Bytes)}"))}.");
    }, cancellationToken);

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (count, bytes, skipped) = Files.Delete(Folders());
        return ActionOutcome.Ok($"{count} installeurs supprimés, {Files.Size(bytes)} libérés." + (skipped > 0 ? $" {skipped} fichiers en cours d'utilisation ignorés." : ""));
    }, cancellationToken);

    internal static IEnumerable<string> Folders() => StartupInventory.UserProfiles()
        .Select(p => Path.Combine(p.Path, "AppData", "Local"))
        .Where(Directory.Exists)
        .SelectMany(local =>
        {
            try
            {
                return Directory.EnumerateDirectories(local, "*-updater").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        });
}

/// <summary>Nettoyage du magasin de composants de Windows (WinSxS) : anciennes versions remplacées par les mises à jour.</summary>
public sealed class ComponentStoreCleanupAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.CleanComponentStore, Family.Space,
        "Supprimer les anciennes versions laissées par Windows Update",
        "Windows garde les anciennes versions de ses composants après chaque mise à jour. Ce nettoyage les supprime ; les mises à jour déjà installées ne pourront plus être désinstallées. Le PC reste utilisable pendant l'opération.",
        ["Créer un point de restauration", "Lancer « DISM /Online /Cleanup-Image /StartComponentCleanup »", "Mesurer l'espace libéré"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(20));

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ActionEstimate("Gain variable, souvent de 1 à 5 Go sur un PC qui a reçu beaucoup de mises à jour."));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var before = Drives.FreeBytes(Drives.SystemRoot);
        var result = await SystemTools.RunAsync("dism.exe", ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/NoRestart"], TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);
        var freed = Math.Max(0, Drives.FreeBytes(Drives.SystemRoot) - before);
        return result.Succeeded
            ? ActionOutcome.Ok($"Anciennes versions supprimées : {Files.Size(freed)} libérés.")
            : ActionOutcome.Failed($"Le nettoyage a échoué (code {result.ExitCode}). Une mise à jour est peut-être en cours d'installation : réessayez après redémarrage.");
    }
}

/// <summary>Veille prolongée désactivée : hiberfil.sys est supprimé par Windows. « Annuler » la réactive.</summary>
public sealed class DisableHibernationAction : SystemActionBase, IApplicableAction
{
    internal static string HiberFile => Path.Combine(Drives.SystemRoot, "hiberfil.sys");

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DisableHibernation, Family.Space,
        "Désactiver la veille prolongée",
        "Supprime le fichier de veille prolongée (hiberfil.sys), dont la taille dépend de la mémoire du PC. Le démarrage rapide est désactivé avec lui. Sur un portable, quand la batterie est presque vide, le PC s'éteint au lieu de se mettre en veille prolongée : pensez à enregistrer votre travail.",
        ["Désactiver la veille prolongée avec « powercfg /hibernate off »", "Windows supprime le fichier hiberfil.sys"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromMinutes(1));

    public bool IsApplicable() => File.Exists(HiberFile);

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(HiberFile)
            ? new ActionEstimate($"Environ {Files.Size(new FileInfo(HiberFile).Length)} libérés.")
            : new ActionEstimate("La veille prolongée est déjà désactivée."));

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(File.Exists(HiberFile) ? "on" : "off");

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var size = File.Exists(HiberFile) ? new FileInfo(HiberFile).Length : 0;
        var result = await SystemTools.RunAsync("powercfg.exe", ["/hibernate", "off"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok($"Veille prolongée désactivée : {Files.Size(size)} libérés.")
            : ActionOutcome.Failed($"Windows a refusé de désactiver la veille prolongée (code {result.ExitCode}).");
    }

    public override async Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        if (backup != "on")
        {
            return ActionOutcome.Ok("La veille prolongée était déjà désactivée.");
        }

        var result = await SystemTools.RunAsync("powercfg.exe", ["/hibernate", "on"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok("Veille prolongée réactivée.")
            : ActionOutcome.Failed($"Windows a refusé de réactiver la veille prolongée (code {result.ExitCode}).");
    }
}

/// <summary>
/// Démarrage rapide désactivé (valeur HiberbootEnabled) : « Arrêter » relance alors complètement
/// Windows. La veille prolongée reste disponible. « Annuler » remet la valeur d'origine.
/// </summary>
public sealed class DisableFastStartupAction : SystemActionBase, IApplicableAction
{
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string ValueName = "HiberbootEnabled";

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DisableFastStartup, Family.Cleanup,
        "Désactiver le démarrage rapide",
        "Avec le démarrage rapide, le bouton « Arrêter » ne relance pas complètement Windows : la mémoire n'est pas remise à zéro et certaines mises à jour attendent. Sans lui, chaque arrêt devient un vrai redémarrage ; l'allumage prend quelques secondes de plus.",
        ["Mémoriser le réglage actuel", "Désactiver le démarrage rapide", "Le prochain arrêt ou redémarrage sera complet"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: true, TimeSpan.FromSeconds(5));

    public bool IsApplicable() => Current() == 1 && File.Exists(DisableHibernationAction.HiberFile);

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Current()?.ToString(CultureInfo.InvariantCulture) ?? "absent");

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.CreateSubKey(PowerKey, writable: true);
        key.SetValue(ValueName, 0, RegistryValueKind.DWord);
        return Task.FromResult(ActionOutcome.Ok("Démarrage rapide désactivé : le prochain arrêt sera un vrai redémarrage de Windows."));
    }

    public override Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.CreateSubKey(PowerKey, writable: true);
        if (int.TryParse(backup, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            key.SetValue(ValueName, value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        return Task.FromResult(ActionOutcome.Ok("Démarrage rapide remis comme avant."));
    }

    private static int? Current()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PowerKey);
        return key?.GetValue(ValueName) is int value ? value : null;
    }
}

/// <summary>Le disque virtuel de Docker Desktop ne se réduit qu'avec Docker lui-même : la fiche guide l'utilisateur.</summary>
public sealed class DockerDiskGuide : SystemActionBase, IApplicableAction
{
    private const long MinimumSize = 1L << 30;

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.GuideDocker, Family.Space,
        "Réduire ou déplacer le disque de Docker",
        "Docker Desktop range ses images, conteneurs et volumes dans un disque virtuel sur C:. Ce fichier grossit avec le temps et ne rétrécit pas tout seul. Seul Docker peut le réduire ou le déplacer sans risque.",
        [
            "Ouvrez Docker Desktop et supprimez ce dont vous n'avez plus besoin dans « Containers », « Images » et « Volumes » (ou tapez « docker system prune -a » dans un terminal).",
            "Pour libérer C:, déplacez le disque : Docker Desktop > Settings > Resources > Advanced > « Disk image location ». Choisissez un dossier sur un autre disque, par exemple E:\\Docker, puis cliquez sur « Apply & restart ».",
            "Attendez la fin du déplacement : Docker redémarre tout seul et l'espace est libéré sur C:.",
        ],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(15), ActionKind.Guide);

    public bool IsApplicable() => Disks().Sum(d => d.Length) >= MinimumSize;

    public override Task<ActionEstimate> EstimateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var bytes = Disks().Sum(d => d.Length);
        return Task.FromResult(new ActionEstimate(bytes > 0 ? $"Le disque virtuel de Docker occupe {Files.Size(bytes)} sur C:." : "Docker n'a pas de disque virtuel sur C:."));
    }

    public override Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ActionOutcome.Ok("Recommandation consultée."));

    internal static IEnumerable<FileInfo> Disks() => StartupInventory.UserProfiles()
        .Select(p => Path.Combine(p.Path, @"AppData\Local\Docker\wsl"))
        .Where(Directory.Exists)
        .SelectMany(folder => new DirectoryInfo(folder).EnumerateFiles("*.vhdx", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
        .Where(f => Drives.IsOnSystemDrive(f.FullName));
}
