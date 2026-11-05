using Dapper;
using Microsoft.Data.Sqlite;
using SoluPulse.Core;
using SoluPulse.Core.Audit;
using SoluPulse.Core.Corrections;

namespace SoluPulse.Storage.Tests;

public sealed class StorageTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"solupulse-test-{Guid.NewGuid():N}.db");
    private SqliteDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new SqliteDatabase(_path);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializationIsIdempotentAndUsesWal()
    {
        await _database.InitializeAsync();

        await using var connection = await _database.OpenAsync();
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
    }

    [Fact]
    public async Task StoresMeasurementsAndReturnsHistory()
    {
        var store = new MeasurementStore(_database);
        await store.InsertAsync(Enumerable.Range(0, 10)
            .Select(i => new Measurement(T0.AddMinutes(i), "cpu.temperature_c", 40 + i, "°C", "test"))
            .ToList());

        var history = await store.HistoryAsync("cpu.temperature_c", T0, T0.AddHours(1), maxPoints: 1000);
        var metrics = await store.ListMetricsAsync();

        Assert.Equal(10, history.Count);
        Assert.Equal(40, history[0].Value);
        var metric = Assert.Single(metrics);
        Assert.Equal(49, metric.LastValue);
        Assert.Equal("°C", metric.Unit);
    }

    [Fact]
    public async Task CompactionAggregatesOldMeasurementsHourly()
    {
        var store = new MeasurementStore(_database);
        var old = T0.AddDays(-100);
        await store.InsertAsync([
            new Measurement(old, "memoire.utilisation_pct", 20, "%", "test"),
            new Measurement(old.AddMinutes(10), "memoire.utilisation_pct", 40, "%", "test"),
            new Measurement(T0, "memoire.utilisation_pct", 50, "%", "test"),
        ]);

        var deleted = await store.CompactAsync(T0, TimeSpan.FromDays(90), TimeSpan.FromDays(730));
        var history = await store.HistoryAsync("memoire.utilisation_pct", old.AddHours(-1), T0.AddMinutes(1), maxPoints: 100_000);

        Assert.Equal(2, deleted);
        Assert.Equal(2, history.Count);
        Assert.Equal(30, history[0].Value);
        Assert.Equal(20, history[0].Min);
        Assert.Equal(40, history[0].Max);
        Assert.Equal(50, history[1].Value);
    }

    [Fact]
    public async Task OnlyOneOpenAlertPerRuleAndSubject()
    {
        var store = new AlertStore(_database);

        var first = await store.OpenAsync("HW-DISK-001", "c", HealthDomain.Materiel, Severity.Critique, 5, T0);
        var second = await store.OpenAsync("HW-DISK-001", "c", HealthDomain.Materiel, Severity.Critique, 4, T0.AddMinutes(1));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await store.ListActiveAsync());
    }

    [Fact]
    public async Task AResolvedAlertCanReopenAsANewAlert()
    {
        var store = new AlertStore(_database);
        var first = await store.OpenAsync("HW-DISK-001", "c", HealthDomain.Materiel, Severity.Critique, 5, T0);

        var resolved = await store.ResolveAsync("HW-DISK-001", "c", T0.AddHours(1));
        var reopened = await store.OpenAsync("HW-DISK-001", "c", HealthDomain.Materiel, Severity.Critique, 5, T0.AddHours(2));

        Assert.Equal(AlertState.Resolue, resolved!.State);
        Assert.NotEqual(first.Id, reopened.Id);
        Assert.Equal(AlertState.Resolue, (await store.GetAsync(first.Id))!.State);
        Assert.Equal(2, (await store.ListRecentAsync(10)).Count);
    }

    [Fact]
    public async Task JournalRoundTripsExecutions()
    {
        var journal = new ExecutionJournal(_database);
        var id = Guid.NewGuid();
        var record = new ExecutionRecord(id, ActionIds.SetDns, Guid.NewGuid(), ExecutionOrigin.Utilisateur,
            new Dictionary<string, string> { ["carte"] = "Wi-Fi", ["fournisseur"] = "cloudflare" },
            new CallerIdentity("S-1-5-21-1", "PC\\alice"), T0, T0, null, ExecutionState.EnCours, null, null,
            VerificationResult.NonApplicable, null);

        await journal.BeginAsync(record, default);
        await journal.SaveBackupAsync(id, "{\"dns\":[]}", default);
        Assert.Single(await journal.ListInProgressAsync(default));

        await journal.CompleteAsync(id, ExecutionState.Reussie, "ok", VerificationResult.Resolue, T0.AddMinutes(1), default);
        var stored = await journal.GetAsync(id, default);

        Assert.NotNull(stored);
        Assert.Equal(ExecutionState.Reussie, stored.State);
        Assert.Equal("cloudflare", stored.Parameters["fournisseur"]);
        Assert.Equal("{\"dns\":[]}", stored.Backup);
        Assert.Equal(VerificationResult.Resolue, stored.Verification);
        Assert.Empty(await journal.ListInProgressAsync(default));
    }

    [Fact]
    public async Task AuditLogDetectsTampering()
    {
        using var audit = new SqliteAuditLog(_database);
        for (var i = 0; i < 3; i++)
        {
            await audit.AppendAsync(new AuditEntry(T0.AddMinutes(i), "Execution", ActionIds.FlushDns, Guid.NewGuid(), null, "Utilisateur", "alice", T0, "Reussie", null), default);
        }

        Assert.Null(await audit.VerifyAsync(default));
        Assert.Equal(3, (await audit.ReadAllAsync(default)).Count);

        await using (var connection = await _database.OpenAsync())
        {
            await connection.ExecuteAsync("UPDATE audit SET payload_json = replace(payload_json, 'Reussie', 'Echouee') WHERE seq = 2");
        }

        Assert.Equal(2, await audit.VerifyAsync(default));
    }

    [Fact]
    public async Task SettingsInventoryAndSensorIssues()
    {
        var settings = new SettingsStore(_database);
        await settings.SetAsync(new Dictionary<string, string?> { ["notifications.actives"] = "true", ["seuil.HW-DISK-001"] = "15" });
        await settings.SetAsync(new Dictionary<string, string?> { ["seuil.HW-DISK-001"] = null });
        Assert.Equal(["notifications.actives"], (await settings.GetAllAsync()).Keys);

        var inventory = new InventoryStore(_database);
        await inventory.SaveAsync([new InventorySnapshot("demarrage", T0, "[1]")]);
        await inventory.SaveAsync([new InventorySnapshot("demarrage", T0.AddHours(1), "[1,2]")]);
        Assert.Equal("[1,2]", (await inventory.GetAsync("demarrage"))!.Json);
        Assert.Null(await inventory.GetAsync("inconnu"));

        var issues = new SensorIssueStore(_database);
        await issues.ReplaceAsync("batterie", [new SensorIssue("batterie", "capacite", "Aucune batterie", T0)]);
        await issues.ReplaceAsync("batterie", []);
        Assert.Empty(await issues.ListAsync());
    }
}
