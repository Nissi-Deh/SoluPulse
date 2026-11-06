using System.Management;
using System.Text.Json;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>Rallume le pare-feu Windows sur tous les profils ; « Annuler » recoupe ceux qui étaient coupés.</summary>
public sealed class EnableFirewallAction : SystemActionBase
{
    private static readonly Dictionary<string, string> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Domain"] = "domainprofile",
        ["Private"] = "privateprofile",
        ["Public"] = "publicprofile",
    };

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.EnableFirewall, Family.Security,
        "Réactiver le pare-feu Windows",
        "Rallume le pare-feu sur les réseaux où il est coupé (domaine, privé, public). L'état précédent est mémorisé et peut être rétabli.",
        ["Mémoriser l'état du pare-feu pour chaque type de réseau", "Activer le pare-feu sur tous les types de réseau"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromSeconds(10));

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken) => Task.Run<string?>(() =>
    {
        var disabled = Wmi.Query(@"\\.\root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile")
            .Where(p => Wmi.Get<ushort>(p, "Enabled") != 1)
            .Select(p => Wmi.Get<string>(p, "Name"))
            .OfType<string>()
            .Where(Profiles.ContainsKey)
            .ToArray();
        return JsonSerializer.Serialize(disabled);
    }, cancellationToken);

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var result = await SystemTools.RunAsync("netsh.exe", ["advfirewall", "set", "allprofiles", "state", "on"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? ActionOutcome.Ok("Le pare-feu Windows est activé sur tous les types de réseau.")
            : ActionOutcome.Failed($"Windows a refusé d'activer le pare-feu (code {result.ExitCode}). Il est peut-être géré par votre entreprise.");
    }

    public override async Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        var disabled = JsonSerializer.Deserialize<string[]>(backup) ?? [];
        if (disabled.Any(p => !Profiles.ContainsKey(p)))
        {
            return ActionOutcome.Failed("Sauvegarde du pare-feu illisible.");
        }

        foreach (var profile in disabled)
        {
            var result = await SystemTools.RunAsync("netsh.exe", ["advfirewall", "set", Profiles[profile], "state", "off"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ActionOutcome.Failed($"Impossible de rétablir l'état du profil « {profile} ».");
            }
        }

        return ActionOutcome.Ok("État précédent du pare-feu rétabli.");
    }
}

/// <summary>Appelle une méthode de Microsoft Defender par WMI, sans lancer d'exécutable (§6.1).</summary>
internal static class Defender
{
    private const string Scope = @"\\.\root\Microsoft\Windows\Defender";

    public static Task<uint> InvokeAsync(string wmiClass, string method, IReadOnlyDictionary<string, object>? parameters, TimeSpan timeout, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var management = new ManagementClass(new ManagementScope(Scope), new ManagementPath(wmiClass), null);
        using var input = parameters is null ? null : management.GetMethodParameters(method);
        foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
        {
            input![name] = value;
        }

        using var output = management.InvokeMethod(method, input, new InvokeMethodOptions(null, timeout));
        return output?["ReturnValue"] is { } code ? Convert.ToUInt32(code, System.Globalization.CultureInfo.InvariantCulture) : 0u;
    }, cancellationToken);
}

public sealed class UpdateDefenderSignaturesAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.UpdateDefenderSignatures, Family.Security,
        "Mettre à jour l'antivirus",
        "Télécharge les dernières définitions de virus de Microsoft Defender.",
        ["Demander à Microsoft Defender de télécharger ses dernières définitions"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(3));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var code = await Defender.InvokeAsync("MSFT_MpSignature", "Update", null, TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
        return code == 0
            ? ActionOutcome.Ok("Les définitions de virus sont à jour.")
            : ActionOutcome.Failed($"La mise à jour de Defender a échoué (code 0x{code:X8}). Vérifiez la connexion à Internet.");
    }
}

public sealed class DefenderQuickScanAction : SystemActionBase
{
    private const byte QuickScan = 1;

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.DefenderQuickScan, Family.Security,
        "Lancer une analyse antivirus rapide",
        "Microsoft Defender vérifie les emplacements où se cachent habituellement les virus. Vous pouvez continuer à utiliser le PC pendant l'analyse.",
        ["Lancer l'analyse rapide de Microsoft Defender", "Attendre la fin de l'analyse"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromMinutes(10));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var code = await Defender.InvokeAsync("MSFT_MpScan", "Start", new Dictionary<string, object> { ["ScanType"] = QuickScan }, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
        return code == 0
            ? ActionOutcome.Ok("Analyse rapide terminée. Les menaces éventuelles sont affichées dans Sécurité Windows.")
            : ActionOutcome.Failed($"L'analyse n'a pas pu être lancée (code 0x{code:X8}).");
    }
}
