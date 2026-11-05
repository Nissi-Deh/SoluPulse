using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

/// <summary>Une valeur en direct ; la barre n'apparaît que si la valeur a une échelle de 0 à 100.</summary>
public sealed class GaugeItem(string label, string value, double? percent, Color color)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public double Percent { get; } = Math.Clamp(percent ?? 0, 0, 100);

    public bool HasBar { get; } = percent is not null;

    public IBrush Brush { get; } = new SolidColorBrush(color);
}

/// <summary>Une ligne « libellé : valeur », avec une pastille d'état facultative (toujours doublée d'un texte).</summary>
public sealed class DetailRow(string label, string value, Color? state = null)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public bool HasState { get; } = state is not null;

    public IBrush StateBrush { get; } = new SolidColorBrush(state ?? Palette.Unknown);
}

public sealed class ComponentCard(string glyph, string title, string? subtitle = null)
{
    public string Glyph { get; } = glyph;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle ?? "";

    public bool HasSubtitle => Subtitle.Length > 0;

    public List<GaugeItem> Gauges { get; } = [];

    public List<DetailRow> Details { get; } = [];

    public string Hint { get; set; } = "";

    public bool HasGauges => Gauges.Count > 0;

    public bool HasDetails => Details.Count > 0;

    public bool HasHint => Hint.Length > 0;

    public IBrush HintBrush { get; } = new SolidColorBrush(Palette.Attention);
}

public sealed class SensorGroup(string title, IReadOnlyList<DetailRow> rows)
{
    public string Title { get; } = title;

    public IReadOnlyList<DetailRow> Rows { get; } = rows;
}

/// <summary>« Mon PC » : chaque composant détecté, ses caractéristiques et ses mesures en direct.</summary>
public sealed partial class ComputerViewModel(AgentClient client) : PageViewModel
{
    public override string Title => "Mon PC";

    public ObservableCollection<ComponentCard> Cards { get; } = [];

    public ObservableCollection<SensorGroup> Sensors { get; } = [];

    [ObservableProperty]
    private string _sensorsTitle = "";

    [ObservableProperty]
    private bool _hasSensors;

    [ObservableProperty]
    private bool _empty;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var inventory = await client.Api.GetInventoryAsync(AgentClient.Empty);
        var metrics = new MetricValues(await client.Api.GetLiveMetricsAsync(AgentClient.Empty));
        using var snapshots = new Snapshots(inventory);

        Cards.Clear();
        foreach (var card in ComponentCards.Build(snapshots, metrics))
        {
            Cards.Add(card);
        }

        Sensors.Clear();
        foreach (var group in ComponentCards.SensorTable(snapshots))
        {
            Sensors.Add(group);
        }

        HasSensors = Sensors.Count > 0;
        SensorsTitle = $"Tous les capteurs ({Sensors.Sum(s => s.Rows.Count)})";
        Empty = Cards.Count == 0;
    });

    [RelayCommand]
    private Task ReloadAsync() => RefreshAsync();
}

/// <summary>Dernières valeurs des métriques ; une valeur vieille de plus d'un jour est ignorée.</summary>
internal sealed class MetricValues
{
    private readonly Dictionary<string, double> _values;

    public MetricValues(MetricList list)
    {
        var limit = DateTimeOffset.UtcNow.AddDays(-1);
        _values = list.Metrics
            .Where(m => m.LastSeen is null || m.LastSeen.ToDateTimeOffset() >= limit)
            .ToDictionary(m => m.Metric, m => m.LastValue, StringComparer.Ordinal);
    }

    public double? this[string metric] => _values.TryGetValue(metric, out var value) ? value : null;

    /// <summary>Noms des objets mesurés sous un préfixe, par exemple les cartes graphiques sous « gpu. ».</summary>
    public IEnumerable<string> Subjects(string prefix) => _values.Keys
        .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
        .Select(k => k[prefix.Length..].Split('.')[0])
        .Distinct(StringComparer.Ordinal);
}

/// <summary>Instantanés d'inventaire, lus en JSON (propriétés en camelCase).</summary>
internal sealed class Snapshots : IDisposable
{
    private readonly Dictionary<string, JsonDocument> _documents = new(StringComparer.Ordinal);

    public Snapshots(InventoryReply reply)
    {
        foreach (var item in reply.Items)
        {
            try
            {
                _documents[item.Kind] = JsonDocument.Parse(item.Json);
            }
            catch (JsonException)
            {
                // Instantané illisible : ignoré.
            }
        }
    }

    public JsonElement this[string kind] => _documents.TryGetValue(kind, out var document) ? document.RootElement : default;

    public void Dispose()
    {
        foreach (var document in _documents.Values)
        {
            document.Dispose();
        }
    }
}

internal static class JsonRead
{
    public static JsonElement Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? Str(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

    public static double? Num(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    public static bool? Bool(this JsonElement element, string name) =>
        element.Get(name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string? name = null)
    {
        var array = name is null ? element : element.Get(name);
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
    }
}

/// <summary>Construit les fiches des composants à partir de l'inventaire et des dernières mesures.</summary>
internal static class ComponentCards
{
    public static IEnumerable<ComponentCard> Build(Snapshots snapshots, MetricValues metrics)
    {
        var hardware = snapshots["materiel"];
        var cards = new List<ComponentCard?>
        {
            Computer(hardware, snapshots["windows"], metrics),
            Security(snapshots["securite"], metrics),
            Processor(hardware, metrics),
            Memory(hardware, metrics),
        };
        cards.AddRange(Graphics(hardware, metrics));
        cards.AddRange(Disks(snapshots["disques"], metrics));
        cards.Add(Volumes(snapshots["volumes"]));
        cards.Add(Battery(metrics));
        cards.Add(Network(hardware, metrics));
        cards.Add(Peripherals(hardware));
        return cards.OfType<ComponentCard>();
    }

    /// <summary>Valeurs en direct du tableau de bord.</summary>
    public static IEnumerable<GaugeItem> LiveGauges(MetricValues metrics)
    {
        if (metrics["cpu.utilisation_pct"] is { } cpu)
        {
            yield return Percent("Processeur", cpu, HighIsBad(cpu, 80, 95));
        }

        if (metrics["cpu.temperature_c"] is { } temperature)
        {
            yield return Temperature("Température du processeur", temperature, 80, 90);
        }

        if (metrics["memoire.utilisation_pct"] is { } memory)
        {
            yield return Percent("Mémoire", memory, HighIsBad(memory, 85, 95));
        }

        if (metrics["disque.systeme.espace_libre_pct"] is { } free)
        {
            var freeGb = metrics["disque.systeme.espace_libre_go"];
            yield return new GaugeItem("Disque système", freeGb is { } gb ? $"{Number(gb)} Go libres" : $"{Number(free)} % libre", 100 - free, LowIsBad(free, 20, 10));
        }

        var gpu = metrics.Subjects("gpu.").Select(g => metrics[$"gpu.{g}.temperature_c"]).OfType<double>().DefaultIfEmpty(double.NaN).Max();
        if (!double.IsNaN(gpu))
        {
            yield return Temperature("Carte graphique", gpu, 85, 95);
        }

        if (metrics["batterie.charge_pct"] is { } charge)
        {
            var plugged = metrics["alimentation.secteur"] == 1;
            yield return new GaugeItem(plugged ? "Batterie (branchée)" : "Batterie", $"{Number(charge)} %", charge, LowIsBad(charge, 20, 10));
        }

        if (metrics["reseau.internet.latence_ms"] is { } latency)
        {
            yield return new GaugeItem("Internet", $"{Number(latency)} ms", null, HighIsBad(latency, 100, 300));
        }
    }

    public static IEnumerable<SensorGroup> SensorTable(Snapshots snapshots) => snapshots["capteurs"].Items()
        .Select(c => new SensorGroup(
            $"{c.Str("composant")} · {c.Str("type")}",
            c.Items("capteurs")
                .Select(s => new DetailRow($"{s.Str("mesure")} · {s.Str("nom")}", $"{Number(s.Num("valeur") ?? 0)} {s.Str("unite")}"))
                .ToList()))
        .Where(g => g.Rows.Count > 0);

    private static ComponentCard Computer(JsonElement hardware, JsonElement windows, MetricValues metrics)
    {
        var computer = hardware.Get("Ordinateur");
        var card = new ComponentCard("💻", computer.Str("modele") ?? "Ce PC", Join(computer.Str("fabricant"), computer.Str("type")));
        Add(card, "Windows", Join(windows.Str("edition"), windows.Str("version"), windows.Str("build") is { } build ? $"build {build}" : null));
        Add(card, "Nom du PC", windows.Str("machine"));
        Add(card, "Carte mère", computer.Str("carteMere"));
        Add(card, "BIOS", computer.Str("bios"));
        if (metrics["systeme.jours_depuis_demarrage"] is { } days)
        {
            card.Details.Add(new DetailRow("Allumé depuis", days < 1 ? $"{Number(days * 24)} h" : $"{Number(days)} jours", days > 14 ? Palette.Information : null));
        }

        return card;
    }

    private static ComponentCard? Security(JsonElement security, MetricValues metrics)
    {
        if (metrics["securite.antivirus_actif"] is null && metrics["securite.pare_feu.profils_desactives"] is null)
        {
            return null;
        }

        var card = new ComponentCard("🛡", "Sécurité", "Protections de Windows");
        var antivirus = security.Items("Antivirus").Where(a => a.Bool("actif") == true).Select(a => a.Str("nom")).OfType<string>().ToList();
        card.Details.Add(metrics["securite.antivirus_actif"] == 1
            ? new DetailRow("Antivirus", antivirus.Count > 0 ? $"{string.Join(", ", antivirus)} : actif" : "Microsoft Defender : actif", Palette.Ok)
            : new DetailRow("Antivirus", "Aucun antivirus actif", Palette.Critique));
        if (metrics["securite.defender.signatures_age_j"] is { } signatures)
        {
            card.Details.Add(new DetailRow("Définitions de virus", signatures < 1 ? "à jour (aujourd'hui)" : $"mises à jour il y a {Number(signatures)} jours", HighIsBad(signatures, 3, 7)));
        }
        else if (metrics["securite.antivirus_a_jour"] is { } upToDate)
        {
            card.Details.Add(new DetailRow("Définitions de virus", upToDate == 1 ? "à jour" : "périmées", upToDate == 1 ? Palette.Ok : Palette.Attention));
        }

        if (metrics["securite.defender.dernier_scan_j"] is { } scan)
        {
            card.Details.Add(new DetailRow("Dernière analyse", scan < 1 ? "aujourd'hui" : $"il y a {Number(scan)} jours", HighIsBad(scan, 8, 14)));
        }

        if (metrics["securite.pare_feu.profils_desactives"] is { } firewallOff)
        {
            card.Details.Add(firewallOff == 0
                ? new DetailRow("Pare-feu", "actif sur tous les réseaux", Palette.Ok)
                : new DetailRow("Pare-feu", $"coupé sur {Number(firewallOff)} type(s) de réseau", Palette.Critique));
        }

        card.Details.Add(State("Contrôle de compte d'utilisateur", metrics["securite.uac_actif"], "actif", "désactivé", Palette.Attention));
        card.Details.Add(State("Démarrage sécurisé", metrics["securite.demarrage_securise"], "actif", "désactivé", Palette.Information));
        card.Details.Add(State("Chiffrement du disque", metrics["securite.chiffrement_disque_systeme"], "BitLocker actif", "non chiffré", Palette.Information));
        var tpmVersion = security.Get("Tpm").Str("version");
        card.Details.Add(State("Puce TPM", metrics["securite.tpm_pret"], tpmVersion is null ? "prête" : $"prête (version {tpmVersion})", "absente ou désactivée", Palette.Information));
        return card;
    }

    private static ComponentCard? Processor(JsonElement hardware, MetricValues metrics)
    {
        var processor = hardware.Items("Processeurs").FirstOrDefault();
        if (processor.ValueKind != JsonValueKind.Object && metrics["cpu.utilisation_pct"] is null)
        {
            return null;
        }

        var maxClock = processor.Num("frequenceMaxMhz");
        var card = new ComponentCard("⚙", processor.Str("nom") ?? "Processeur", Join(
            processor.Num("coeurs") is { } cores ? $"{Number(cores)} cœurs" : null,
            processor.Num("threads") is { } threads ? $"{Number(threads)} threads" : null,
            maxClock is { } clock ? $"{Number(clock / 1000)} GHz" : null));
        if (metrics["cpu.utilisation_pct"] is { } load)
        {
            card.Gauges.Add(Percent("Utilisation", load, HighIsBad(load, 80, 95)));
        }

        if (metrics["cpu.temperature_c"] is { } temperature)
        {
            card.Gauges.Add(Temperature("Température", temperature, 80, 90));
        }
        else
        {
            card.Hint = "Température indisponible : le pilote PawnIO n'est pas actif ou ce processeur n'a pas de capteur lisible.";
        }

        if (metrics["cpu.frequence_mhz"] is { } frequency)
        {
            card.Gauges.Add(new GaugeItem("Fréquence", $"{Number(frequency / 1000)} GHz", maxClock is > 0 ? 100 * frequency / maxClock : null, Palette.Information));
        }

        if (metrics["cpu.puissance_w"] is { } power)
        {
            card.Gauges.Add(new GaugeItem("Consommation", $"{Number(power)} W", null, Palette.Information));
        }

        Add(card, "Cache L3", processor.Num("cacheL3Ko") is { } l3 and > 0 ? $"{Number(l3 / 1024)} Mo" : null);
        Add(card, "Support", processor.Str("support"));
        return card;
    }

    private static ComponentCard? Memory(JsonElement hardware, MetricValues metrics)
    {
        var memory = hardware.Get("Memoire");
        var modules = memory.Items("barrettes").ToList();
        var totalMb = metrics["memoire.totale_mo"];
        if (modules.Count == 0 && totalMb is null)
        {
            return null;
        }

        var total = memory.Num("totaleGo") ?? Math.Round((totalMb ?? 0) / 1024);
        var type = modules.Select(m => m.Str("type")).FirstOrDefault(t => t is not null and not "inconnu");
        var slots = memory.Num("emplacements");
        var card = new ComponentCard("🧠", $"{Number(total)} Go de mémoire{(type is null ? "" : $" {type}")}", Join(
            modules.Count > 0 ? $"{modules.Count} barrette{(modules.Count > 1 ? "s" : "")}{(slots is > 0 ? $" sur {Number(slots.Value)} emplacements" : "")}" : null,
            memory.Num("maximumGo") is { } max and > 0 ? $"jusqu'à {Number(max)} Go" : null));
        if (metrics["memoire.utilisation_pct"] is { } used)
        {
            card.Gauges.Add(Percent("Utilisation", used, HighIsBad(used, 85, 95)));
        }

        if (metrics["memoire.disponible_mo"] is { } available)
        {
            card.Gauges.Add(new GaugeItem("Disponible", $"{Number(available / 1024)} Go", null, Palette.Information));
        }

        foreach (var module in modules)
        {
            card.Details.Add(new DetailRow(module.Str("emplacement") ?? "Barrette", Join(
                module.Num("tailleGo") is { } size ? $"{Number(size)} Go" : null,
                module.Str("type"),
                module.Num("frequenceMhz") is { } mhz and > 0 ? $"{Number(mhz)} MHz" : null,
                module.Str("fabricant"),
                module.Str("reference")) ?? "—"));
        }

        if (slots is { } slotCount && slotCount > modules.Count && modules.Count > 0)
        {
            card.Details.Add(new DetailRow("Emplacements libres", $"{Number(slotCount - modules.Count)} : vous pouvez ajouter de la mémoire"));
        }

        if (memory.Bool("taillesIdentiques") == false && modules.Count > 1)
        {
            card.Hint = "Les barrettes n'ont pas toutes la même taille : une partie de la mémoire fonctionne sans le double canal, donc un peu moins vite.";
        }

        return card;
    }

    private static IEnumerable<ComponentCard> Graphics(JsonElement hardware, MetricValues metrics)
    {
        foreach (var gpu in hardware.Items("CartesGraphiques"))
        {
            var name = gpu.Str("nom") ?? "Carte graphique";
            var prefix = $"gpu.{Format.Segment(name)}";
            var card = new ComponentCard("🎮", name, "Carte graphique");
            if (metrics[$"{prefix}.temperature_c"] is { } temperature)
            {
                card.Gauges.Add(Temperature("Température", temperature, 85, 95));
            }

            if (metrics[$"{prefix}.utilisation_pct"] is { } load)
            {
                card.Gauges.Add(Percent("Utilisation", load, HighIsBad(load, 90, 99)));
            }

            if (metrics[$"{prefix}.memoire_utilisee_pct"] is { } vram)
            {
                var totalMb = metrics[$"{prefix}.memoire_totale_mo"];
                card.Gauges.Add(new GaugeItem("Mémoire vidéo", totalMb is { } mb ? $"{Number(vram)} % de {Number(mb / 1024)} Go" : $"{Number(vram)} %", vram, HighIsBad(vram, 90, 98)));
            }

            if (metrics[$"{prefix}.puissance_w"] is { } power)
            {
                card.Gauges.Add(new GaugeItem("Consommation", $"{Number(power)} W", null, Palette.Information));
            }

            Add(card, "Pilote", gpu.Str("pilote"));
            Add(card, "Affichage", gpu.Str("affichage"));
            yield return card;
        }
    }

    private static IEnumerable<ComponentCard> Disks(JsonElement disks, MetricValues metrics)
    {
        foreach (var disk in disks.Items())
        {
            var name = disk.Str("name") ?? "Disque";
            var prefix = $"disque_physique.{Format.Segment(name)}";
            var card = new ComponentCard("💾", name, Join(disk.Str("type"), disk.Str("bus"), disk.Num("tailleGo") is { } size ? $"{Number(size)} Go" : null));
            if (metrics[$"{prefix}.temperature_c"] is { } temperature)
            {
                card.Gauges.Add(Temperature("Température", temperature, 55, 65));
            }

            if (metrics[$"{prefix}.usure_pct"] is { } wear)
            {
                card.Gauges.Add(new GaugeItem("Usure", $"{Number(wear)} %", wear, HighIsBad(wear, 80, 95)));
            }

            if (metrics[$"{prefix}.etat_sante"] is { } health)
            {
                card.Details.Add(health switch
                {
                    0 => new DetailRow("État selon Windows", "bon", Palette.Ok),
                    1 => new DetailRow("État selon Windows", "avertissement", Palette.Attention),
                    _ => new DetailRow("État selon Windows", "défaillant", Palette.Critique),
                });
            }

            if (metrics[$"{prefix}.prediction_panne"] == 1)
            {
                card.Details.Add(new DetailRow("Prédiction SMART", "panne probable", Palette.Critique));
            }

            Add(card, "Heures de fonctionnement", metrics[$"{prefix}.heures_fonctionnement"] is { } hours ? $"{Number(hours)} h" : null);
            foreach (var (metric, label) in new[] { ("secteurs_realloues", "Secteurs réalloués"), ("secteurs_en_attente", "Secteurs en attente"), ("secteurs_irrecuperables", "Secteurs irrécupérables") })
            {
                if (metrics[$"{prefix}.{metric}"] is { } count)
                {
                    card.Details.Add(new DetailRow(label, Number(count), count == 0 ? Palette.Ok : Palette.Attention));
                }
            }

            yield return card;
        }
    }

    private static ComponentCard? Volumes(JsonElement volumes)
    {
        var list = volumes.Items().ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var card = new ComponentCard("📁", "Espace disque", $"{list.Count} volume{(list.Count > 1 ? "s" : "")}");
        foreach (var volume in list)
        {
            var size = volume.Num("tailleGo") ?? 0;
            var free = volume.Num("libreGo") ?? 0;
            var freePercent = size > 0 ? 100 * free / size : 0;
            var label = $"{volume.Str("lettre")?.TrimEnd('\\')} {volume.Str("nom") ?? (volume.Bool("systeme") == true ? "Windows" : "")}".Trim();
            card.Gauges.Add(new GaugeItem(label, $"{Number(free)} Go libres sur {Number(size)}", 100 - freePercent, LowIsBad(freePercent, 20, 10)));
        }

        return card;
    }

    private static ComponentCard? Battery(MetricValues metrics)
    {
        if (metrics["batterie.sante_pct"] is not { } health)
        {
            return null;
        }

        var card = new ComponentCard("🔋", "Batterie", metrics["batterie.cycles"] is { } cycles ? $"{Number(cycles)} cycles de charge" : null);
        card.Gauges.Add(new GaugeItem("Santé", $"{Number(health)} %", health, LowIsBad(health, 70, 50)));
        if (metrics["batterie.charge_pct"] is { } charge)
        {
            card.Gauges.Add(new GaugeItem(metrics["alimentation.secteur"] == 1 ? "Charge (branchée)" : "Charge", $"{Number(charge)} %", charge, LowIsBad(charge, 20, 10)));
        }

        Add(card, "Capacité d'origine", metrics["batterie.capacite_origine_mwh"] is { } designed ? $"{Number(designed / 1000)} Wh" : null);
        Add(card, "Capacité actuelle", metrics["batterie.capacite_actuelle_mwh"] is { } full ? $"{Number(full / 1000)} Wh" : null);
        return card;
    }

    private static ComponentCard Network(JsonElement hardware, MetricValues metrics)
    {
        var card = new ComponentCard("🌐", "Réseau", metrics["reseau.connecte"] == 0 ? "Déconnecté" : "Connecté");
        if (metrics["reseau.internet.latence_ms"] is { } latency)
        {
            card.Gauges.Add(new GaugeItem("Latence Internet", $"{Number(latency)} ms", null, HighIsBad(latency, 100, 300)));
        }

        if (metrics["reseau.wifi.signal_pct"] is { } signal)
        {
            card.Gauges.Add(new GaugeItem("Signal Wi-Fi", $"{Number(signal)} %", signal, LowIsBad(signal, 50, 30)));
        }

        if (metrics["reseau.debit_recu_kbps"] is { } received)
        {
            card.Gauges.Add(new GaugeItem("Reçu", Throughput(received), null, Palette.Information));
        }

        if (metrics["reseau.debit_envoye_kbps"] is { } sent)
        {
            card.Gauges.Add(new GaugeItem("Envoyé", Throughput(sent), null, Palette.Information));
        }

        foreach (var adapter in hardware.Items("CartesReseau"))
        {
            var state = adapter.Str("etat");
            card.Details.Add(new DetailRow(adapter.Str("nom") ?? "Carte réseau",
                Join(state, adapter.Num("vitesseMbps") is { } speed and > 0 ? $"{Number(speed)} Mbit/s" : null) ?? "—",
                state == "connectée" ? Palette.Ok : Palette.Unknown));
        }

        return card;
    }

    private static ComponentCard? Peripherals(JsonElement hardware)
    {
        var card = new ComponentCard("🔌", "Périphériques", "Écrans, son, caméra, Bluetooth, imprimantes");
        Add(card, "Écrans", Names(hardware.Items("Ecrans").Select(e => Join(e.Str("nom"), e.Str("fabricant"), e.Num("annee") is { } year and > 0 ? Number(year) : null))));
        Add(card, "Son", Names(hardware.Items("Audio").Select(a => a.Str("nom"))));
        Add(card, "Caméras", Names(hardware.Items("Cameras").Select(c => c.Str("nom"))));
        Add(card, "Bluetooth", Names(hardware.Items("Bluetooth").Select(b => b.Str("nom"))));
        Add(card, "Appareils Bluetooth associés", Names(hardware.Items("AppareilsBluetooth").Select(b => b.ValueKind == JsonValueKind.String ? b.GetString() : null)));
        Add(card, "Lecteurs biométriques", Names(hardware.Items("LecteursBiometriques").Select(b => b.Str("nom"))));
        Add(card, "Imprimantes", Names(hardware.Items("Imprimantes").Select(p => p.Str("nom") is { } name
            ? name + (p.Bool("parDefaut") == true ? " (par défaut)" : "") + (p.Bool("horsLigne") == true ? " (hors ligne)" : "")
            : null)));
        return card.HasDetails ? card : null;
    }

    private static DetailRow State(string label, double? value, string on, string off, Color offColor) => value switch
    {
        1 => new DetailRow(label, on, Palette.Ok),
        0 => new DetailRow(label, off, offColor),
        _ => new DetailRow(label, "inconnu", Palette.Unknown),
    };

    private static GaugeItem Percent(string label, double value, Color color) => new(label, $"{Number(value)} %", value, color);

    private static GaugeItem Temperature(string label, double value, double warning, double critical) =>
        new(label, $"{Number(value)} °C", value, HighIsBad(value, warning, critical));

    private static Color HighIsBad(double value, double warning, double critical) =>
        value >= critical ? Palette.Critique : value >= warning ? Palette.Attention : Palette.Ok;

    private static Color LowIsBad(double value, double warning, double critical) =>
        value <= critical ? Palette.Critique : value <= warning ? Palette.Attention : Palette.Ok;

    private static string Throughput(double kbps) => kbps >= 1000 ? $"{Number(kbps / 1000)} Mbit/s" : $"{Number(kbps)} kbit/s";

    private static string Number(double value) => value.ToString(Math.Abs(value) >= 100 ? "0" : "0.#", Format.French);

    private static string? Join(params string?[] parts) =>
        parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } kept ? string.Join(" · ", kept) : null;

    private static string? Names(IEnumerable<string?> names) =>
        names.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } list ? string.Join(", ", list) : null;

    private static void Add(ComponentCard card, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            card.Details.Add(new DetailRow(label, value));
        }
    }
}

/// <summary>Libellés lisibles des métriques, pour l'historique.</summary>
internal static class MetricLabels
{
    private static readonly Dictionary<string, string> Components = new(StringComparer.Ordinal)
    {
        ["cpu"] = "Processeur",
        ["memoire"] = "Mémoire",
        ["gpu"] = "Carte graphique",
        ["disque"] = "Disque",
        ["disque_physique"] = "Disque",
        ["disque_activite"] = "Disque",
        ["volume"] = "Volume",
        ["batterie"] = "Batterie",
        ["alimentation"] = "Alimentation",
        ["reseau"] = "Réseau",
        ["ventilateur"] = "Ventilateur",
        ["carte_mere"] = "Carte mère",
        ["securite"] = "Sécurité",
        ["systeme"] = "Système",
        ["evenements"] = "Événements",
        ["ecrans"] = "Écrans",
    };

    private static readonly Dictionary<string, string> Measures = new(StringComparer.Ordinal)
    {
        ["temperature_c"] = "température",
        ["utilisation_pct"] = "utilisation",
        ["espace_libre_pct"] = "espace libre (%)",
        ["espace_libre_go"] = "espace libre (Go)",
        ["taille_go"] = "taille",
        ["tours_min"] = "vitesse",
        ["puissance_w"] = "consommation",
        ["frequence_mhz"] = "fréquence",
        ["memoire_utilisee_pct"] = "mémoire vidéo utilisée",
        ["memoire_totale_mo"] = "mémoire vidéo totale",
        ["usure_pct"] = "usure",
        ["sante_pct"] = "santé",
        ["charge_pct"] = "charge",
        ["latence_ms"] = "latence",
        ["perte_pct"] = "perte de paquets",
        ["signal_pct"] = "signal",
        ["debit_recu_kbps"] = "débit reçu",
        ["debit_envoye_kbps"] = "débit envoyé",
        ["etat_sante"] = "état de santé",
        ["heures_fonctionnement"] = "heures de fonctionnement",
    };

    public static string Describe(string metric)
    {
        var parts = metric.Split('.');
        if (parts.Length < 2)
        {
            return metric;
        }

        var component = Components.GetValueOrDefault(parts[0], parts[0].Replace('_', ' '));
        var measure = Measures.GetValueOrDefault(parts[^1], parts[^1].Replace('_', ' '));
        var subject = string.Join(' ', parts[1..^1]).Replace('_', ' ');
        return subject.Length > 0 && subject != "systeme" ? $"{component} {subject} : {measure}" : $"{component} : {measure}";
    }
}
