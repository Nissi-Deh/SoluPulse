using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Actions;

/// <summary>Action réseau sans paramètre : un appel d'outil Windows avec des arguments fixes.</summary>
public abstract class FixedToolAction(string tool, string[] arguments, string success) : SystemActionBase
{
    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var result = await SystemTools.RunAsync(tool, arguments, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? ActionOutcome.Ok(success) : ActionOutcome.Failed($"L'opération a échoué (code {result.ExitCode}) : {result.Output}");
    }
}

public sealed class FlushDnsAction() : FixedToolAction("ipconfig.exe", ["/flushdns"], "Le cache DNS a été vidé.")
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.FlushDns, Family.Network,
        "Vider le cache DNS",
        "Efface la mémoire des adresses de sites. Windows les redemandera au prochain accès.",
        ["Lancer « ipconfig /flushdns »"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromSeconds(5));
}

public sealed class ResetWinsockAction() : FixedToolAction("netsh.exe", ["winsock", "reset"], "Winsock réinitialisé. Redémarrez le PC pour terminer.")
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.ResetWinsock, Family.Network,
        "Réinitialiser Winsock",
        "Remet à zéro la couche réseau de Windows, souvent abîmée par des logiciels VPN ou de sécurité désinstallés. Un redémarrage est nécessaire.",
        ["Créer un point de restauration", "Lancer « netsh winsock reset »", "Redémarrer le PC"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: true, TimeSpan.FromMinutes(1));
}

public sealed class ResetTcpIpAction() : FixedToolAction("netsh.exe", ["int", "ip", "reset"], "TCP/IP réinitialisé. Redémarrez le PC pour terminer.")
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.ResetTcpIp, Family.Network,
        "Réinitialiser TCP/IP",
        "Remet les réglages réseau de Windows à leur état d'origine. Une adresse IP fixe configurée à la main sera effacée. Un redémarrage est nécessaire.",
        ["Créer un point de restauration", "Lancer « netsh int ip reset »", "Redémarrer le PC"],
        RiskLevel.Moyen, Reversible: false, RequiresReboot: true, TimeSpan.FromMinutes(1));
}

/// <summary>Libère puis renouvelle l'adresse IP obtenue de la box.</summary>
public sealed class RenewIpAction : SystemActionBase
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.RenewIp, Family.Network,
        "Renouveler l'adresse IP",
        "Demande une nouvelle adresse à votre box. La connexion est coupée quelques secondes.",
        ["Lancer « ipconfig /release »", "Lancer « ipconfig /renew »"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromSeconds(30));

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await SystemTools.RunAsync("ipconfig.exe", ["/release"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        var renew = await SystemTools.RunAsync("ipconfig.exe", ["/renew"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return renew.Succeeded
            ? ActionOutcome.Ok("Nouvelle adresse IP obtenue.")
            : ActionOutcome.Failed("La box n'a pas répondu à la demande d'adresse. Vérifiez qu'elle est allumée.");
    }
}

/// <summary>Les cartes réseau proposées au choix : connectées et dotées d'une passerelle.</summary>
internal static class Adapters
{
    public const string Parameter = "carte";

    public static IReadOnlyList<ParameterChoice> Choices() => NetworkCollector.ActiveInterfaces()
        .Select(n => new ParameterChoice(n.Name, n.Name, n.Description))
        .ToList();

    public static NetworkInterface Find(ActionContext context)
    {
        var name = context.Get(Parameter);
        return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            ?? throw new ActionParameterException("Carte réseau introuvable.");
    }
}

public sealed class RestartAdapterAction : SystemActionBase, IActionParameterChoices
{
    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.RestartAdapter, Family.Network,
        "Redémarrer la carte réseau",
        "Désactive puis réactive la carte réseau choisie. La connexion est coupée quelques secondes.",
        ["Désactiver la carte réseau", "La réactiver"],
        RiskLevel.Faible, Reversible: false, RequiresReboot: false, TimeSpan.FromSeconds(30),
        ParameterNames: [Adapters.Parameter]);

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) =>
        Task.FromResult(Adapters.Choices());

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        Adapters.Find(context);
    }

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var adapter = Adapters.Find(context);
        await SystemTools.RunAsync("netsh.exe", ["interface", "set", "interface", adapter.Name, "disabled"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);

        // La réactivation est retentée et n'est jamais annulée : la carte ne doit pas rester coupée.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var enable = await SystemTools.RunAsync("netsh.exe", ["interface", "set", "interface", adapter.Name, "enabled"], TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            if (enable.Succeeded)
            {
                return ActionOutcome.Ok($"La carte « {adapter.Name} » a été redémarrée.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }

        return ActionOutcome.Failed($"La carte « {adapter.Name} » n'a pas pu être réactivée. Réactivez-la dans les paramètres réseau de Windows.");
    }
}

/// <summary>Change le serveur DNS d'une carte parmi une liste fermée de fournisseurs ; « Annuler » remet l'ancien réglage.</summary>
public sealed class SetDnsAction : SystemActionBase, IActionParameterChoices
{
    public const string ProviderParameter = "fournisseur";

    private static readonly Dictionary<string, (string Label, string[] Servers)> Providers = new(StringComparer.Ordinal)
    {
        ["automatique"] = ("Automatique (fourni par la box)", []),
        ["cloudflare"] = ("Cloudflare (1.1.1.1)", ["1.1.1.1", "1.0.0.1"]),
        ["google"] = ("Google (8.8.8.8)", ["8.8.8.8", "8.8.4.4"]),
        ["quad9"] = ("Quad9 (9.9.9.9)", ["9.9.9.9", "149.112.112.112"]),
    };

    public override ActionDescriptor Descriptor { get; } = new(
        ActionIds.SetDns, Family.Network,
        "Changer de serveur DNS",
        "Utilise un autre service pour trouver l'adresse des sites. L'ancien réglage est mémorisé et peut être rétabli.",
        ["Mémoriser les serveurs DNS actuels de la carte", "Appliquer les serveurs du fournisseur choisi"],
        RiskLevel.Faible, Reversible: true, RequiresReboot: false, TimeSpan.FromSeconds(10),
        ParameterNames: [Adapters.Parameter, ProviderParameter]);

    public Task<IReadOnlyList<ParameterChoice>> GetChoicesAsync(string parameter, CallerIdentity caller, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ParameterChoice>>(parameter == ProviderParameter
            ? Providers.Select(p => new ParameterChoice(p.Key, p.Value.Label)).ToList()
            : Adapters.Choices());

    public override async Task ValidateAsync(ActionContext context, CancellationToken cancellationToken)
    {
        await base.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        Adapters.Find(context);
        if (!Providers.ContainsKey(context.Get(ProviderParameter)))
        {
            throw new ActionParameterException("Fournisseur DNS inconnu.");
        }
    }

    public override Task<string?> BackupAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var adapter = Adapters.Find(context);
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapter.Id}");
        // NameServer vide : les serveurs DNS viennent de la box (DHCP).
        var servers = (key?.GetValue("NameServer") as string ?? "")
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Task.FromResult<string?>(JsonSerializer.Serialize(servers));
    }

    public override async Task<ActionOutcome> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var adapter = Adapters.Find(context);
        var (label, servers) = Providers[context.Get(ProviderParameter)];
        var applied = await ApplyAsync(adapter.Name, servers, cancellationToken).ConfigureAwait(false);
        return applied ? ActionOutcome.Ok($"DNS de « {adapter.Name} » : {label}.") : ActionOutcome.Failed("Windows a refusé le changement de DNS.");
    }

    public override async Task<ActionOutcome> UndoAsync(ActionContext context, string backup, CancellationToken cancellationToken)
    {
        var adapter = Adapters.Find(context);
        var servers = JsonSerializer.Deserialize<string[]>(backup) ?? [];
        if (servers.Any(s => !System.Net.IPAddress.TryParse(s, out _)))
        {
            return ActionOutcome.Failed("Sauvegarde DNS illisible.");
        }

        var restored = await ApplyAsync(adapter.Name, servers, cancellationToken).ConfigureAwait(false);
        return restored ? ActionOutcome.Ok("Réglage DNS précédent restauré.") : ActionOutcome.Failed("Impossible de restaurer le réglage DNS.");
    }

    private static async Task<bool> ApplyAsync(string adapter, string[] servers, CancellationToken cancellationToken)
    {
        if (servers.Length == 0)
        {
            return (await SystemTools.RunAsync("netsh.exe", ["interface", "ipv4", "set", "dnsservers", adapter, "dhcp"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false)).Succeeded;
        }

        var first = await SystemTools.RunAsync("netsh.exe", ["interface", "ipv4", "set", "dnsservers", adapter, "static", servers[0], "primary", "no"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        if (!first.Succeeded)
        {
            return false;
        }

        for (var i = 1; i < servers.Count; i++)
        {
            await SystemTools.RunAsync("netsh.exe", ["interface", "ipv4", "add", "dnsservers", adapter, servers[i], $"index={i + 1}", "no"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
