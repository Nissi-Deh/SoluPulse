using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

public sealed record MetricChoice(string Metric, string Label, string Unit);

public sealed record PeriodChoice(string Label, TimeSpan Span);

/// <summary>Graphiques d'historique pour chaque mesure (EF-IHM-05).</summary>
public sealed partial class HistoryViewModel(AgentClient client) : PageViewModel
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["cpu.temperature_c"] = "Température du processeur",
        ["cpu.utilisation_pct"] = "Utilisation du processeur",
        ["memoire.utilisation_pct"] = "Utilisation de la mémoire",
        ["disque.systeme.espace_libre_pct"] = "Espace libre du disque système",
        ["disque_activite.utilisation_pct"] = "Activité du disque",
        ["batterie.sante_pct"] = "Santé de la batterie",
        ["batterie.charge_pct"] = "Charge de la batterie",
        ["reseau.internet.latence_ms"] = "Latence Internet",
        ["reseau.passerelle.latence_ms"] = "Latence de la box",
        ["reseau.wifi.signal_pct"] = "Signal Wi-Fi",
        ["systeme.duree_demarrage_s"] = "Durée du démarrage",
    };

    public override string Title => "Historique";

    public ObservableCollection<MetricChoice> Metrics { get; } = [];

    public IReadOnlyList<PeriodChoice> Periods { get; } =
    [
        new("24 heures", TimeSpan.FromDays(1)),
        new("7 jours", TimeSpan.FromDays(7)),
        new("30 jours", TimeSpan.FromDays(30)),
        new("90 jours", TimeSpan.FromDays(90)),
        new("1 an", TimeSpan.FromDays(365)),
    ];

    [ObservableProperty]
    private MetricChoice? _selectedMetric;

    [ObservableProperty]
    private PeriodChoice? _selectedPeriod;

    [ObservableProperty]
    private ISeries[] _series = [];

    [ObservableProperty]
    private Axis[] _xAxes = [new DateTimeAxis(TimeSpan.FromHours(1), d => d.ToString("dd/MM HH:mm", Format.French))];

    [ObservableProperty]
    private string _summary = "";

    partial void OnSelectedMetricChanged(MetricChoice? value) => _ = LoadAsync();

    partial void OnSelectedPeriodChanged(PeriodChoice? value) => _ = LoadAsync();

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var current = SelectedMetric?.Metric;
        var list = await client.Api.ListMetricsAsync(AgentClient.Empty);
        Metrics.Clear();
        foreach (var metric in list.Metrics.OrderBy(m => Labels.ContainsKey(m.Metric) ? 0 : 1).ThenBy(m => m.Metric, StringComparer.Ordinal))
        {
            Metrics.Add(new MetricChoice(metric.Metric, Labels.TryGetValue(metric.Metric, out var label) ? label : MetricLabels.Describe(metric.Metric), metric.Unit));
        }

        SelectedPeriod ??= Periods[0];
        SelectedMetric = Metrics.FirstOrDefault(m => m.Metric == current) ?? (Metrics.Count > 0 ? Metrics[0] : null);
    });

    private Task LoadAsync() => SelectedMetric is null || SelectedPeriod is null ? Task.CompletedTask : RunAsync(async () =>
    {
        var to = DateTimeOffset.UtcNow;
        var history = await client.Api.GetHistoryAsync(new HistoryRequest
        {
            Metric = SelectedMetric.Metric,
            From = Timestamp.FromDateTimeOffset(to - SelectedPeriod.Span),
            To = Timestamp.FromDateTimeOffset(to),
            MaxPoints = 400,
        });
        var points = history.Points.Select(p => new DateTimePoint(p.At.ToDateTime().ToLocalTime(), p.Value)).ToArray();
        Series =
        [
            new LineSeries<DateTimePoint>
            {
                Values = points,
                Name = SelectedMetric.Label,
                GeometrySize = 0,
                LineSmoothness = 0.3,
                Fill = null,
                Stroke = new SolidColorPaint(new SKColor(14, 116, 144), 2),
            },
        ];
        var step = SelectedPeriod.Span.TotalDays <= 1 ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        XAxes = [new DateTimeAxis(step, d => d.ToString(step < TimeSpan.FromDays(1) ? "HH:mm" : "dd/MM", Format.French))];
        Summary = points.Length == 0
            ? "Aucune mesure sur cette période."
            : string.Create(Format.French, $"{points.Length} points · min {points.Min(p => p.Value):0.##} · max {points.Max(p => p.Value):0.##} · dernière {points[^1].Value:0.##} {SelectedMetric.Unit}");
    });
}

/// <summary>Journal des corrections, avec le bouton « Annuler » (EF-COR-04, EF-COR-05).</summary>
public sealed partial class JournalViewModel(AgentClient client) : PageViewModel
{
    public override string Title => "Journal des corrections";

    public ObservableCollection<ExecutionItem> Items { get; } = [];

    [ObservableProperty]
    private string _auditStatus = "";

    [ObservableProperty]
    private bool _isEmpty;

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var list = await client.Api.ListExecutionsAsync(new ListRequest { Limit = 100 });
        Items.Clear();
        foreach (var execution in list.Executions)
        {
            Items.Add(new ExecutionItem(execution));
        }

        IsEmpty = Items.Count == 0;
    });

    [RelayCommand]
    private Task UndoAsync(ExecutionItem item) => RunAsync(async () =>
    {
        var reply = await client.Api.UndoCorrectionAsync(new UndoRequest { ExecutionId = item.Info.Id }, deadline: DateTime.UtcNow.AddMinutes(30));
        AuditStatus = reply.Message;
        await RefreshAsync();
    });

    [RelayCommand]
    private Task VerifyAuditAsync() => RunAsync(async () =>
    {
        var reply = await client.Api.VerifyAuditAsync(AgentClient.Empty);
        AuditStatus = reply.Intact
            ? $"Journal d'audit intact ({reply.Entries} entrées vérifiées)."
            : $"Attention : le journal d'audit a été modifié à partir de l'entrée {reply.FirstBroken}.";
    });
}

/// <summary>Inventaire système : programmes au démarrage, services, processus, mises à jour… Le matériel est sur « Mon PC ».</summary>
public sealed partial class InventoryViewModel(AgentClient client) : PageViewModel
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["windows"] = "Windows",
        ["processus"] = "Programmes les plus gourmands",
        ["demarrage"] = "Programmes au démarrage",
        ["services_arretes"] = "Services en panne",
        ["peripheriques_en_erreur"] = "Périphériques en erreur",
        ["mises_a_jour"] = "Mises à jour en attente",
        ["evenements_recents"] = "Événements récents",
        ["cartes_reseau"] = "Cartes réseau actives",
        ["disques"] = "Disques",
        ["volumes"] = "Volumes",
        ["batterie"] = "Batterie",
        ["onduleur"] = "Onduleur",
        ["protection"] = "Protection, sauvegardes et fins de support",
        ["menaces"] = "Points de vigilance de sécurité",
        ["programmes_actifs"] = "Programmes suspects ou gourmands",
        ["ecrans_bleus"] = "Écrans bleus des 30 derniers jours",
        ["echecs_mises_a_jour"] = "Mises à jour en échec",
        ["cles_usb"] = "Clés USB branchées",
        ["rancongiciel"] = "Surveillance des rançongiciels",
    };

    // Affichés sous forme de fiches sur la page « Mon PC ».
    private static readonly HashSet<string> ShownOnComputerPage = new(StringComparer.Ordinal) { "materiel", "securite", "capteurs" };

    public override string Title => "Inventaire système";

    public ObservableCollection<InventoryEntry> Items { get; } = [];

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var reply = await client.Api.GetInventoryAsync(AgentClient.Empty);
        Items.Clear();
        foreach (var item in reply.Items
            .Where(i => !ShownOnComputerPage.Contains(i.Kind))
            .OrderBy(i => Titles.Keys.ToList().IndexOf(i.Kind) is var index and >= 0 ? index : 99))
        {
            string text;
            try
            {
                using var document = JsonDocument.Parse(item.Json);
                text = InventoryText.Render(document.RootElement);
            }
            catch (JsonException)
            {
                text = item.Json;
            }

            Items.Add(new InventoryEntry(Titles.GetValueOrDefault(item.Kind, item.Kind), $"Relevé {Format.Ago(item.At)}", text));
        }
    });
}

/// <summary>Met un instantané JSON en texte lisible : une ligne par élément, des libellés en français.</summary>
internal static class InventoryText
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["libelle"] = "Libellé",
        ["memoireMo"] = "Mémoire (Mo)",
        ["cpuPct"] = "Processeur (%)",
        ["parCpu"] = "Par utilisation du processeur",
        ["parMemoire"] = "Par utilisation de la mémoire",
        ["emplacement"] = "Emplacement",
        ["systeme"] = "Système",
        ["active"] = "Activé",
        ["presente"] = "Présente",
        ["capaciteOrigineMwh"] = "Capacité d'origine (mWh)",
        ["capaciteActuelleMwh"] = "Capacité actuelle (mWh)",
        ["driveFormat"] = "Format",
        ["lettre"] = "Lettre",
        ["processeurs"] = "Processeurs logiques",
    };

    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["go"] = "(Go)",
        ["mo"] = "(Mo)",
        ["ko"] = "(Ko)",
        ["pct"] = "(%)",
        ["mbps"] = "(Mbit/s)",
        ["mhz"] = "(MHz)",
    };

    public static string Render(JsonElement root)
    {
        var builder = new StringBuilder();
        Write(builder, root, 0);
        return builder.ToString().TrimEnd();
    }

    private static void Write(StringBuilder builder, JsonElement element, int depth)
    {
        var indent = new string(' ', depth * 3);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!IsComposite(property.Value))
                    {
                        if (Scalar(property.Value) is { } value)
                        {
                            builder.Append(indent).Append(Label(property.Name)).Append(" : ").AppendLine(value);
                        }
                    }
                    else
                    {
                        builder.Append(indent).Append(Label(property.Name)).AppendLine(" :");
                        Write(builder, property.Value, depth + 1);
                    }
                }

                break;
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    builder.Append(indent).AppendLine("(aucun)");
                }

                foreach (var item in items)
                {
                    if (item.ValueKind == JsonValueKind.Object && !item.EnumerateObject().Any(p => IsComposite(p.Value)))
                    {
                        builder.Append(indent).Append("• ").AppendLine(Line(item));
                    }
                    else if (!IsComposite(item))
                    {
                        builder.Append(indent).Append("• ").AppendLine(Scalar(item) ?? "—");
                    }
                    else
                    {
                        Write(builder, item, depth + 1);
                    }
                }

                break;
            default:
                builder.Append(indent).AppendLine(Scalar(element) ?? "—");
                break;
        }
    }

    private static string Line(JsonElement item)
    {
        string? name = null;
        var parts = new List<string>();
        foreach (var property in item.EnumerateObject())
        {
            if (Scalar(property.Value) is not { } value)
            {
                continue;
            }

            if (property.NameEquals("nom") || property.NameEquals("name"))
            {
                name = value;
            }
            else
            {
                parts.Add($"{Label(property.Name)} : {value}");
            }
        }

        var details = string.Join(" · ", parts);
        return name is null ? details : parts.Count == 0 ? name : $"{name} — {details}";
    }

    private static bool IsComposite(JsonElement element) => element.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() is { Length: > 0 } text ? text : null,
        JsonValueKind.Number => element.GetDouble().ToString("0.##", Format.French),
        JsonValueKind.True => "oui",
        JsonValueKind.False => "non",
        _ => null,
    };

    /// <summary>« tailleGo » devient « Taille (Go) », « vitesseMbps » devient « Vitesse (Mbit/s) ».</summary>
    private static string Label(string name)
    {
        if (Labels.TryGetValue(name, out var label))
        {
            return label;
        }

        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }

            current.Append(char.ToLowerInvariant(c));
        }

        words.Add(current.ToString());
        var text = string.Join(' ', words.Select(w => Units.GetValueOrDefault(w, w)));
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }
}

/// <summary>Paramètres : seuils, notifications, rétention des données, langue (EF-IHM-06).</summary>
public sealed partial class SettingsViewModel(AgentClient client, MainViewModel main) : PageViewModel
{
    public override string Title => "Paramètres";

    public IReadOnlyList<string> Severities { get; } = ["information", "attention", "critique"];

    public string Version { get; } = $"Version {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";

    public ObservableCollection<ThresholdItem> Thresholds { get; } = [];

    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private string _minSeverity = "attention";

    [ObservableProperty]
    private decimal _retentionDays = 90;

    [ObservableProperty]
    private bool _internetProbes;

    [ObservableProperty]
    private bool _automaticCorrections = true;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleModeText))]
    private bool _simpleMode;

    /// <summary>Code saisi : nouveau code pour activer, code actuel pour désactiver ou pour les réglages avancés.</summary>
    [ObservableProperty]
    private string _technicianCode = "";

    [ObservableProperty]
    private string _technicianCodeConfirm = "";

    public string SimpleModeText => SimpleMode
        ? "Mode simplifié actif : les explications techniques sont masquées, et les corrections à risque comme les réglages avancés demandent le code du technicien."
        : "Pour le PC d'un proche peu à l'aise avec l'informatique : SoluPulse continue de surveiller et de prévenir, mais seules les corrections sans risque restent accessibles. Le technicien choisit un code de 4 à 12 chiffres.";

    public override Task RefreshAsync() => RunAsync(async () => Apply(await client.Api.GetSettingsAsync(AgentClient.Empty)));

    [RelayCommand]
    private Task ToggleSimpleModeAsync() => RunAsync(async () =>
    {
        var code = TechnicianCode.Trim();
        if (!SimpleMode && code != TechnicianCodeConfirm.Trim())
        {
            throw new FormatException("Les deux codes ne sont pas identiques.");
        }

        Apply(await client.Api.SetSimpleModeAsync(new SimpleModeRequest { Enabled = !SimpleMode, TechnicianCode = code }));
        TechnicianCode = "";
        TechnicianCodeConfirm = "";
        Message = SimpleMode ? "Mode simplifié activé. Notez le code du technicien en lieu sûr." : "Mode simplifié désactivé.";
    });

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var update = new SettingsUpdate
        {
            NotificationsEnabled = NotificationsEnabled,
            NotificationMinSeverity = MinSeverity,
            TechnicianCode = TechnicianCode.Trim(),
        };

        // En mode simplifié, sans le code du technicien, seuls les notifications sont enregistrées.
        if (SimpleMode && update.TechnicianCode.Length == 0)
        {
            Apply(await client.Api.UpdateSettingsAsync(update));
            Message = "Notifications enregistrées. Les autres réglages demandent le code du technicien.";
            return;
        }

        update.DetailRetentionDays = (int)RetentionDays;
        update.InternetProbes = InternetProbes;
        update.AutomaticCorrections = AutomaticCorrections;
        foreach (var threshold in Thresholds)
        {
            var text = threshold.OverrideText.Trim().Replace(',', '.');
            if (text.Length == 0)
            {
                update.ResetThresholds.Add(threshold.RuleId);
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                update.ThresholdOverrides[threshold.RuleId] = value;
            }
            else
            {
                throw new FormatException($"Seuil invalide pour « {threshold.Title} » : {threshold.OverrideText}");
            }
        }

        Apply(await client.Api.UpdateSettingsAsync(update));
        Message = "Paramètres enregistrés.";
    });

    private void Apply(SettingsReply settings)
    {
        main.ApplySettings(settings);
        NotificationsEnabled = settings.NotificationsEnabled;
        MinSeverity = settings.NotificationMinSeverity;
        RetentionDays = settings.DetailRetentionDays;
        InternetProbes = settings.InternetProbes;
        AutomaticCorrections = settings.AutomaticCorrections;
        SimpleMode = settings.SimpleMode;
        Thresholds.Clear();
        foreach (var threshold in settings.Thresholds)
        {
            Thresholds.Add(new ThresholdItem(threshold));
        }
    }
}
