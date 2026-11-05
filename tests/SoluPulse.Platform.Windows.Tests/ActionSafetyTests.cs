using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;

namespace SoluPulse.Platform.Windows.Tests;

/// <summary>
/// Tests sans effet sur la machine : validation des paramètres, choix proposés, estimations et
/// sauvegardes. L'exécution réelle de chaque action se teste sur machine virtuelle (docs/tests-vm.md).
/// </summary>
public sealed class ActionSafetyTests : IDisposable
{
    private static readonly CallerIdentity Caller = new("S-1-5-21-1000-1000-1000-1001", "PC\\test");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"solupulse-actions-{Guid.NewGuid():N}");

    public ActionSafetyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static ActionContext Context(params (string Key, string Value)[] parameters) =>
        new(parameters.ToDictionary(p => p.Key, p => p.Value), Caller);

    [Fact]
    public void FilesHelperDeletesOnlyOldFilesAndEmptyFolders()
    {
        var old = Path.Combine(_folder, "sous-dossier", "ancien.tmp");
        var recent = Path.Combine(_folder, "recent.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllBytes(old, new byte[2048]);
        File.WriteAllBytes(recent, new byte[1024]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

        var measured = Files.Measure([_folder], TimeSpan.FromDays(1));
        var deleted = Files.Delete([_folder], TimeSpan.FromDays(1));

        Assert.Equal((1, 2048L), measured);
        Assert.Equal(1, deleted.Count);
        Assert.Equal(2048, deleted.Bytes);
        Assert.True(File.Exists(recent));
        Assert.False(Directory.Exists(Path.GetDirectoryName(old)));
    }

    [Fact]
    public void FilesHelperSkipsFilesInUse()
    {
        var locked = Path.Combine(_folder, "verrouille.tmp");
        File.WriteAllBytes(locked, new byte[10]);
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var deleted = Files.Delete([_folder]);

        Assert.Equal(1, deleted.Skipped);
        Assert.True(File.Exists(locked));
    }

    [Theory]
    [InlineData(500, "Ko")]
    [InlineData(5L * 1024 * 1024, "Mo")]
    [InlineData(3L * 1024 * 1024 * 1024, "Go")]
    public void FormatsSizesInFrench(long bytes, string unit) => Assert.EndsWith(unit, Files.Size(bytes), StringComparison.Ordinal);

    [Fact]
    public async Task SetDnsRejectsUnknownProvidersAndAdapters()
    {
        var action = new SetDnsAction();

        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(Context(("carte", "Carte inexistante"), ("fournisseur", "cloudflare")), default));
        var choices = await action.GetChoicesAsync(SetDnsAction.ProviderParameter, Caller, default);
        Assert.Equal(["automatique", "cloudflare", "google", "quad9"], choices.Select(c => c.Value).Order());
    }

    [Fact]
    public async Task SetDnsRejectsProvidersOutsideTheClosedList()
    {
        var action = new SetDnsAction();
        var adapter = (await action.GetChoicesAsync("carte", Caller, default)).FirstOrDefault();
        if (adapter is null)
        {
            return; // Machine sans carte réseau active.
        }

        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(Context(("carte", adapter.Value), ("fournisseur", "8.8.8.8; del C:\\")), default));
    }

    [Fact]
    public async Task ParameterisedActionsRejectUnknownTargets()
    {
        await Assert.ThrowsAsync<ActionParameterException>(() => new RestartAdapterAction().ValidateAsync(Context(("carte", "Carte inexistante")), default));
        await Assert.ThrowsAsync<ActionParameterException>(() => new StartServiceAction().ValidateAsync(Context(("service", "ServiceInexistant")), default));
        await Assert.ThrowsAsync<ActionParameterException>(() => new DisableStartupProgramAction().ValidateAsync(Context(("entree", "0000000000000000")), default));
    }

    [Fact]
    public async Task ParameterisedActionsRequireTheirParameter()
    {
        await Assert.ThrowsAsync<ActionParameterException>(() => new DisableStartupProgramAction().ValidateAsync(Context(), default));
        await Assert.ThrowsAsync<ActionParameterException>(() => new SetDnsAction().ValidateAsync(Context(), default));
    }

    [Fact]
    public async Task StartupChoicesOnlyListEnabledProgramsOfTheCaller()
    {
        var choices = await new DisableStartupProgramAction().GetChoicesAsync("entree", Caller, default);
        var entries = StartupInventory.List();

        Assert.All(choices, c =>
        {
            var entry = Assert.Single(entries, e => e.Id == c.Value);
            Assert.True(entry.Enabled);
            Assert.True(entry.UserSid is null || entry.UserSid == Caller.UserSid);
        });
    }

    [Fact]
    public async Task RecycleBinRejectsNonUserAccounts()
    {
        var action = new EmptyRecycleBinAction();
        var system = new ActionContext(new Dictionary<string, string>(), new CallerIdentity("S-1-5-18", "SYSTEM"));

        await Assert.ThrowsAsync<ActionParameterException>(() => action.EstimateAsync(system, default));
    }

    [Fact]
    public async Task ReadOnlyEstimatesAndBackupsWork()
    {
        var estimate = await new CleanTempAction().EstimateAsync(Context(), default);
        var powerBackup = await new BalancedPowerPlanAction().BackupAsync(Context(), default);

        Assert.StartsWith("Environ", estimate.Summary, StringComparison.Ordinal);
        Assert.True(powerBackup is null || Guid.TryParse(powerBackup, out _));
    }

    [Fact]
    public async Task WindowsUpdateResetBackupNamesTheRenamedFolders()
    {
        var backup = await new ResetWindowsUpdateAction().BackupAsync(Context(), default);

        Assert.NotNull(backup);
        Assert.Contains("SoftwareDistribution.solupulse-", backup, StringComparison.Ordinal);
        Assert.Contains("catroot2.solupulse-", backup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionsWithoutUndoSayso()
    {
        var outcome = await new FlushDnsAction().UndoAsync(Context(), "{}", default);

        Assert.False(outcome.Success);
    }

    [Fact]
    public void GuidedRecommendationsNeverModifyTheSystem()
    {
        Assert.All(HardwareGuides.All, g =>
        {
            Assert.Equal(ActionKind.Guide, g.Descriptor.Kind);
            Assert.False(g.Descriptor.RequiresRestorePoint);
        });
    }

    [Fact]
    public void RiskyActionsRequireARestorePoint()
    {
        var risky = WindowsPlatform.Actions().Where(a => a.Descriptor.Risk >= RiskLevel.Moyen).Select(a => a.Descriptor).ToList();

        Assert.NotEmpty(risky);
        Assert.All(risky, d => Assert.True(d.RequiresRestorePoint, d.Id));
    }
}
