using Google.Protobuf.WellKnownTypes;
using SoluPulse.Agent.Monitoring;
using SoluPulse.Contracts;
using SoluPulse.Core;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Rules;
using SoluPulse.Core.Scoring;
using Alert = SoluPulse.Core.Alert;

namespace SoluPulse.Agent.Monitoring;

/// <summary>Conversion du modèle métier vers les messages du contrat gRPC.</summary>
public sealed class AlertMapper(ActionCatalog catalog)
{
    public static string DomainKey(HealthDomain domain) => domain switch
    {
        HealthDomain.Materiel => "materiel",
        HealthDomain.Systeme => "systeme",
        HealthDomain.Securite => "securite",
        _ => "reseau",
    };

    public static string DomainLabel(HealthDomain domain) => domain switch
    {
        HealthDomain.Materiel => "Matériel",
        HealthDomain.Systeme => "Système et logiciels",
        HealthDomain.Securite => "Sécurité",
        _ => "Réseau",
    };

    public static string SeverityKey(Severity? severity) => severity switch
    {
        Severity.Information => "information",
        Severity.Attention => "attention",
        Severity.Critique => "critique",
        _ => "",
    };

    public static Severity? ParseSeverity(string? key) => key switch
    {
        "information" => Severity.Information,
        "attention" => Severity.Attention,
        "critique" => Severity.Critique,
        _ => null,
    };

    public static string RiskKey(RiskLevel risk) => risk switch
    {
        RiskLevel.Faible => "faible",
        RiskLevel.Moyen => "moyen",
        _ => "eleve",
    };

    public static Timestamp Time(DateTimeOffset value) => Timestamp.FromDateTimeOffset(value);

    public ActionInfo Action(ActionDescriptor descriptor)
    {
        var info = new ActionInfo
        {
            Id = descriptor.Id,
            Family = descriptor.Family,
            Title = descriptor.Title,
            Description = descriptor.Description,
            Risk = RiskKey(descriptor.Risk),
            Reversible = descriptor.Reversible,
            RequiresReboot = descriptor.RequiresReboot,
            EstimatedSeconds = (int)descriptor.EstimatedDuration.TotalSeconds,
            Guide = descriptor.Kind == ActionKind.Guide,
            RestorePoint = descriptor.RequiresRestorePoint,
        };
        info.Steps.AddRange(descriptor.Steps);
        info.ParameterNames.AddRange(descriptor.ParameterNames ?? []);
        return info;
    }

    public AlertInfo Alert(Alert alert, Rule? rule)
    {
        var info = new AlertInfo
        {
            Id = alert.Id.ToString(),
            RuleId = alert.RuleId,
            Subject = alert.Subject,
            Domain = DomainKey(alert.Domain),
            Severity = SeverityKey(alert.Severity),
            Active = alert.State == AlertState.Active,
            Title = rule?.Title ?? alert.RuleId,
            SimpleExplanation = rule?.SimpleExplanation ?? "Cette règle n'existe plus dans la base de règles.",
            TechnicalExplanation = rule?.TechnicalExplanation ?? "",
            ProbableCause = rule?.ProbableCause ?? "",
            OpenedAt = Time(alert.OpenedAt),
            Metric = rule is null ? "" : Bind(rule.PrimaryCondition.Metric, alert.Subject),
        };
        if (alert.ResolvedAt is { } resolved)
        {
            info.ResolvedAt = Time(resolved);
        }

        if (alert.TriggerValue is { } value)
        {
            info.TriggerValue = value;
        }

        if (rule is not null)
        {
            info.Corrections.AddRange(rule.Corrections.Where(catalog.Contains).Select(id => Action(catalog.Get(id).Descriptor)));
        }

        return info;
    }

    public StatusReply Status(HealthReport report, IReadOnlyList<Alert> active, IReadOnlyList<SensorIssue> issues, AgentState state)
    {
        var reply = new StatusReply
        {
            GlobalScore = report.Global.Value,
            ActiveAlerts = active.Count,
            WorstSeverity = SeverityKey(active.Count == 0 ? null : active.Max(a => a.Severity)),
            DiagnosticRunning = state.DiagnosticRunning,
            AgentVersion = typeof(AlertMapper).Assembly.GetName().Version?.ToString(3) ?? "",
        };
        if (state.LastCollection is { } last)
        {
            reply.LastCollection = Time(last);
        }

        reply.Domains.AddRange(report.Domains.Select(d => new DomainStatus
        {
            Domain = DomainKey(d.Domain),
            Label = DomainLabel(d.Domain),
            Evaluated = d.Evaluated,
            Score = d.Score.Value,
            ActiveAlerts = d.ActiveAlerts,
            WorstSeverity = SeverityKey(d.WorstSeverity),
        }));
        reply.SensorIssues.AddRange(issues.Select(i => new SensorIssueInfo { Collector = i.Collector, Sensor = i.Sensor, Message = i.Message }));
        reply.RuleErrors.AddRange(state.RuleErrors.Select(e => e.ToString()));
        return reply;
    }

    public ExecutionInfo Execution(ExecutionRecord record)
    {
        var info = new ExecutionInfo
        {
            Id = record.Id.ToString(),
            ActionId = record.ActionId,
            ActionTitle = catalog.Contains(record.ActionId) ? catalog.Get(record.ActionId).Descriptor.Title : record.ActionId,
            AlertId = record.AlertId?.ToString() ?? "",
            Origin = record.Origin.ToString(),
            ValidatedBy = record.ValidatedBy.UserName,
            ValidatedAt = Time(record.ValidatedAt),
            StartedAt = Time(record.StartedAt),
            State = record.State.ToString(),
            Message = record.Message ?? "",
            Verification = record.Verification.ToString(),
            CanUndo = record.State == ExecutionState.Reussie && record.Backup is not null && record.UndoneAt is null,
        };
        if (record.FinishedAt is { } finished)
        {
            info.FinishedAt = Time(finished);
        }

        return info;
    }

    private static string Bind(string pattern, string subject)
    {
        if (subject.Length == 0 || !pattern.Contains('*', StringComparison.Ordinal))
        {
            return pattern;
        }

        var captures = subject.Split('.');
        var index = 0;
        return string.Join('.', pattern.Split('.').Select(p => p == "*" && index < captures.Length ? captures[index++] : p));
    }
}
