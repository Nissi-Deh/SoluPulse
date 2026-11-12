using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoluPulse.Core.Updates;
using SoluPulse.Platform.Windows.Collectors;

namespace SoluPulse.Platform.Windows.Tests;

[Collection("Listes de détection")]
public sealed class ThreatListTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        ThreatLists.Reset();
        _key.Dispose();
    }

    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    private (byte[] Content, string Signature) Signed(ThreatListData data)
    {
        var content = Encoding.UTF8.GetBytes(ThreatLists.Serialize(data));
        return (content, Convert.ToBase64String(_key.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }

    private static ThreatListData Newer() => ThreatLists.Builtin with
    {
        Version = 2,
        Software = [.. ThreatLists.Builtin.Software, new SoftwareEntry("Notepad++", "8.8.2", "faille de l'installeur (CVE-2025-49144)")],
    };

    [Fact]
    public void ASignedNewerListIsAppliedAndUsedByTheDetection()
    {
        var (content, signature) = Signed(Newer());

        Assert.True(ThreatLists.TryApply(content, signature, PublicKey, out _));
        Assert.Equal(2, ThreatLists.Current.Version);
        Assert.Contains(VulnerableSoftware.Find([new("Notepad++ (64-bit x64)", "8.7.0", "Don Ho")]), f => f.StartsWith("Notepad++", StringComparison.Ordinal));
    }

    [Fact]
    public void AModifiedListIsRefused()
    {
        var (content, signature) = Signed(Newer());
        content[^5] ^= 1;

        Assert.False(ThreatLists.TryApply(content, signature, PublicKey, out var reason));
        Assert.Equal("signature invalide", reason);
        Assert.Equal(1, ThreatLists.Current.Version);
    }

    [Fact]
    public void AListSignedByAnotherKeyIsRefused()
    {
        var (content, signature) = Signed(Newer());

        Assert.False(ThreatLists.TryApply(content, signature, ThreatLists.PublicKeyPem, out _));
    }

    [Fact]
    public void AnOlderOrInvalidListIsRefused()
    {
        var (old, oldSignature) = Signed(ThreatLists.Builtin);
        var (bad, badSignature) = Signed(Newer() with { Software = [new SoftwareEntry("X", "pas une version", "faille")] });

        Assert.False(ThreatLists.TryApply(old, oldSignature, PublicKey, out var reason));
        Assert.Equal("pas plus récente que la liste en service", reason);
        Assert.False(ThreatLists.TryApply(bad, badSignature, PublicKey, out _));
    }

    [Fact]
    public void ThePublishedListIsSignedWithTheEmbeddedKey()
    {
        var folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "data")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        Assert.NotNull(folder);
        var file = Path.Combine(folder, "data", ThreatLists.FileName);
        Assert.True(ThreatLists.Verify(File.ReadAllBytes(file), File.ReadAllText(file + ".sig"), ThreatLists.PublicKeyPem));
    }

    [Fact]
    public void PicksTheNewestPublishedVersionWithItsInstaller()
    {
        using var releases = JsonDocument.Parse("""
            [
              { "tag_name": "v1.2.0", "draft": true, "prerelease": false, "html_url": "https://github.com/x/r/1.2.0", "assets": [] },
              { "tag_name": "v1.1.0", "draft": false, "prerelease": true, "html_url": "https://github.com/x/r/1.1.0",
                "assets": [ { "name": "SoluPulse-Setup-1.1.0.exe", "browser_download_url": "https://x/setup.exe" },
                            { "name": "SoluPulse-1.1.0-x64.msi", "browser_download_url": "https://x/1.1.0.msi" } ] },
              { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "html_url": "https://github.com/x/r/1.0.0", "assets": [] }
            ]
            """);

        var latest = ReleaseFeed.Pick(releases.RootElement)!;

        Assert.Equal(new Version(1, 1, 0), latest.Version);
        Assert.Equal("https://x/1.1.0.msi", latest.MsiUrl);
        Assert.True(latest.Prerelease);
    }
}
