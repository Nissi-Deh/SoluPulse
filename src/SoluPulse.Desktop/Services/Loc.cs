using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;

namespace SoluPulse.Desktop.Services;

/// <summary>
/// Langue de l'interface. Les textes de SoluPulse sont écrits en français ; en anglais, chaque texte
/// affiché est remplacé par sa traduction au moment de l'affichage, qu'il vienne de l'interface, des
/// règles, des corrections ou du service. Les textes qui contiennent des nombres se traduisent par
/// modèles (« {0} appareils trouvés »). Un texte sans traduction reste en français.
/// </summary>
public static partial class Loc
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<(Regex Pattern, string Template)> Patterns = [];

    public static string Language { get; private set; } = "fr";

    public static bool English => Language == "en";

    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("fr-FR");

    private static string LanguageFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoluPulse", "langue.txt");

    /// <summary>Au démarrage : la langue choisie, puis la traduction de chaque texte affiché.</summary>
    public static void Initialize()
    {
        try
        {
            Language = File.Exists(LanguageFile) && File.ReadAllText(LanguageFile).Trim() == "en" ? "en" : "fr";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Language = "fr";
        }

        if (!English)
        {
            return;
        }

        Load();
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (e.NewValue is string text && TryTranslate(text, out var translated))
            {
                block.SetCurrentValue(TextBlock.TextProperty, translated);
            }
        });
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.NewValue is string title && TryTranslate(title, out var translated))
            {
                window.SetCurrentValue(Window.TitleProperty, translated);
            }
        });
    }

    /// <summary>La langue s'applique au prochain démarrage de l'application.</summary>
    public static void Save(string language)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
        File.WriteAllText(LanguageFile, language == "en" ? "en" : "fr");
    }

    public static string T(string french) => English && TryTranslate(french, out var english) ? english : french;

    internal static bool TryTranslate(string french, out string english)
    {
        english = french;
        if (french.Length == 0)
        {
            return false;
        }

        if (Exact.TryGetValue(french, out var exact))
        {
            english = exact;
            return true;
        }

        // Textes de plusieurs lignes (messages de correction suivis d'une vérification) : ligne par ligne.
        if (french.Contains('\n', StringComparison.Ordinal))
        {
            var lines = french.Split('\n').Select(l => T(l)).ToArray();
            english = string.Join('\n', lines);
            return english != french;
        }

        foreach (var (pattern, template) in Patterns)
        {
            var match = pattern.Match(french);
            if (match.Success)
            {
                english = Placeholder().Replace(template, m =>
                {
                    var value = match.Groups[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1].Value;
                    return Exact.GetValueOrDefault(value, value);
                });
                return true;
            }
        }

        return false;
    }

    /// <summary>Dictionnaire intégré : une clé avec {0}, {1}… est un modèle.</summary>
    internal static void Load(string? json = null)
    {
        Exact.Clear();
        Patterns.Clear();
        if (json is null)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoluPulse.Desktop.Assets.en.json");
            if (stream is null)
            {
                return;
            }

            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        foreach (var (french, english) in entries.Where(e => e.Value.Length > 0))
        {
            if (Placeholder().IsMatch(french))
            {
                var pattern = "^" + Placeholder().Replace(Regex.Escape(french).Replace(@"\{", "{", StringComparison.Ordinal), "(.+?)") + "$";
                Patterns.Add((new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), english));
            }
            else
            {
                Exact[french] = english;
            }
        }

        // Les modèles les plus précis (le plus de texte fixe) d'abord.
        Patterns.Sort((a, b) => b.Pattern.ToString().Length.CompareTo(a.Pattern.ToString().Length));
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
