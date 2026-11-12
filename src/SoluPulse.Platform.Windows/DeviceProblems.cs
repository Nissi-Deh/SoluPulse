using System.Text.RegularExpressions;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows;

/// <summary>Un périphérique que Windows signale en erreur, avec ce que l'utilisateur peut en faire.</summary>
public sealed record DeviceProblem(
    string InstanceId,
    string Name,
    uint Code,
    string? DeviceClass,
    IReadOnlyList<string> HardwareIds,
    string? Vendor,
    string Explanation,
    string Advice);

/// <summary>
/// Périphériques en erreur (Win32_PnPEntity.ConfigManagerErrorCode) expliqués en français. Le
/// fabricant est déduit de l'identifiant matériel (VEN_8086 : Intel), pour savoir où chercher le pilote.
/// </summary>
public static partial class DeviceProblems
{
    /// <summary>Désactivé volontairement (22) ou débranché (45) : pas une panne.</summary>
    public static readonly IReadOnlySet<uint> Ignored = new HashSet<uint> { 22, 45 };

    private static readonly Dictionary<uint, (string Explanation, string Advice)> Codes = new()
    {
        [1] = ("Le périphérique n'est pas configuré correctement.", "Mettez à jour son pilote."),
        [3] = ("Le pilote est peut-être abîmé, ou Windows manque de mémoire.", "Redémarrez le PC, puis réinstallez le pilote si le problème revient."),
        [10] = ("Le périphérique ne peut pas démarrer.", "Installez le pilote le plus récent du fabricant."),
        [12] = ("Le périphérique n'a pas assez de ressources libres (conflit avec un autre).", "Débranchez les périphériques ajoutés récemment ; un technicien peut régler le conflit dans le BIOS."),
        [14] = ("Le périphérique ne fonctionnera qu'après un redémarrage.", "Redémarrez le PC."),
        [18] = ("Le pilote doit être réinstallé.", "Réinstallez le pilote du périphérique."),
        [19] = ("Les réglages du périphérique dans Windows sont abîmés.", "Désinstallez le périphérique dans le Gestionnaire de périphériques, puis relancez la détection du matériel."),
        [21] = ("Windows est en train de retirer ce périphérique.", "Redémarrez le PC."),
        [24] = ("Le périphérique est absent, ne répond pas, ou tous ses pilotes ne sont pas installés.", "Vérifiez son branchement, puis installez son pilote."),
        [28] = ("Aucun pilote n'est installé pour ce périphérique.", "Installez le pilote : Windows Update le propose souvent, sinon le site du fabricant."),
        [31] = ("Windows n'arrive pas à charger le pilote de ce périphérique.", "Installez le pilote le plus récent du fabricant."),
        [32] = ("Le pilote de ce périphérique est désactivé.", "Réinstallez le pilote, ou demandez à un technicien de réactiver le service du pilote."),
        [37] = ("Le pilote a renvoyé une erreur au démarrage.", "Réinstallez le pilote du périphérique."),
        [39] = ("Le pilote est absent ou abîmé.", "Réinstallez le pilote du périphérique."),
        [40] = ("Les informations du pilote dans Windows sont incorrectes.", "Désinstallez puis réinstallez le pilote."),
        [41] = ("Le pilote est chargé, mais Windows ne trouve pas le matériel.", "Vérifiez le branchement, puis réinstallez le pilote."),
        [43] = ("Le périphérique a signalé un problème et Windows l'a arrêté.", "Débranchez-le et rebranchez-le ; si cela continue, mettez à jour son pilote ou faites-le vérifier."),
        [47] = ("Le périphérique a été préparé pour être retiré.", "Débranchez-le et rebranchez-le."),
        [48] = ("Windows bloque ce pilote, connu pour poser problème.", "Installez une version plus récente du pilote, depuis le site du fabricant."),
        [49] = ("Le registre de Windows est trop chargé pour ajouter des périphériques.", "Désinstallez les périphériques que vous n'utilisez plus ; un technicien peut nettoyer le registre."),
        [52] = ("La signature numérique du pilote ne peut pas être vérifiée.", "Installez le pilote officiel signé, depuis Windows Update ou le site du fabricant."),
    };

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8086"] = "Intel", ["8087"] = "Intel", ["10DE"] = "NVIDIA", ["1002"] = "AMD", ["1022"] = "AMD", ["10EC"] = "Realtek",
        ["0BDA"] = "Realtek", ["14E4"] = "Broadcom", ["168C"] = "Qualcomm Atheros", ["17CB"] = "Qualcomm", ["0CF3"] = "Qualcomm Atheros",
        ["14C3"] = "MediaTek", ["0E8D"] = "MediaTek", ["1B21"] = "ASMedia", ["1217"] = "O2 Micro", ["1180"] = "Ricoh",
        ["046D"] = "Logitech", ["045E"] = "Microsoft", ["04F2"] = "Chicony", ["13D3"] = "AzureWave", ["0489"] = "Foxconn",
        ["1033"] = "NEC", ["104C"] = "Texas Instruments", ["1106"] = "VIA", ["11AB"] = "Marvell", ["1B4B"] = "Marvell",
        ["144D"] = "Samsung", ["15B7"] = "SanDisk / WD", ["1C5C"] = "SK hynix", ["1987"] = "Phison", ["126F"] = "Silicon Motion",
        ["06CB"] = "Synaptics", ["138A"] = "Synaptics", ["27C6"] = "Goodix", ["1C7A"] = "LighTuning", ["0A5C"] = "Broadcom",
        ["03F0"] = "HP", ["103C"] = "HP", ["17AA"] = "Lenovo", ["1028"] = "Dell", ["413C"] = "Dell", ["1043"] = "Asus", ["0B05"] = "Asus",
        ["1025"] = "Acer", ["1462"] = "MSI", ["04A9"] = "Canon", ["04B8"] = "Epson", ["04F9"] = "Brother", ["0781"] = "SanDisk",
        ["0951"] = "Kingston", ["05AC"] = "Apple", ["2109"] = "VIA Labs", ["05E3"] = "Genesys Logic", ["1A86"] = "WCH (adaptateur série)",
        ["067B"] = "Prolific (adaptateur série)", ["0403"] = "FTDI (adaptateur série)", ["10C4"] = "Silicon Labs (adaptateur série)",
    };

    public static IReadOnlyList<DeviceProblem> InError()
    {
        return Wmi.Query(@"\\.\root\cimv2", "SELECT Name, DeviceID, ConfigManagerErrorCode, PNPClass, HardwareID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
            .Select(d => Describe(
                Wmi.Get<string>(d, "DeviceID") ?? "",
                Wmi.Get<string>(d, "Name"),
                Wmi.Get<uint>(d, "ConfigManagerErrorCode"),
                Wmi.Get<string>(d, "PNPClass"),
                d["HardwareID"] as string[] ?? []))
            .Where(d => !Ignored.Contains(d.Code) && d.InstanceId.Length > 0)
            .ToList();
    }

    public static DeviceProblem Describe(string instanceId, string? name, uint code, string? deviceClass, IReadOnlyList<string> hardwareIds)
    {
        var (explanation, advice) = Codes.TryGetValue(code, out var known)
            ? known
            : ($"Windows signale une erreur sur ce périphérique (code {code}).", "Mettez à jour ou réinstallez son pilote.");
        var vendor = VendorOf(hardwareIds.Count > 0 ? hardwareIds : [instanceId]);
        if (vendor is not null && (code is 1 or 10 or 18 or 24 or 28 or 31 or 37 or 39 or 48 or 52))
        {
            advice += $" Fabricant du composant : {vendor}.";
        }

        return new DeviceProblem(instanceId, string.IsNullOrWhiteSpace(name) ? "Périphérique inconnu" : name, code, deviceClass, hardwareIds, vendor, explanation, advice);
    }

    /// <summary>VEN_xxxx (PCI) ou VID_xxxx (USB) de l'identifiant matériel.</summary>
    public static string? VendorOf(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (VendorId().Match(id) is { Success: true } match && Vendors.TryGetValue(match.Groups[1].Value, out var vendor))
            {
                return vendor;
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?:VEN|VID)_([0-9A-Fa-f]{4})")]
    private static partial Regex VendorId();
}
