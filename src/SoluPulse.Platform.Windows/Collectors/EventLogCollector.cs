using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using SoluPulse.Core.Collection;

namespace SoluPulse.Platform.Windows.Collectors;

/// <summary>
/// Événements critiques (EF-COL-06) : écrans bleus, arrêts brutaux, plantages d'applications,
/// erreurs disque, et durée du dernier démarrage. Les requêtes filtrent par fournisseur et identifiant
/// d'événement connus pour rester légères.
/// </summary>
public sealed class EventLogCollector(TimeProvider timeProvider) : ICollector
{
    private const long Day = 86_400_000;
    private const int MaxRecords = 1000;

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

            var bootTime = XDocument.Parse(record.ToXml()).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == "BootTime")?.Value;
            if (double.TryParse(bootTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0)
            {
                result.Add("systeme.duree_demarrage_s", Math.Round(milliseconds / 1000, 1), "s");
            }
        });

        result.Snapshot("evenements_recents", recent.Take(30));
        return Task.FromResult(result.Build());
    }

    private static void Count(CollectorResultBuilder result, List<object> recent, string metric, string log, string xpath, string label)
    {
        result.Try(metric, () =>
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var count = 0;
            for (var record = reader.ReadEvent(); record is not null && count < MaxRecords; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (count < 5)
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
