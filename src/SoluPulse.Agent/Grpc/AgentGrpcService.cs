using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using SoluPulse.Agent.Monitoring;
using SoluPulse.Agent.Security;
using SoluPulse.Contracts;
using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Grpc;

/// <summary>
/// API locale du service. Chaque appel authentifie l'utilisateur Windows de l'application cliente ;
/// les corrections passent exclusivement par l'exécuteur et son jeton de validation.
/// </summary>
internal sealed class AgentGrpcService(
    CallerAuthenticator authenticator,
    AgentState state,
    DiagnosticRunner runner,
    CorrectionExecutor executor,
    ActionCatalog catalog,
    AlertStore alerts,
    MeasurementStore measurements,
    InventoryStore inventory,
    ExecutionJournal journal,
    SqliteAuditLog audit,
    SettingsService settings,
    SnapshotService snapshots,
    EventHub events,
    AlertMapper mapper,
    IHostApplicationLifetime lifetime,
    ILogger<AgentGrpcService> logger) : AgentService.AgentServiceBase
{
    public override Task<StatusReply> GetStatus(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        return runner.StatusAsync(context.CancellationToken);
    }

    public override async Task<AlertList> ListAlerts(ListAlertsRequest request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var list = request.IncludeResolved
            ? await alerts.ListRecentAsync(Math.Clamp(request.Limit == 0 ? 100 : request.Limit, 1, 500), context.CancellationToken)
            : await alerts.ListActiveAsync(context.CancellationToken);
        var reply = new AlertList();
        reply.Alerts.AddRange(list.Select(a => mapper.Alert(a, state.Engine.FindRule(a.RuleId))));
        return reply;
    }

    public override async Task<AlertInfo> GetAlert(AlertRequest request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var alert = Guid.TryParse(request.Id, out var id) ? await alerts.GetAsync(id, context.CancellationToken) : null;
        return alert is null
            ? throw new RpcException(new Status(StatusCode.NotFound, "Alerte introuvable."))
            : mapper.Alert(alert, state.Engine.FindRule(alert.RuleId));
    }

    public override async Task<StatusReply> RunDiagnostic(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        await runner.RunFullDiagnosticAsync(context.CancellationToken);
        return await runner.StatusAsync(context.CancellationToken);
    }

    public override Task<ActionList> ListActions(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var reply = new ActionList();
        reply.Actions.AddRange(catalog.Descriptors.Select(mapper.Action));
        return Task.FromResult(reply);
    }

    public override async Task<ChoicesReply> GetActionChoices(ChoicesRequest request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        var action = Guard(() => catalog.Get(request.ActionId));
        if (action is not IActionParameterChoices source || !(action.Descriptor.ParameterNames ?? []).Contains(request.Parameter))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Paramètre inconnu pour cette action."));
        }

        var choices = await source.GetChoicesAsync(request.Parameter, caller, context.CancellationToken);
        var reply = new ChoicesReply();
        reply.Choices.AddRange(choices.Select(c => new Choice { Value = c.Value, Label = c.Label, Detail = c.Detail ?? "" }));
        return reply;
    }

    public override async Task<CorrectionCard> PrepareCorrection(PrepareRequest request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        Guid? alertId = Guid.TryParse(request.AlertId, out var id) ? id : null;
        var parameters = request.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var prepared = await GuardAsync(() => executor.PrepareAsync(request.ActionId, alertId, parameters, caller, context.CancellationToken));
        var card = new CorrectionCard
        {
            Token = prepared.Token,
            Action = mapper.Action(prepared.Action),
            Estimate = prepared.Estimate.Summary ?? "",
            ExpiresAt = AlertMapper.Time(prepared.ExpiresAt),
        };
        foreach (var (key, value) in prepared.Parameters)
        {
            card.Parameters[key] = value;
        }

        return card;
    }

    public override async Task<CorrectionReply> ConfirmCorrection(TokenRequest request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        logger.LogInformation("Validation de correction reçue de {User}.", caller.UserName);
        var result = await GuardAsync(() => executor.ExecuteAsync(request.Token, caller, context.CancellationToken));
        await runner.EvaluateAsync(context.CancellationToken);
        if (result.State == ExecutionState.Reussie && result.ExecutionId is { } executionId
            && await journal.GetAsync(executionId, context.CancellationToken) is { } record)
        {
            runner.RefreshDiskUsageAfter(record.ActionId, lifetime.ApplicationStopping);
        }

        return Reply(result);
    }

    public override async Task<Empty> CancelCorrection(TokenRequest request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        await executor.CancelAsync(request.Token, caller, context.CancellationToken);
        return new Empty();
    }

    public override async Task<CorrectionReply> UndoCorrection(UndoRequest request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        if (!Guid.TryParse(request.ExecutionId, out var id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Identifiant invalide."));
        }

        var result = await GuardAsync(() => executor.UndoAsync(id, caller, context.CancellationToken));
        return Reply(result);
    }

    public override async Task<ExecutionList> ListExecutions(ListRequest request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var records = await journal.ListRecentAsync(Math.Clamp(request.Limit == 0 ? 50 : request.Limit, 1, 500), context.CancellationToken);
        var reply = new ExecutionList();
        reply.Executions.AddRange(records.Select(mapper.Execution));
        return reply;
    }

    public override Task<MetricList> GetLiveMetrics(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var reply = new MetricList();
        reply.Metrics.AddRange(state.Latest().Values.OrderBy(m => m.Metric, StringComparer.Ordinal).Select(m => new Contracts.MetricInfo
        {
            Metric = m.Metric,
            Unit = m.Unit,
            LastValue = m.Value,
            LastSeen = AlertMapper.Time(m.Timestamp),
        }));
        return Task.FromResult(reply);
    }

    public override async Task<MetricList> ListMetrics(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var reply = new MetricList();
        reply.Metrics.AddRange((await measurements.ListMetricsAsync(context.CancellationToken)).Select(m => new Contracts.MetricInfo
        {
            Metric = m.Metric,
            Unit = m.Unit,
            LastValue = m.LastValue,
            LastSeen = AlertMapper.Time(m.LastSeen),
        }));
        return reply;
    }

    public override async Task<HistoryReply> GetHistory(HistoryRequest request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var to = request.To?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var from = request.From?.ToDateTimeOffset() ?? to.AddDays(-1);
        var points = await measurements.HistoryAsync(request.Metric, from, to, Math.Clamp(request.MaxPoints == 0 ? 300 : request.MaxPoints, 10, 2000), context.CancellationToken);
        var reply = new HistoryReply { Metric = request.Metric };
        reply.Points.AddRange(points.Select(p => new Contracts.HistoryPoint { At = AlertMapper.Time(p.Timestamp), Value = p.Value, Min = p.Min, Max = p.Max }));
        return reply;
    }

    public override async Task<InventoryReply> GetInventory(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var reply = new InventoryReply();
        reply.Items.AddRange((await inventory.ListAsync(context.CancellationToken)).Select(i => new InventoryItem { Kind = i.Kind, At = AlertMapper.Time(i.Timestamp), Json = i.Json }));
        return reply;
    }

    public override async Task<SettingsReply> GetSettings(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        return await SettingsReplyAsync(context.CancellationToken);
    }

    public override async Task<SettingsReply> UpdateSettings(SettingsUpdate request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        var unknown = request.ThresholdOverrides.Keys.Concat(request.ResetThresholds).Where(id => state.Engine.FindRule(id) is null).ToList();
        if (unknown.Count > 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Règles inconnues : {string.Join(", ", unknown)}"));
        }

        await GuardAsync(() => settings.UpdateAsync(
            request.HasNotificationsEnabled ? request.NotificationsEnabled : null,
            request.HasNotificationMinSeverity ? AlertMapper.ParseSeverity(request.NotificationMinSeverity) : null,
            request.HasDetailRetentionDays ? request.DetailRetentionDays : null,
            request.HasInternetProbes ? request.InternetProbes : null,
            request.ThresholdOverrides,
            request.ResetThresholds,
            request.HasAutomaticCorrections ? request.AutomaticCorrections : null,
            request.HasWeeklySnapshots ? request.WeeklySnapshots : null,
            context.CancellationToken));
        logger.LogInformation("Paramètres modifiés par {User}.", caller.UserName);
        await runner.EvaluateAsync(context.CancellationToken);
        return await SettingsReplyAsync(context.CancellationToken);
    }

    public override async Task<SnapshotList> ListSnapshots(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        return await SnapshotListAsync(context.CancellationToken);
    }

    public override async Task<SnapshotReply> CreateSnapshot(Empty request, ServerCallContext context)
    {
        var caller = authenticator.Authenticate(context);
        var result = await snapshots.CreateManualAsync(caller, context.CancellationToken);
        return new SnapshotReply
        {
            Success = result.Success,
            Message = result.Success ? "Capture créée : vous pourrez revenir à cet état du système." : result.Message,
            List = await SnapshotListAsync(context.CancellationToken),
        };
    }

    private async Task<SnapshotList> SnapshotListAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken);
        var points = await Task.Run(RestorePointService.List, cancellationToken);
        var reply = new SnapshotList
        {
            Protection = RestorePointService.Protection() switch
            {
                SystemProtection.Active => "active",
                SystemProtection.BloqueeParStrategie => "bloquee",
                _ => "desactivee",
            },
            WeeklyEnabled = current.WeeklySnapshots,
        };
        reply.Snapshots.AddRange(points.Select(p => new SnapshotInfo
        {
            Sequence = p.Sequence,
            Description = p.Description,
            CreatedAt = AlertMapper.Time(p.CreatedAt),
            Origin = RestorePointService.Origin(p.Description),
        }));
        return reply;
    }

    public override async Task<AuditReply> VerifyAudit(Empty request, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var broken = await audit.VerifyAsync(context.CancellationToken);
        var entries = await audit.ReadAllAsync(context.CancellationToken);
        return new AuditReply { Intact = broken is null, Entries = entries.Count, FirstBroken = broken ?? 0 };
    }

    public override async Task WatchEvents(Empty request, IServerStreamWriter<AgentEvent> responseStream, ServerCallContext context)
    {
        authenticator.Authenticate(context);
        var (id, reader) = events.Subscribe();

        // Le flux se ferme aussi à l'arrêt du service : sinon Kestrel attend la fin du délai d'arrêt
        // et le service est tué par Windows pendant une mise à jour.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        try
        {
            await responseStream.WriteAsync(new AgentEvent { Kind = "etat", Status = await runner.StatusAsync(stop.Token) });
            await foreach (var agentEvent in reader.ReadAllAsync(stop.Token))
            {
                await responseStream.WriteAsync(agentEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // L'application s'est déconnectée ou le service s'arrête.
        }
        finally
        {
            events.Unsubscribe(id);
        }
    }

    private async Task<SettingsReply> SettingsReplyAsync(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken);
        var reply = new SettingsReply
        {
            NotificationsEnabled = current.NotificationsEnabled,
            NotificationMinSeverity = AlertMapper.SeverityKey(current.NotificationMinSeverity),
            DetailRetentionDays = current.DetailRetentionDays,
            InternetProbes = current.InternetProbes,
            Language = current.Language,
            AutomaticCorrections = current.AutomaticCorrections,
            WeeklySnapshots = current.WeeklySnapshots,
        };
        reply.Thresholds.AddRange(state.Engine.Rules.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r =>
        {
            var threshold = new RuleThreshold
            {
                RuleId = r.Id,
                Title = r.Title,
                Metric = r.PrimaryCondition.Metric,
                Operator = Core.Rules.RuleCondition.Symbol(r.PrimaryCondition.Operator),
                DefaultThreshold = r.PrimaryCondition.Threshold,
            };
            if (current.ThresholdOverrides.TryGetValue(r.Id, out var value))
            {
                threshold.Override = value;
            }

            return threshold;
        }));
        return reply;
    }

    private CorrectionReply Reply(CorrectionResult result)
    {
        var requiresReboot = false;
        if (result.ExecutionId is { } id && journal.GetAsync(id, CancellationToken.None).GetAwaiter().GetResult() is { } record && catalog.Contains(record.ActionId))
        {
            requiresReboot = catalog.Get(record.ActionId).Descriptor.RequiresReboot && result.State == ExecutionState.Reussie;
        }

        return new CorrectionReply
        {
            ExecutionId = result.ExecutionId?.ToString() ?? "",
            State = result.State.ToString(),
            Message = result.Message,
            Verification = result.Verification.ToString(),
            RequiresReboot = requiresReboot,
        };
    }

    private static T Guard<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ToRpc(ex) is { } rpc)
        {
            throw rpc;
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ToRpc(ex) is { } rpc)
        {
            throw rpc;
        }
    }

    private static async Task GuardAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex) when (ToRpc(ex) is { } rpc)
        {
            throw rpc;
        }
    }

    private static RpcException? ToRpc(Exception ex) => ex switch
    {
        UnknownActionException => new RpcException(new Status(StatusCode.NotFound, ex.Message)),
        ActionParameterException or ArgumentException => new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)),
        CorrectionRejectedException => new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)),
        _ => null,
    };
}
