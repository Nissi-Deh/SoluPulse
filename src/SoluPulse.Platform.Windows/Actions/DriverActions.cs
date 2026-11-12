using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>
/// Relance la détection du matériel (pnputil /scan-devices), comme « Rechercher les modifications sur
/// le matériel » du Gestionnaire de périphériques : Windows réessaie d'installer les pilotes manquants.
/// </summary>
public sealed class RescanDevicesAction : SystemActionBase, IApplicableAction
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.RescanDevices, Family.Repair,
        "Relancer la détection du matériel",
        "Windows recherche à nouveau les périphériques branchés et réessaie d'installer leurs pilotes. Cela suffit souvent après avoir rebranché un appareil.",
        ["Rechercher les modifications du matériel", "Compter les périphériques encore en erreur"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(1),
        TouchesSystem: false);

    public bool IsApplicable() => DeviceProblems.InError().Count > 0;

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var before = DeviceProblems.InError().Count;
        var result = await SystemTools.RunAsync("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return ActionOutcome.Failed($"Windows n'a pas pu relancer la détection du matériel (code {result.ExitCode}).");
        }

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        var after = DeviceProblems.InError().Count;
        return after < before
            ? ActionOutcome.Ok($"Détection relancée : {before - after} périphérique(s) fonctionnent de nouveau, {after} encore en erreur.")
            : ActionOutcome.Ok($"Détection relancée. {after} périphérique(s) restent en erreur : installez leur pilote.");
    }
}

/// <summary>
/// Cherche dans Windows Update le pilote d'un périphérique en erreur, d'après ses identifiants matériels,
/// puis l'installe. Windows Update ne propose que des pilotes signés et validés par Microsoft.
/// </summary>
public sealed class InstallDriverAction : SystemActionBase, IActionParameterChoices, IApplicableAction
{
    public const string DeviceParameter = "peripherique";

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.InstallDriver, Family.Repair,
        "Installer le pilote proposé par Windows Update",
        "SoluPulse demande à Windows Update le pilote qui correspond exactement à ce périphérique, puis l'installe. Ces pilotes sont signés et validés par Microsoft. Nécessite Internet.",
        ["Chercher dans Windows Update un pilote pour les identifiants de ce périphérique", "Télécharger et installer le pilote", "Relancer la détection du matériel"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(10),
        ParameterNames: [DeviceParameter]);

    public bool IsApplicable() => DeviceProblems.InError().Count > 0;

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ParameterChoice>>(() => parameter != DeviceParameter
            ? []
            : DeviceProblems.InError().Select(d => new ParameterChoice(d.InstanceId, d.Name, d.Explanation)).ToList(), cancellationToken);

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        if (Find(context) is null)
        {
            throw new ActionParameterException("Ce périphérique n'est plus en erreur.");
        }
    }

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var device = Find(context) ?? throw new ActionParameterException("Ce périphérique n'est plus en erreur.");
        if (device.HardwareIds.Count == 0)
        {
            return ActionOutcome.Failed("Ce périphérique ne donne pas ses identifiants matériels : Windows Update ne peut pas lui trouver de pilote.");
        }

        var outcome = await Task.Run(() => DriverUpdates.Install(device.HardwareIds), cancellationToken).ConfigureAwait(false);
        if (!outcome.Success)
        {
            return outcome;
        }

        await SystemTools.RunAsync("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private static DeviceProblem? Find(ActionContext context)
    {
        var id = context.Get(DeviceParameter);
        return DeviceProblems.InError().FirstOrDefault(d => string.Equals(d.InstanceId, id, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Recherche et installation d'un pilote par l'agent Windows Update (objets COM explicites).</summary>
internal static class DriverUpdates
{
    public static ActionOutcome Install(IReadOnlyList<string> hardwareIds)
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Agent Windows Update absent.");
        var session = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Agent Windows Update absent.");
        var searcher = Com.Call(session, "CreateUpdateSearcher");
        searcher.GetType().InvokeMember("Online", System.Reflection.BindingFlags.SetProperty, null, searcher, [true], System.Globalization.CultureInfo.InvariantCulture);

        object found;
        try
        {
            found = Com.Call(searcher, "Search", "IsInstalled=0 and Type='Driver'");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is System.Runtime.InteropServices.COMException com)
        {
            return ActionOutcome.Failed($"Windows Update ne répond pas (code 0x{com.HResult:X8}). Vérifiez la connexion Internet.");
        }

        var updates = Com.Get(found, "Updates") ?? throw new InvalidOperationException("Windows Update n'a pas renvoyé de liste.");
        var count = Com.Int(Com.Get(updates, "Count"));
        object? match = null;
        string? title = null;
        for (var i = 0; i < count && match is null; i++)
        {
            var update = Com.Get(updates, "Item", i);
            if (update is not null && Matches(Com.Get(update, "DriverHardwareID") as string, hardwareIds))
            {
                match = update;
                title = Com.Get(update, "Title") as string;
            }
        }

        if (match is null)
        {
            return ActionOutcome.Failed("Windows Update ne propose pas de pilote pour ce périphérique. Suivez le guide pour le trouver sur le site du fabricant.");
        }

        var collectionType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl") ?? throw new InvalidOperationException("Agent Windows Update absent.");
        var collection = Activator.CreateInstance(collectionType) ?? throw new InvalidOperationException("Agent Windows Update absent.");
        if (Com.Get(match, "EulaAccepted") is false)
        {
            Com.Call(match, "AcceptEula");
        }

        Com.Call(collection, "Add", match);

        var downloader = Com.Call(session, "CreateUpdateDownloader");
        downloader.GetType().InvokeMember("Updates", System.Reflection.BindingFlags.SetProperty, null, downloader, [collection], System.Globalization.CultureInfo.InvariantCulture);
        var downloaded = Com.Call(downloader, "Download");
        if (Com.Int(Com.Get(downloaded, "ResultCode")) is not (2 or 3))
        {
            return ActionOutcome.Failed($"Le téléchargement du pilote « {title} » a échoué.");
        }

        var installer = Com.Call(session, "CreateUpdateInstaller");
        installer.GetType().InvokeMember("Updates", System.Reflection.BindingFlags.SetProperty, null, installer, [collection], System.Globalization.CultureInfo.InvariantCulture);
        var installed = Com.Call(installer, "Install");
        var code = Com.Int(Com.Get(installed, "ResultCode"));
        var reboot = Com.Get(installed, "RebootRequired") is true;
        return code is 2 or 3
            ? ActionOutcome.Ok($"Pilote installé : {title}.{(reboot ? " Redémarrez le PC pour finir." : "")}")
            : ActionOutcome.Failed($"L'installation du pilote « {title} » a échoué (code {code}).");
    }

    /// <summary>Le pilote vise l'un des identifiants du périphérique (le plus précis vient en premier).</summary>
    internal static bool Matches(string? driverHardwareId, IReadOnlyList<string> hardwareIds) =>
        !string.IsNullOrEmpty(driverHardwareId) && hardwareIds.Any(id => string.Equals(id, driverHardwareId, StringComparison.OrdinalIgnoreCase));
}
