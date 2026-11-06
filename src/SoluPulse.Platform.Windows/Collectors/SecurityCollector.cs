using Microsoft.Win32;
using SoluPulse.Core.Collection;
using SoluPulse.Platform.Windows.Interop;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Protections de Windows : antivirus et signatures, pare-feu, démarrage sécurisé, contrôle de compte
/// d'utilisateur, chiffrement BitLocker et puce TPM. Seuls des états sont relevés, jamais de contenu.
/// </summary>
public sealed class SecurityCollector(TimeProvider timeProvider) : ICollector
{
    private const string SecurityCenterScope = @"\\.\root\SecurityCenter2";
    private const string DefenderScope = @"\\.\root\Microsoft\Windows\Defender";
    private const string FirewallScope = @"\\.\root\StandardCimv2";
    private const string BitLockerScope = @"\\.\root\cimv2\Security\MicrosoftVolumeEncryption";
    private const string TpmScope = @"\\.\root\cimv2\Security\MicrosoftTpm";

    // Âge renvoyé par Defender quand l'analyse n'a jamais eu lieu.
    private const uint NeverScanned = uint.MaxValue;

    public string Name => "securite";

    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var snapshot = new Dictionary<string, object?>();

        // Le Centre de sécurité connaît aussi les antivirus et pare-feu d'autres éditeurs.
        var antivirus = new List<SecurityProduct>();
        var firewalls = new List<SecurityProduct>();
        result.Try("centre_securite", () =>
        {
            antivirus.AddRange(Products("AntiVirusProduct"));
            firewalls.AddRange(Products("FirewallProduct"));
        });
        snapshot["Antivirus"] = antivirus;

        DefenderStatus? defender = null;
        result.Try("defender", () =>
        {
            var status = Wmi.QueryFirst(DefenderScope, "SELECT AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge, QuickScanAge, FullScanAge, IsTamperProtected FROM MSFT_MpComputerStatus");
            if (status is not null)
            {
                defender = new DefenderStatus(
                    Wmi.Get<bool>(status, "AMServiceEnabled") && Wmi.Get<bool>(status, "AntivirusEnabled"),
                    Wmi.Get<bool>(status, "RealTimeProtectionEnabled"),
                    Wmi.Get<uint>(status, "AntivirusSignatureAge"),
                    Wmi.Get<uint>(status, "QuickScanAge"),
                    Wmi.Get<uint>(status, "FullScanAge"),
                    Wmi.Get<bool>(status, "IsTamperProtected"));
            }
        });
        snapshot["Defender"] = defender;

        var defenderActive = defender is { Actif: true, TempsReel: true };
        if (antivirus.Count > 0 || defender is not null)
        {
            result.Add("securite.antivirus_actif", antivirus.Any(a => a.Actif) || defenderActive ? 1 : 0, "");
        }

        if (antivirus.Any(a => a.Actif))
        {
            result.Add("securite.antivirus_a_jour", antivirus.Any(a => a.Actif && a.AJour) ? 1 : 0, "");
        }

        // Les mesures propres à Defender n'ont de sens que s'il est l'antivirus en service.
        if (defender is { Actif: true } d)
        {
            result.Add("securite.defender.protection_temps_reel", d.TempsReel ? 1 : 0, "");
            result.Add("securite.defender.signatures_age_j", d.AgeSignaturesJours, "j");
            var lastScan = Math.Min(d.AgeAnalyseRapideJours, d.AgeAnalyseCompleteJours);
            if (lastScan != NeverScanned)
            {
                result.Add("securite.defender.dernier_scan_j", lastScan, "j");
            }
        }

        result.Try("pare_feu", () =>
        {
            var profiles = Wmi.Query(FirewallScope, "SELECT Name, Enabled FROM MSFT_NetFirewallProfile")
                .Select(p => new { Profil = Wmi.Get<string>(p, "Name"), Actif = Wmi.Get<ushort>(p, "Enabled") == 1 })
                .ToList();
            snapshot["PareFeu"] = new { Profils = profiles, AutresPareFeu = firewalls };

            // Un pare-feu d'un autre éditeur, actif, remplace celui de Windows.
            var disabled = firewalls.Any(f => f.Actif) ? 0 : profiles.Count(p => !p.Actif);
            result.Add("securite.pare_feu.profils_desactives", disabled, "");
        });

        result.Try("demarrage_securise", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int enabled)
            {
                result.Add("securite.demarrage_securise", enabled == 1 ? 1 : 0, "");
                snapshot["DemarrageSecurise"] = enabled == 1;
            }
            else
            {
                snapshot["DemarrageSecurise"] = "non pris en charge (BIOS ancien)";
            }
        });

        result.Try("uac", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var enabled = key?.GetValue("EnableLUA") is not int lua || lua != 0;
            result.Add("securite.uac_actif", enabled ? 1 : 0, "");
            snapshot["ControleCompteUtilisateur"] = enabled;
        });

        result.Try("bitlocker", () =>
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            var volume = Wmi.Query(BitLockerScope, "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume")
                .FirstOrDefault(v => string.Equals(Wmi.Get<string>(v, "DriveLetter"), systemDrive, StringComparison.OrdinalIgnoreCase));
            if (volume is not null)
            {
                // ProtectionStatus : 0 non protégé, 1 protégé, 2 inconnu.
                var status = Wmi.Get<uint>(volume, "ProtectionStatus");
                if (status <= 1)
                {
                    result.Add("securite.chiffrement_disque_systeme", status, "");
                }

                snapshot["Chiffrement"] = status switch { 1 => "BitLocker actif", 0 => "non chiffré", _ => "inconnu" };
            }
        });

        result.Try("tpm", () =>
        {
            var tpm = Wmi.QueryFirst(TpmScope, "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm");
            var ready = tpm is not null && Wmi.Get<bool>(tpm, "IsEnabled_InitialValue") && Wmi.Get<bool>(tpm, "IsActivated_InitialValue");
            result.Add("securite.tpm_pret", ready ? 1 : 0, "");
            snapshot["Tpm"] = tpm is null ? "absent" : new { Pret = ready, Version = Wmi.Get<string>(tpm, "SpecVersion")?.Split(',')[0].Trim() };
        });

        result.Snapshot("securite", snapshot);
        return Task.FromResult(result.Build());
    }

    private static IEnumerable<SecurityProduct> Products(string wmiClass) =>
        Wmi.Query(SecurityCenterScope, $"SELECT displayName, productState FROM {wmiClass}")
            .Select(p => SecurityProduct.From(Wmi.Get<string>(p, "displayName") ?? "?", Wmi.Get<uint>(p, "productState")));

    private sealed record DefenderStatus(bool Actif, bool TempsReel, uint AgeSignaturesJours, uint AgeAnalyseRapideJours, uint AgeAnalyseCompleteJours, bool ProtectionAntiFalsification);
}

/// <summary>Produit déclaré au Centre de sécurité Windows.</summary>
public sealed record SecurityProduct(string Nom, bool Actif, bool AJour)
{
    /// <summary>
    /// <c>productState</c> se lit en hexadécimal sur trois octets : celui du milieu vaut 0x10 ou 0x11
    /// quand le produit est actif, le dernier vaut 0x00 quand ses signatures sont à jour.
    /// </summary>
    public static SecurityProduct From(string name, uint productState) =>
        new(name, ((productState >> 8) & 0x10) != 0, (productState & 0x10) == 0);
}
