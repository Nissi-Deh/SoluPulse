using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
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
            Metrics.Add(new MetricChoice(metric.Metric, Labels.TryGetValue(metric.Metric, out var label) ? $"{label} ({metric.Metric})" : metric.Metric, metric.Unit));
        }

        SelectedPeriod ??= Periods[0];
        SelectedMetric = Metrics.FirstOrDefault(m => m.Metric == current) ?? Metrics.FirstOrDefault();
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

/// <summary>Inventaire relevé par les collecteurs : programmes au démarrage, services, disques…</summary>
public sealed partial class InventoryViewModel(AgentClient client) : PageViewModel
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["windows"] = "Windows",
        ["disques"] = "Disques",
        ["volumes"] = "Volumes",
        ["batterie"] = "Batterie",
        ["processus"] = "Programmes les plus gourmands",
        ["demarrage"] = "Programmes au démarrage",
        ["services_arretes"] = "Services arrêtés",
        ["peripheriques_en_erreur"] = "Périphériques en erreur",
        ["mises_a_jour"] = "Mises à jour en attente",
        ["cartes_reseau"] = "Cartes réseau",
        ["evenements_recents"] = "Événements récents",
    };

    public override string Title => "Inventaire";

    public ObservableCollection<InventoryEntry> Items { get; } = [];

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var reply = await client.Api.GetInventoryAsync(AgentClient.Empty);
        Items.Clear();
        foreach (var item in reply.Items.OrderBy(i => Titles.Keys.ToList().IndexOf(i.Kind) is var index and >= 0 ? index : 99))
        {
            string text;
            try
            {
                text = JsonSerializer.Serialize(JsonDocument.Parse(item.Json).RootElement, Indented);
            }
            catch (JsonException)
            {
                text = item.Json;
            }

            Items.Add(new InventoryEntry(Titles.GetValueOrDefault(item.Kind, item.Kind), $"Relevé {Format.Ago(item.At)}", text));
        }
    });
}

/// <summary>Paramètres : seuils, notifications, rétention des données, langue (EF-IHM-06).</summary>
public sealed partial class SettingsViewModel(AgentClient client, MainViewModel main) : PageViewModel
{
    public override string Title => "Paramètres";

    public IReadOnlyList<string> Severities { get; } = ["information", "attention", "critique"];

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
    private string _message = "";

    public override Task RefreshAsync() => RunAsync(async () => Apply(await client.Api.GetSettingsAsync(AgentClient.Empty)));

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var update = new SettingsUpdate
        {
            NotificationsEnabled = NotificationsEnabled,
            NotificationMinSeverity = MinSeverity,
            DetailRetentionDays = (int)RetentionDays,
            InternetProbes = InternetProbes,
        };
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
        Thresholds.Clear();
        foreach (var threshold in settings.Thresholds)
        {
            Thresholds.Add(new ThresholdItem(threshold));
        }
    }
}
