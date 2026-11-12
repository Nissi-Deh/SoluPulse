using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SoluPulse.Contracts;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.ViewModels;

internal static class Format
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string Date(Google.Protobuf.WellKnownTypes.Timestamp? timestamp) =>
        timestamp is null ? "" : timestamp.ToDateTimeOffset().ToLocalTime().ToString("dddd d MMMM à HH:mm", French);

    public static string Ago(Google.Protobuf.WellKnownTypes.Timestamp? timestamp)
    {
        if (timestamp is null)
        {
            return "jamais";
        }

        var elapsed = DateTimeOffset.UtcNow - timestamp.ToDateTimeOffset();
        return elapsed.TotalSeconds switch
        {
            < 60 => "à l'instant",
            < 3600 => $"il y a {(int)elapsed.TotalMinutes} min",
            < 86400 => $"il y a {(int)elapsed.TotalHours} h",
            _ => $"il y a {(int)elapsed.TotalDays} j",
        };
    }

    /// <summary>Même normalisation que <c>MetricName.Segment</c> côté service, pour relier un composant à ses mesures.</summary>
    public static string Segment(string label)
    {
        var decomposed = label.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        var segment = builder.ToString().Trim('_');
        while (segment.Contains("__", StringComparison.Ordinal))
        {
            segment = segment.Replace("__", "_", StringComparison.Ordinal);
        }

        return segment;
    }

    public static string Duration(int seconds) => seconds switch
    {
        < 60 => "moins d'une minute",
        < 3600 => $"environ {Math.Max(1, seconds / 60)} min",
        _ => $"environ {seconds / 3600} h",
    };
}

public sealed class DomainItem(DomainStatus status)
{
    public string Label { get; } = status.Label;

    public Geometry Icon { get; } = Icons.ForDomain(status.Domain);

    /// <summary>Remplissage de la barre de score, de 0 à 100 ; vide tant que le domaine n'est pas évalué.</summary>
    public double Percent { get; } = status.Evaluated ? status.Score : 0;

    public string ScoreText { get; } = status.Evaluated ? status.Score.ToString(Format.French) : "—";

    public string StateText { get; } = !status.Evaluated
        ? "Non évalué"
        : status.ActiveAlerts == 0 ? "Aucun problème" : $"{status.ActiveAlerts} alerte{(status.ActiveAlerts > 1 ? "s" : "")} — {Palette.SeverityLabel(status.WorstSeverity).ToLower(Format.French)}";

    public IBrush Brush { get; } = new SolidColorBrush(status.Evaluated ? Palette.ForScore(status.Score) : Palette.Unknown);
}

public sealed class ActionItem(ActionInfo info)
{
    public ActionInfo Info { get; } = info;

    public string Title { get; } = info.Title;

    public string Description { get; } = info.Description;

    public string Summary { get; } = info.Guide
        ? "Recommandation à suivre vous-même"
        : $"Risque {Risk(info.Risk)} · {Format.Duration(info.EstimatedSeconds)}{(info.Reversible ? " · annulable" : "")}{(info.RequiresReboot ? " · redémarrage requis" : "")}{(info.Automatic ? " · automatique" : "")}";

    public static string Risk(string risk) => risk switch
    {
        "faible" => "faible",
        "moyen" => "moyen",
        _ => "élevé",
    };
}

public sealed class AlertItem(AlertInfo info)
{
    public AlertInfo Info { get; } = info;

    public string Id { get; } = info.Id;

    public string Title { get; } = info.Title;

    public string Subject { get; } = string.IsNullOrEmpty(info.Subject) ? "" : info.Subject.Replace('_', ' ');

    public bool HasSubject => Subject.Length > 0;

    public string SeverityLabel { get; } = Palette.SeverityLabel(info.Severity);

    public IBrush SeverityBrush { get; } = new SolidColorBrush(Palette.ForSeverity(info.Severity));

    public string Explanation { get; } = info.SimpleExplanation;

    public string Technical { get; } = info.TechnicalExplanation;

    public bool HasTechnical => Technical.Length > 0 && !SimpleMode;

    /// <summary>Mode simplifié : l'explication technique est réservée au technicien.</summary>
    public bool SimpleMode { get; init; }

    public string Cause { get; } = info.ProbableCause;

    public string OpenedText { get; } = info.Active
        ? $"Détectée {Format.Date(info.OpenedAt)}"
        : $"Résolue {Format.Date(info.ResolvedAt)}";

    public string ValueText { get; } = info.HasTriggerValue ? $"Valeur relevée : {info.TriggerValue.ToString("0.##", Format.French)}{UnitOf(info.Metric)}" : "";

    public bool Active { get; } = info.Active;

    public IReadOnlyList<ActionItem> Corrections { get; } = info.Corrections.Select(c => new ActionItem(c)).ToList();

    /// <summary>Les corrections ne sont proposées que tant que le problème est présent.</summary>
    public bool ShowCorrections => Active && Corrections.Count > 0;

    public bool ShowNoCorrection => Active && Corrections.Count == 0;

    /// <summary>Unité déduite du suffixe de la métrique (« cpu.temperature_c » → « °C »).</summary>
    public static string UnitOf(string metric)
    {
        var last = metric[(metric.LastIndexOf('.') + 1)..];
        return last switch
        {
            "tours_min" => " tr/min",
            _ when last.EndsWith("_c", StringComparison.Ordinal) => " °C",
            _ when last.EndsWith("_pct", StringComparison.Ordinal) => " %",
            _ when last.EndsWith("_ms", StringComparison.Ordinal) => " ms",
            _ when last.EndsWith("_s", StringComparison.Ordinal) => " s",
            _ when last.EndsWith("_j", StringComparison.Ordinal) || last.StartsWith("jours", StringComparison.Ordinal) || last.EndsWith("_jours", StringComparison.Ordinal) => " jours",
            _ when last.EndsWith("_go", StringComparison.Ordinal) => " Go",
            _ when last.EndsWith("_go_h", StringComparison.Ordinal) => " Go/h",
            _ when last.EndsWith("_min", StringComparison.Ordinal) => " min",
            _ when last.EndsWith("_v", StringComparison.Ordinal) => " V",
            _ => "",
        };
    }
}

public sealed class ExecutionItem(ExecutionInfo info)
{
    public ExecutionInfo Info { get; } = info;

    public string Title { get; } = info.ActionTitle;

    public string Date { get; } = Format.Date(info.StartedAt);

    public string ValidatedBy { get; } = info.Origin == "Automatique"
        ? "Lancée automatiquement par SoluPulse (action sans risque)"
        : $"Validée par {info.ValidatedBy}";

    public string Message { get; } = info.Message;

    public bool CanUndo { get; } = info.CanUndo;

    public string StateLabel { get; } = info.State switch
    {
        "Reussie" => info.Verification == "Resolue" ? "Réussie · problème résolu" : "Réussie",
        "Echouee" => "Échouée",
        "Annulee" => "Annulée",
        "EnCours" => "En cours",
        "Interrompue" => "Interrompue",
        "AnnuleeAuRedemarrage" => "Annulée après interruption",
        _ => info.State,
    };

    public IBrush StateBrush { get; } = new SolidColorBrush(info.State switch
    {
        "Reussie" => Palette.Ok,
        "Echouee" or "Interrompue" => Palette.Critique,
        _ => Palette.Unknown,
    });
}

/// <summary>Une valeur proposée par le service, cochée ou non (paramètre à choix multiples).</summary>
public sealed partial class ChoiceItem(Choice choice) : ObservableObject
{
    public Choice Choice { get; } = choice;

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// Un paramètre de la fiche. Les valeurs viennent toujours du service : une liste déroulante, ou des
/// cases à cocher quand plusieurs valeurs sont possibles (gros fichiers à supprimer…).
/// </summary>
public partial class ParameterItem(string name, bool multi = false) : ObservableObject
{
    public string Name { get; } = name;

    public bool IsMulti { get; } = multi;

    public bool IsSingle => !IsMulti;

    public string Label { get; } = name switch
    {
        "entree" => "Programme",
        "service" => "Service",
        "carte" => "Carte réseau",
        "fournisseur" => "Fournisseur DNS",
        "dossier" => "Dossier à déplacer",
        "destination" => "Disque de destination",
        "fichiers" => "Fichiers à supprimer définitivement (cochez-les)",
        _ => name,
    };

    public ObservableCollection<ChoiceItem> Options { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<Choice> _choices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value))]
    private Choice? _selected;

    /// <summary>La valeur envoyée au service, ou <c>null</c> tant que rien n'est choisi.</summary>
    public string? Value => IsMulti
        ? Options.Where(o => o.IsChecked).Select(o => o.Choice.Value).ToList() is { Count: > 0 } values ? string.Join('|', values) : null
        : Selected?.Value;

    public string CheckedSummary => Options.Count(o => o.IsChecked) is var count and > 0
        ? $"{count} fichier{(count > 1 ? "s" : "")} coché{(count > 1 ? "s" : "")} sur {Options.Count}"
        : $"Aucun fichier coché sur {Options.Count}";

    partial void OnChoicesChanged(IReadOnlyList<Choice> value)
    {
        Options.Clear();
        foreach (var choice in value)
        {
            var option = new ChoiceItem(choice);
            option.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(CheckedSummary));
            };
            Options.Add(option);
        }

        OnPropertyChanged(nameof(CheckedSummary));
    }
}

public partial class ThresholdItem(RuleThreshold threshold) : ObservableObject
{
    public string RuleId { get; } = threshold.RuleId;

    public string Title { get; } = threshold.Title;

    public string Condition { get; } = $"{threshold.Metric} {threshold.Operator} {threshold.DefaultThreshold.ToString(Format.French)} (par défaut)";

    public double Default { get; } = threshold.DefaultThreshold;

    [ObservableProperty]
    private string _overrideText = threshold.HasOverride ? threshold.Override.ToString(Format.French) : "";
}

public sealed class InventoryEntry(string title, string date, string text)
{
    public string Title { get; } = title;

    public string Date { get; } = date;

    public string Text { get; } = text;
}
