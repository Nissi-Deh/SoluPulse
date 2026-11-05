using NSubstitute;
using SoluPulse.Core.Corrections;

namespace SoluPulse.Core.Tests;

public sealed class CorrectionExecutorTests : IDisposable
{
    private static readonly CallerIdentity Alice = new("S-1-5-21-1-1001", "PC\\alice");
    private static readonly CallerIdentity Mallory = new("S-1-5-21-1-1002", "PC\\mallory");
    private static readonly Dictionary<string, string> NoParameters = [];

    private readonly ManualTimeProvider _time = new(Measurements.T0);
    private readonly InMemoryJournal _journal = new();
    private readonly InMemoryAudit _audit = new();
    private readonly IRestorePointService _restorePoints = Substitute.For<IRestorePointService>();
    private readonly IAlertVerifier _verifier = Substitute.For<IAlertVerifier>();
    private readonly RecordingAction _lowRisk = new(RecordingAction.Describe("ACT-LOW"));
    private readonly RecordingAction _mediumRisk = new(RecordingAction.Describe("ACT-MEDIUM", RiskLevel.Moyen));
    private readonly CorrectionExecutor _executor;

    public CorrectionExecutorTests()
    {
        _restorePoints.CreateAsync(default!, default).ReturnsForAnyArgs(new RestorePointResult(true, "ok"));
        _verifier.VerifyAsync(default, default).ReturnsForAnyArgs(VerificationResult.Resolue);
        _executor = new CorrectionExecutor(new ActionCatalog([_lowRisk, _mediumRisk]), _journal, _audit, _restorePoints, _verifier, _time);
    }

    public void Dispose() => _executor.Dispose();

    // Critère d'acceptation V1 : « Aucune action exécutée sans validation, vérifié par un test automatisé ».
    [Fact]
    public async Task NoActionRunsWithoutAPreparedAndValidatedCard()
    {
        await Assert.ThrowsAsync<CorrectionRejectedException>(() => _executor.ExecuteAsync("jeton-invente", Alice, default));

        Assert.Equal(0, _lowRisk.Executions);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task PreparingAloneDoesNotRunAnything()
    {
        await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);

        Assert.Equal(0, _lowRisk.Executions);
    }

    [Fact]
    public async Task AnotherUserCannotValidateTheCard()
    {
        var card = await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);

        await Assert.ThrowsAsync<CorrectionRejectedException>(() => _executor.ExecuteAsync(card.Token, Mallory, default));
        Assert.Equal(0, _lowRisk.Executions);
    }

    [Fact]
    public async Task AValidationCanOnlyBeUsedOnce()
    {
        var card = await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);
        await _executor.ExecuteAsync(card.Token, Alice, default);

        await Assert.ThrowsAsync<CorrectionRejectedException>(() => _executor.ExecuteAsync(card.Token, Alice, default));
        Assert.Equal(1, _lowRisk.Executions);
    }

    [Fact]
    public async Task AnExpiredValidationIsRejected()
    {
        var card = await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);
        _time.Advance(CorrectionExecutor.ValidationWindow + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<CorrectionRejectedException>(() => _executor.ExecuteAsync(card.Token, Alice, default));
        Assert.Equal(0, _lowRisk.Executions);
    }

    [Fact]
    public async Task ActionsOutsideTheCatalogDoNotExist()
    {
        await Assert.ThrowsAsync<UnknownActionException>(() => _executor.PrepareAsync("ACT-FORMAT-C", null, NoParameters, Alice, default));
    }

    [Fact]
    public async Task UnexpectedParametersAreRejected()
    {
        var parameters = new Dictionary<string, string> { ["commande"] = "del /s /q C:\\" };

        await Assert.ThrowsAsync<ActionParameterException>(() => _executor.PrepareAsync("ACT-LOW", null, parameters, Alice, default));
    }

    [Fact]
    public async Task ASuccessfulCorrectionIsBackedUpVerifiedAndAudited()
    {
        var alertId = Guid.NewGuid();
        var card = await _executor.PrepareAsync("ACT-LOW", alertId, NoParameters, Alice, default);

        var result = await _executor.ExecuteAsync(card.Token, Alice, default);

        Assert.Equal(ExecutionState.Reussie, result.State);
        Assert.Equal(VerificationResult.Resolue, result.Verification);
        var record = _journal.Records[result.ExecutionId!.Value];
        Assert.NotNull(record.Backup);
        Assert.Equal(Alice, record.ValidatedBy);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("ACT-LOW", entry.ActionId);
        Assert.Equal(Alice.UserName, entry.ValidatedBy);
        await _restorePoints.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task AMediumRiskActionCreatesARestorePointFirst()
    {
        var card = await _executor.PrepareAsync("ACT-MEDIUM", null, NoParameters, Alice, default);

        await _executor.ExecuteAsync(card.Token, Alice, default);

        await _restorePoints.ReceivedWithAnyArgs(1).CreateAsync(default!, default);
        Assert.Equal(1, _mediumRisk.Executions);
    }

    [Fact]
    public async Task AMediumRiskActionIsNotAppliedWithoutARestorePoint()
    {
        _restorePoints.CreateAsync(default!, default).ReturnsForAnyArgs(new RestorePointResult(false, "désactivé"));
        var card = await _executor.PrepareAsync("ACT-MEDIUM", null, NoParameters, Alice, default);

        var result = await _executor.ExecuteAsync(card.Token, Alice, default);

        Assert.Equal(ExecutionState.Echouee, result.State);
        Assert.Equal(0, _mediumRisk.Executions);
    }

    [Fact]
    public async Task AFailedActionIsRolledBack()
    {
        _lowRisk.Fail = true;
        var card = await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);

        var result = await _executor.ExecuteAsync(card.Token, Alice, default);

        Assert.Equal(ExecutionState.Echouee, result.State);
        Assert.Equal(1, _lowRisk.Undos);
    }

    [Fact]
    public async Task ACorrectionCanBeUndone()
    {
        var card = await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);
        var result = await _executor.ExecuteAsync(card.Token, Alice, default);

        var undo = await _executor.UndoAsync(result.ExecutionId!.Value, Alice, default);

        Assert.Equal(ExecutionState.Annulee, undo.State);
        Assert.Equal(1, _lowRisk.Undos);
        await Assert.ThrowsAsync<CorrectionRejectedException>(() => _executor.UndoAsync(result.ExecutionId!.Value, Alice, default));
    }

    [Fact]
    public async Task InterruptedCorrectionsAreRolledBackAtStartup()
    {
        var id = Guid.NewGuid();
        await _journal.BeginAsync(new ExecutionRecord(id, "ACT-LOW", null, ExecutionOrigin.Utilisateur, NoParameters, Alice, Measurements.T0, Measurements.T0, null, ExecutionState.EnCours, "{}", null, VerificationResult.NonApplicable, null), default);

        var recovered = await _executor.RecoverInterruptedAsync(default);

        Assert.Equal(1, recovered);
        Assert.Equal(ExecutionState.AnnuleeAuRedemarrage, _journal.Records[id].State);
        Assert.Equal(1, _lowRisk.Undos);
        Assert.Equal("Reprise", Assert.Single(_audit.Entries).Kind);
    }

    [Fact]
    public async Task RefusingACardDestroysItsToken()
    {
        var card = await _executor.PrepareAsync("ACT-LOW", null, NoParameters, Alice, default);

        await _executor.CancelAsync(card.Token, Alice, default);

        await Assert.ThrowsAsync<CorrectionRejectedException>(() => _executor.ExecuteAsync(card.Token, Alice, default));
        Assert.Equal("Refus", Assert.Single(_audit.Entries).Kind);
    }
}
