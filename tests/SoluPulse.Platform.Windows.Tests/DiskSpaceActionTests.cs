using SoluPulse.Core.Corrections;
using SoluPulse.Platform.Windows.Actions;
using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows.Tests;

/// <summary>
/// Actions d'espace disque : seules les parties sans effet sur la machine sont testées ici (outils de
/// déplacement dans un dossier temporaire, validation des paramètres). Le déplacement réel d'un
/// dossier personnel se teste sur machine virtuelle (docs/tests-vm.md).
/// </summary>
public sealed class DiskSpaceActionTests : IDisposable
{
    private static readonly CallerIdentity Caller = new("S-1-5-21-1000-1000-1000-1001", "PC\\test");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"solupulse-espace-{Guid.NewGuid():N}");

    public DiskSpaceActionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static ActionContext Context(params (string Key, string Value)[] parameters) =>
        new(parameters.ToDictionary(p => p.Key, p => p.Value), Caller);

    [Fact]
    public void MoveTreeMovesEveryFileAndKeepsTheStructure()
    {
        var source = Path.Combine(_folder, "source");
        var target = Path.Combine(_folder, "cible");
        Directory.CreateDirectory(Path.Combine(source, "photos", "vide"));
        File.WriteAllBytes(Path.Combine(source, "a.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(source, "photos", "b.jpg"), new byte[300]);

        var (moved, bytes, skipped) = Files.MoveTree(source, target);

        Assert.Equal((2, 400L, 0), (moved, bytes, skipped));
        Assert.True(File.Exists(Path.Combine(target, "photos", "b.jpg")));
        Assert.True(Directory.Exists(Path.Combine(target, "photos", "vide")));
        Assert.Empty(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void MoveTreeNeverOverwritesNorDuplicates()
    {
        var source = Path.Combine(_folder, "source");
        var target = Path.Combine(_folder, "cible");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "meme-nom.txt"), "source");
        File.WriteAllText(Path.Combine(target, "meme-nom.txt"), "cible");
        var locked = Path.Combine(source, "ouvert.txt");
        File.WriteAllText(locked, "en cours");

        (int Moved, long Bytes, int Skipped) result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Files.MoveTree(source, target);
        }

        Assert.Equal(2, result.Skipped);
        Assert.Equal("cible", File.ReadAllText(Path.Combine(target, "meme-nom.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(source, "meme-nom.txt")));
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(target, "ouvert.txt")), "Un fichier copié mais pas supprimé ne doit pas rester en double.");
    }

    [Theory]
    [InlineData(@"%USERPROFILE%\Documents", @"C:\Users\Alice\Documents")]
    [InlineData(@"%userprofile%\Downloads", @"C:\Users\Alice\Downloads")]
    [InlineData(@"E:\Alice\Documents", @"E:\Alice\Documents")]
    public void ExpandsFolderLocationsForTheRightProfile(string raw, string expected) =>
        Assert.Equal(expected, UserFolders.Expand(raw, @"C:\Users\Alice"));

    [Theory]
    [InlineData(@"C:\Users\Alice\OneDrive\Documents", true)]
    [InlineData(@"C:\Users\Alice\OneDrive - Solutech\Bureau", true)]
    [InlineData(@"C:\Users\Alice\Documents", false)]
    public void DetectsFoldersSyncedByOneDrive(string path, bool synced) =>
        Assert.Equal(synced, UserFolders.IsSyncedByOneDrive(path));

    [Fact]
    public void MultipleChoicesAreSplitAndDeduplicated()
    {
        var context = Context(("fichiers", "aaa|bbb||aaa| ccc "));

        Assert.Equal(["aaa", "bbb", "ccc"], context.GetAll("fichiers"));
        Assert.Throws<ActionParameterException>(() => Context(("fichiers", "|")).GetAll("fichiers"));
    }

    [Fact]
    public async Task MoveFolderRejectsUnknownFoldersAndDestinations()
    {
        var action = new MoveUserFolderAction();

        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(Context(("dossier", "documents"), ("destination", @"Z:\")), default));
        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(Context(("dossier", @"C:\Windows"), ("destination", @"D:\")), default));
        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(Context(("dossier", "documents")), default));
    }

    [Fact]
    public async Task LargeFilesOnlyAcceptFilesFromTheList()
    {
        var action = new DeleteLargeFilesAction();
        var system = new ActionContext(new Dictionary<string, string> { ["fichiers"] = Files.Id(@"C:\Windows\System32\kernel32.dll") }, new CallerIdentity("S-1-5-18", "SYSTEM"));

        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(system, default));
        await Assert.ThrowsAsync<ActionParameterException>(() => action.ValidateAsync(Context(("fichiers", "0123456789abcdef")), default));
    }

    [Fact]
    public void PathIdsAreStableAndCaseInsensitive()
    {
        Assert.Equal(Files.Id(@"C:\Users\Alice\Downloads\film.mkv"), Files.Id(@"c:\users\alice\downloads\FILM.MKV"));
        Assert.Equal(16, Files.Id("x").Length);
    }

    [Fact]
    public async Task ReadOnlyEstimatesNeverThrow()
    {
        Assert.NotNull((await new ComponentStoreCleanupAction().EstimateAsync(Context(), default)).Summary);
        Assert.NotNull((await new DisableHibernationAction().EstimateAsync(Context(), default)).Summary);
        Assert.NotNull((await new CleanUpdateLeftoversAction().EstimateAsync(Context(), default)).Summary);
        Assert.NotNull((await new DockerDiskGuide().EstimateAsync(Context(), default)).Summary);
    }

    [Fact]
    public void DockerGuideNeverModifiesTheSystem()
    {
        var descriptor = new DockerDiskGuide().Descriptor;

        Assert.Equal(ActionKind.Guide, descriptor.Kind);
        Assert.False(descriptor.RequiresRestorePoint);
        Assert.False(descriptor.Automatic);
    }

    [Fact]
    public async Task DiskUsageSnapshotListsCategoriesBiggestFirst()
    {
        var result = await new DiskUsageCollector(TimeProvider.System).CollectAsync(default);

        Assert.Contains(result.Snapshots, s => s.Kind == DiskUsageCollector.CollectorName);
        Assert.Contains(result.Measurements, m => m.Metric == "occupation.temporaires_go");
    }
}
