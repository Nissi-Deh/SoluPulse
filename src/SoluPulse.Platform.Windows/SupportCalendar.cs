using System.Text.RegularExpressions;

namespace SoluPulse.Platform.Windows;

/// <summary>
/// Dates de fin des correctifs de sécurité de Windows, d'après le calendrier publié par Microsoft.
/// Famille et Pro ont 24 mois par version de Windows 11 ; Entreprise et Éducation 36 mois. Une
/// version plus récente que celles de la table n'a pas de date : aucune alerte n'est levée.
/// </summary>
public static partial class SupportCalendar
{
    private static readonly HashSet<string> LongServicingEditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enterprise", "EnterpriseN", "EnterpriseG", "EnterpriseGN", "Education", "EducationN", "IoTEnterprise", "IoTEnterpriseK",
    };

    /// <summary>Windows 10 22H2 : fin le 14 octobre 2025, prolongée jusqu'au 12 octobre 2027 pour les PC inscrits au programme ESU.</summary>
    public static readonly DateOnly Windows10End = new(2025, 10, 14);
    public static readonly DateOnly Windows10ConsumerEsuEnd = new(2027, 10, 12);
    public static readonly DateOnly Windows10CommercialEsuEnd = new(2028, 10, 10);

    /// <summary>Date de fin des correctifs, ou <c>null</c> si la version n'est pas connue de la table.</summary>
    public static DateOnly? EndOfSupport(int build, string edition, bool esu)
    {
        var ltsc = edition.EndsWith('S') && edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase);
        if (ltsc)
        {
            var iot = edition.StartsWith("IoT", StringComparison.OrdinalIgnoreCase);
            return build switch
            {
                14393 => new DateOnly(2026, 10, 13),
                17763 => new DateOnly(2029, 1, 9),
                19044 => iot ? new DateOnly(2032, 1, 13) : new DateOnly(2027, 1, 12),
                26100 => iot ? new DateOnly(2034, 10, 10) : new DateOnly(2029, 10, 9),
                _ => null,
            };
        }

        if (build >= 22000)
        {
            // Dates des versions de Windows 11 : liste en service (intégrée, ou téléchargée et signée).
            return ThreatLists.Current.Windows11.FirstOrDefault(w => w.Build == build) is { } dates
                ? (LongServicingEditions.Contains(edition) ? dates.Enterprise : dates.HomePro)
                : null;
        }

        if (build is >= 10240 and < 19045)
        {
            // Versions de Windows 10 antérieures à 22H2 : toutes hors support depuis 2023 au plus tard.
            return new DateOnly(2023, 6, 13);
        }

        if (build == 19045)
        {
            return !esu ? Windows10End : LongServicingEditions.Contains(edition) ? Windows10CommercialEsuEnd : Windows10ConsumerEsuEnd;
        }

        return null;
    }

    /// <summary>
    /// Processeurs Intel de bureau de 13e et 14e génération touchés par l'instabilité de tension
    /// (i5-13600 et au-dessus, modèles 65 W et plus ; les portables et les modèles T ne le sont pas).
    /// </summary>
    public static bool IsUnstableIntelGeneration(string processorName) => UnstableIntel().IsMatch(processorName);

    /// <summary>Le microcode 0x12B (septembre 2024) arrête la dégradation ; il arrive par une mise à jour du BIOS.</summary>
    public const uint FixedIntelMicrocode = 0x12B;

    [GeneratedRegex(@"\bi[579]-1[34][6-9]\d\d(K|KF|KS|F)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnstableIntel();
}
