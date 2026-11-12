using System.Text.Json;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Rules;
using SoluPulse.Platform.Windows;

// Outil de traduction : liste les textes des règles et du catalogue de corrections, tels que
// l'application les reçoit, pour compléter src/SoluPulse.Desktop/Assets/en.json.
//   ExtraireTextes <dossier rules> <sortie.json>
var texts = new SortedSet<string>(StringComparer.Ordinal);
var rules = RuleLoader.LoadDirectory(args[0], ActionIds.All);
foreach (var rule in rules.Rules)
{
    texts.Add(rule.Title);
    texts.Add(rule.ProbableCause);
    texts.Add(rule.SimpleExplanation);
    if (rule.TechnicalExplanation is { } technical)
    {
        texts.Add(technical);
    }
}

foreach (var action in WindowsPlatform.Actions())
{
    var d = action.Descriptor;
    texts.Add(d.Title);
    texts.Add(d.Description);
    texts.Add(d.Family);
    foreach (var step in d.Steps)
    {
        texts.Add(step);
    }
}

File.WriteAllText(args[1], JsonSerializer.Serialize(texts, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"{texts.Count} textes ; {rules.Errors.Count} erreurs de règles.");
