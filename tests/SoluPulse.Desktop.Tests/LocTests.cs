using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SoluPulse.Desktop.Services;

namespace SoluPulse.Desktop.Tests;

public sealed class LocTests
{
    private static string Embedded()
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream("SoluPulse.Desktop.Assets.en.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheDictionaryIsEmbeddedAndConsistent()
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(Embedded())!;

        Assert.True(entries.Count > 1000);
        foreach (var (french, english) in entries)
        {
            var a = Regex.Matches(french, @"\{(\d+)\}").Select(m => m.Value).Order();
            var b = Regex.Matches(english, @"\{(\d+)\}").Select(m => m.Value).Order();
            Assert.True(a.SequenceEqual(b), french);
        }
    }

    [Fact]
    public void TranslatesExactTextsPatternsAndLines()
    {
        Loc.Load(Embedded());

        Assert.True(Loc.TryTranslate("Tableau de bord", out var dashboard));
        Assert.Equal("Dashboard", dashboard);
        Assert.True(Loc.TryTranslate("12 appareils trouvés, tous connus.", out var devices));
        Assert.Equal("12 devices found, all known.", devices);
        Assert.True(Loc.TryTranslate("Lecture : 1911 Mo/s · Écriture : 871 Mo/s", out var speed));
        Assert.Equal("Read: 1911 MB/s · Write: 871 MB/s", speed);
        Assert.True(Loc.TryTranslate("Disque système presque plein", out var rule));
        Assert.Equal("System disk almost full", rule);
        Assert.False(Loc.TryTranslate("Texte inconnu du dictionnaire", out var unknown));
        Assert.Equal("Texte inconnu du dictionnaire", unknown);
    }

    [Fact]
    public void ValuesCapturedByAPatternAreTranslatedWhenKnown()
    {
        Loc.Load("""{ "Risque {0} · {1}{2}": "Risk {0} · {1}{2}", "faible": "low" }""");

        Assert.True(Loc.TryTranslate("Risque faible · environ 5 min", out var text));
        Assert.StartsWith("Risk low · ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FrenchStaysTheDefault()
    {
        Assert.Equal("fr", typeof(Loc).GetProperty("Language", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        Assert.Equal("Tableau de bord", Loc.T("Tableau de bord"));
    }
}
