using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Événements critiques (EF-COL-06) : écrans bleus et leurs codes, arrêts brutaux, plantages
/// d'applications, erreurs et resets de disque, vérifications du disque au démarrage, processeur
/// bridé, erreurs matérielles WHEA, pilote graphique, mémoire virtuelle, passages secteur/batterie,
/// corrections de l'horloge, coupures Wi-Fi, échecs de connexion, et durée du dernier démarrage. Les
/// requêtes filtrent par fournisseur et identifiant d'événement connus pour rester légères.
/// </summary>
public sealed class EventLogCollector(TimeProvider timeProvider) : ICollector
{
    private const long Day = 86_400_000;
    private const int MaxRecords = 1000;

    // Contrôleurs de stockage qui signalent un reset (événement 129) : AHCI, NVMe, Intel RST.
    private const string ResetProviders =
        "Provider[@Name='storahci' or @Name='stornvme' or @Name='iaStorA' or @Name='iaStorAC' or @Name='iaStorAVC' or @Name='iaStorV' or @Name='iaStorAfs']";

    public string Name => "evenements";

    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public Task<CollectorResult> CollectAsync(CancellationToken cancellationToken)
    {
        var result = new CollectorResultBuilder(Name, timeProvider);
        var recent = new List<object>();

        Count(result, recent, "evenements.ecrans_bleus_7j", "System",
            $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {7 * Day}]]]",
            "Écran bleu");
        Count(result, recent, "evenements.arrets_brutaux_7j", "System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= {7 * Day}]]]",
            "Arrêt brutal");
        Count(result, recent, "evenements.plantages_applications_24h", "Application",
            $"*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= {Day}]]]",
            "Plantage d'application");
        Count(result, recent, "evenements.erreurs_disque_24h", "System",
            $"*[System[((Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)) or (Provider[@Name='Microsoft-Windows-Ntfs'] and EventID=55)) and TimeCreated[timediff(@SystemTime) <= {Day}]]]",
            "Erreur disque");
        Count(result, recent, "evenements.resets_disque_24h", "System",
            $"*[System[{ResetProviders} and EventID=129 and TimeCreated[timediff(@SystemTime) <= {Day}]]]",
            "Reset du disque");
        Count(result, recent, "evenements.verifications_disque_demarrage_30j", "Application",
            $"*[System[Provider[@Name='Microsoft-Windows-Wininit'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {30 * Day}]]]",
            "Vérification du disque au démarrage");
        // 17 (erreur PCI Express corrigée) est écarté : beaucoup de portables en écrivent sans cesse,
        // sans conséquence. Restent les erreurs fatales (1, 18), processeur (19) et mémoire (47).
        Count(result, recent, "evenements.erreurs_materiel_30j", "System",
            $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1 or EventID=18 or EventID=19 or EventID=47) and TimeCreated[timediff(@SystemTime) <= {30 * Day}]]]",
            "Erreur matérielle (WHEA)");
        Count(result, recent, "evenements.pilote_graphique_7j", "System",
            $"*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= {7 * Day}]]]",
            "Pilote graphique redémarré");
        Count(result, recent, "evenements.memoire_virtuelle_faible_7j", "System",
            $"*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004 and TimeCreated[timediff(@SystemTime) <= {7 * Day}]]]",
            "Mémoire virtuelle faible");
        Count(result, recent, "evenements.changements_alimentation_24h", "System",
            $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=105 and TimeCreated[timediff(@SystemTime) <= {Day}]]]",
            null);
        // Connexions réseau ratées (type 3) ou Bureau à distance (type 10) : pas les fautes de frappe
        // au clavier du PC. Le journal Sécurité n'est lisible que par le service (compte système).
        Count(result, recent, "securite.echecs_connexion_24h", "Security",
            $"*[System[EventID=4625 and TimeCreated[timediff(@SystemTime) <= {Day}]] and EventData[Data[@Name='LogonType']='3' or Data[@Name='LogonType']='10']]",
            null);

        result.Try("evenements.processeur_bride_minutes_7j", () =>
        {
            // Beaucoup de portables écrivent l'événement 37 à chaque démarrage : on additionne la durée
            // réelle du bridage du premier cœur, pas le nombre d'événements.
            var seconds = Read("System",
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=37 and TimeCreated[timediff(@SystemTime) <= {7 * Day}]] and EventData[Data[@Name='Number']='0']]",
                r => long.TryParse(Data(XDocument.Parse(r.ToXml()), "CapDurationInSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0);
            result.Add("evenements.processeur_bride_minutes_7j", Math.Round(seconds.Sum() / 60.0), "min");
        });

        result.Try("evenements.coupures_courant_30j", () =>
        {
            // Une vraie coupure : arrêt brutal (41) sans écran bleu (BugcheckCode 0) et sans appui long
            // sur le bouton d'alimentation (PowerButtonTimestamp 0).
            var cuts = Read("System",
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[timediff(@SystemTime) <= {30 * Day}]]]",
                r => IsPowerCut(XDocument.Parse(r.ToXml())) ? r.TimeCreated : null);
            result.Add("evenements.coupures_courant_30j", DistinctIncidents(cuts.OfType<DateTime>(), TimeSpan.FromMinutes(10)), "");
        });

        result.Try("evenements.deconnexions_wifi_24h", () =>
        {
            List<DateTime> drops;
            try
            {
                // La raison 3 est un changement de réseau voulu par l'utilisateur : ce n'est pas une coupure.
                drops = Read("Microsoft-Windows-WLAN-AutoConfig/Operational",
                    $"*[System[EventID=8003 and TimeCreated[timediff(@SystemTime) <= {Day}]] and EventData[Data[@Name='ReasonCode']!='3']]",
                    r => r.TimeCreated).OfType<DateTime>().ToList();
            }
            catch (EventLogNotFoundException)
            {
                return;
            }

            // La mise en veille et le réveil coupent aussi le Wi-Fi : ces coupures-là sont écartées.
            var sleeps = Read("System",
                $"*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=107 or EventID=506 or EventID=507)) or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)) and TimeCreated[timediff(@SystemTime) <= {Day + 3_600_000}]]]",
                r => r.TimeCreated).OfType<DateTime>().ToList();
            result.Add("evenements.deconnexions_wifi_24h", UnexplainedDrops(drops, sleeps, TimeSpan.FromMinutes(3)), "");
        });

        result.Try("evenements.corrections_horloge_30j", () =>
        {
            var jumps = Read("System",
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=1 and TimeCreated[timediff(@SystemTime) <= {30 * Day}]]]",
                r => ClockJump(XDocument.Parse(r.ToXml())));
            result.Add("evenements.corrections_horloge_30j", jumps.Count(IsLargeCorrection), "");

            // Horloge restée dans le passé : plus ancienne que l'installation de SoluPulse elle-même.
            var installed = File.GetLastWriteTimeUtc(typeof(EventLogCollector).Assembly.Location);
            result.Add("systeme.horloge_dans_le_passe", timeProvider.GetUtcNow().UtcDateTime < installed.AddDays(-1) ? 1 : 0, "");
        });

        result.Try("ecrans_bleus", () =>
        {
            var codes = Read("System",
                $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {30 * Day}]]]",
                r => BugCheck.ParseCode(XDocument.Parse(r.ToXml())));
            var known = codes.OfType<uint>().ToList();
            foreach (var family in Enum.GetValues<BugCheckFamily>().Where(f => f != BugCheckFamily.Autre))
            {
                result.Add($"ecrans_bleus.{BugCheck.MetricOf(family)}_30j", known.Count(c => BugCheck.FamilyOf(c) == family), "");
            }

            result.Add("ecrans_bleus.meme_code_30j", known.GroupBy(c => c).Select(g => g.Count()).DefaultIfEmpty(0).Max(), "");
            result.Snapshot("ecrans_bleus", known.GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => new
            {
                Code = BugCheck.Format(g.Key),
                Nom = BugCheck.NameOf(g.Key),
                Nombre = g.Count(),
            }));
        });

        result.Try("demarrage", () =>
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            if (record is null)
            {
                return;
            }

            var bootTime = Data(XDocument.Parse(record.ToXml()), "BootTime");
            if (double.TryParse(bootTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0)
            {
                result.Add("systeme.duree_demarrage_s", Math.Round(milliseconds / 1000, 1), "s");
            }
        });

        result.Snapshot("evenements_recents", recent.Take(30));
        return Task.FromResult(result.Build());
    }

    /// <summary>Arrêt brutal sans écran bleu ni appui long sur le bouton : le courant a été coupé.</summary>
    internal static bool IsPowerCut(XDocument xml) =>
        Data(xml, "BugcheckCode") is "0" or null
        && Data(xml, "PowerButtonTimestamp") is "0" or null;

    /// <summary>Coupures Wi-Fi qui ne tombent pas à moins de <paramref name="margin"/> d'une mise en veille ou d'un réveil.</summary>
    internal static int UnexplainedDrops(IEnumerable<DateTime> drops, IReadOnlyCollection<DateTime> sleeps, TimeSpan margin) =>
        drops.Count(d => !sleeps.Any(s => (d - s).Duration() <= margin));

    /// <summary>Compte les dates séparées d'au moins <paramref name="gap"/> : un incident = un groupe d'événements proches.</summary>
    internal static int DistinctIncidents(IEnumerable<DateTime> times, TimeSpan gap)
    {
        var count = 0;
        DateTime? last = null;
        foreach (var time in times.Order())
        {
            if (last is not { } previous || time - previous > gap)
            {
                count++;
            }

            last = time;
        }

        return count;
    }

    /// <summary>Changement d'horloge (Kernel-General 1) : écart entre la nouvelle et l'ancienne heure, et raison.</summary>
    internal static (TimeSpan Delta, int Reason)? ClockJump(XDocument xml) =>
        DateTimeOffset.TryParse(Data(xml, "NewTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var newTime)
        && DateTimeOffset.TryParse(Data(xml, "OldTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var oldTime)
            ? (newTime - oldTime, int.TryParse(Data(xml, "Reason"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reason) ? reason : 0)
            : null;

    /// <summary>
    /// La raison 2 est la remise à l'heure au réveil d'une veille : l'écart y est normal. Une autre
    /// correction d'au moins un jour signifie que l'horloge du PC était fausse au démarrage, souvent à
    /// cause de la pile de la carte mère.
    /// </summary>
    internal static bool IsLargeCorrection((TimeSpan Delta, int Reason)? jump) =>
        jump is { } j && j.Reason != 2 && j.Delta.Duration() >= TimeSpan.FromDays(1);

    internal static string? Data(XDocument xml, string name) =>
        xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == name)?.Value;

    private static List<T> Read<T>(string log, string xpath, Func<EventRecord, T> select)
    {
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        var values = new List<T>();
        for (var record = reader.ReadEvent(); record is not null && values.Count < MaxRecords; record = reader.ReadEvent())
        {
            using (record)
            {
                values.Add(select(record));
            }
        }

        return values;
    }

    private static void Count(CollectorResultBuilder result, List<object> recent, string metric, string log, string xpath, string? label)
    {
        result.Try(metric, () =>
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            EventLogReader reader;
            try
            {
                reader = new EventLogReader(query);
            }
            catch (EventLogNotFoundException)
            {
                // Journal absent (pas de Wi-Fi sur ce PC, par exemple) : rien à compter.
                return;
            }

            using var owned = reader;
            var count = 0;
            for (var record = reader.ReadEvent(); record is not null && count < MaxRecords; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (label is not null && count < 5)
                    {
                        recent.Add(new { Type = label, Date = record.TimeCreated, Source = record.ProviderName, Id = record.Id });
                    }

                    count++;
                }
            }

            result.Add(metric, count, "");
        });
    }
}

public enum BugCheckFamily
{
    Memoire,
    Materiel,
    Graphique,
    Stockage,
    Autre,
}

/// <summary>Codes d'arrêt des écrans bleus : la famille oriente vers le bon test ou la bonne réparation.</summary>
public static class BugCheck
{
    private static readonly Dictionary<uint, (string Name, BugCheckFamily Family)> Known = new()
    {
        [0x1A] = ("MEMORY_MANAGEMENT", BugCheckFamily.Memoire),
        [0x4E] = ("PFN_LIST_CORRUPT", BugCheckFamily.Memoire),
        [0x12B] = ("FAULTY_HARDWARE_CORRUPTED_PAGE", BugCheckFamily.Memoire),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", BugCheckFamily.Materiel),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", BugCheckFamily.Materiel),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", BugCheckFamily.Materiel),
        [0x116] = ("VIDEO_TDR_FAILURE", BugCheckFamily.Graphique),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", BugCheckFamily.Graphique),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", BugCheckFamily.Graphique),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", BugCheckFamily.Graphique),
        [0x141] = ("VIDEO_ENGINE_TIMEOUT_DETECTED", BugCheckFamily.Graphique),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", BugCheckFamily.Stockage),
        [0x77] = ("KERNEL_STACK_INPAGE_ERROR", BugCheckFamily.Stockage),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", BugCheckFamily.Stockage),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", BugCheckFamily.Stockage),
        [0x24] = ("NTFS_FILE_SYSTEM", BugCheckFamily.Stockage),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", BugCheckFamily.Autre),
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", BugCheckFamily.Autre),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", BugCheckFamily.Autre),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", BugCheckFamily.Autre),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", BugCheckFamily.Autre),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", BugCheckFamily.Autre),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", BugCheckFamily.Autre),
        [0xEF] = ("CRITICAL_PROCESS_DIED", BugCheckFamily.Autre),
    };

    /// <summary>Le premier paramètre de l'événement 1001 commence par le code : « 0x0000001a (0x…, …) ».</summary>
    public static uint? ParseCode(XDocument xml)
    {
        var text = EventLogCollector.Data(xml, "param1")?.Trim();
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = text.IndexOfAny([' ', '(']);
        var hex = (end > 0 ? text[..end] : text)[2..];
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    public static BugCheckFamily FamilyOf(uint code) => Known.TryGetValue(code, out var known) ? known.Family : BugCheckFamily.Autre;

    public static string? NameOf(uint code) => Known.TryGetValue(code, out var known) ? known.Name : null;

    public static string Format(uint code) => $"0x{code:X}";

    public static string MetricOf(BugCheckFamily family) => family switch
    {
        BugCheckFamily.Memoire => "memoire",
        BugCheckFamily.Materiel => "materiel",
        BugCheckFamily.Graphique => "graphique",
        BugCheckFamily.Stockage => "stockage",
        _ => "autre",
    };
}
