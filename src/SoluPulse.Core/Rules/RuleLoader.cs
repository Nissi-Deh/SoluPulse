using System.Globalization;
using System.Text.RegularExpressions;
using SoluPulse.Core.Collection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SoluPulse.Core.Rules;

public sealed record RuleError(string File, string? RuleId, string Message)
{
    public override string ToString() => RuleId is null ? $"{File} : {Message}" : $"{File} [{RuleId}] : {Message}";
}

public sealed record RuleSet(IReadOnlyList<Rule> Rules, IReadOnlyList<RuleError> Errors);

/// <summary>
/// Charge les règles depuis des fichiers YAML externes, sans recompiler (EF-DIA-01).
/// Le désérialiseur est strict : une clé inconnue est une erreur, pour qu'une faute de frappe
/// ne désactive pas silencieusement une condition.
/// </summary>
public static partial class RuleLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static RuleSet LoadDirectory(string directory, IReadOnlySet<string> knownActions)
    {
        if (!Directory.Exists(directory))
        {
            return new RuleSet([], [new RuleError(directory, null, "Dossier de règles introuvable.")]);
        }

        var files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase);
        return LoadFiles(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))), knownActions);
    }

    public static RuleSet LoadFiles(IEnumerable<(string Name, string Content)> files, IReadOnlySet<string> knownActions)
    {
        var rules = new List<Rule>();
        var errors = new List<RuleError>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, content) in files)
        {
            foreach (var (dto, error) in ParseFile(name, content))
            {
                if (error is not null)
                {
                    errors.Add(error);
                    continue;
                }

                var ruleErrors = new List<RuleError>();
                var rule = Convert(name, dto!, knownActions, ruleErrors);
                if (rule is not null && !ids.Add(rule.Id))
                {
                    ruleErrors.Add(new RuleError(name, rule.Id, "Identifiant déjà utilisé par une autre règle."));
                }

                if (ruleErrors.Count > 0)
                {
                    errors.AddRange(ruleErrors);
                }
                else if (rule is not null)
                {
                    rules.Add(rule);
                }
            }
        }

        return new RuleSet(rules, errors);
    }

    private static IEnumerable<(RuleDto? Rule, RuleError? Error)> ParseFile(string name, string content)
    {
        List<RuleDto> dtos;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(content));
            var isList = stream.Documents.Count > 0
                && stream.Documents[0].RootNode is YamlMappingNode root
                && root.Children.ContainsKey(new YamlScalarNode("regles"));

            dtos = isList
                ? Deserializer.Deserialize<RuleFileDto>(content).Regles ?? []
                : [Deserializer.Deserialize<RuleDto>(content)];
        }
        catch (YamlException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return [(null, new RuleError(name, null, $"YAML invalide (ligne {ex.Start.Line}) : {detail}"))];
        }

        return dtos.Select(d => ((RuleDto?)d, (RuleError?)null));
    }

    private static Rule? Convert(string file, RuleDto dto, IReadOnlySet<string> knownActions, List<RuleError> errors)
    {
        void Error(string message) => errors.Add(new RuleError(file, dto.Id, message));

        void Require(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Error($"Champ « {field} » obligatoire.");
            }
        }

        if (string.IsNullOrWhiteSpace(dto.Id) || !IdPattern().IsMatch(dto.Id))
        {
            Error("Champ « id » manquant ou invalide (attendu : majuscules, chiffres et tirets, ex. HW-DISK-001).");
        }

        var domain = dto.Domaine?.Trim().ToLowerInvariant() switch
        {
            "materiel" or "matériel" => HealthDomain.Materiel,
            "systeme" or "système" => HealthDomain.Systeme,
            "securite" or "sécurité" => HealthDomain.Securite,
            "reseau" or "réseau" => HealthDomain.Reseau,
            _ => (HealthDomain?)null,
        };
        if (domain is null)
        {
            Error("Champ « domaine » invalide (materiel, systeme, securite ou reseau).");
        }

        var severity = dto.Gravite?.Trim().ToLowerInvariant() switch
        {
            "information" => Severity.Information,
            "attention" => Severity.Attention,
            "critique" => Severity.Critique,
            _ => (Severity?)null,
        };
        if (severity is null)
        {
            Error("Champ « gravite » invalide (information, attention ou critique).");
        }

        Require(dto.Titre, "titre");
        Require(dto.CauseProbable, "cause_probable");
        Require(dto.ExplicationSimple, "explication_simple");

        var mode = ConditionMode.Tous;
        List<ConditionDto> conditionDtos = [];
        if (dto.Condition is not null && dto.Conditions is not null)
        {
            Error("Utilisez « condition » ou « conditions », pas les deux.");
        }
        else if (dto.Condition is not null)
        {
            conditionDtos = [dto.Condition];
        }
        else if (dto.Conditions is { } group)
        {
            if ((group.Tous is null) == (group.AuMoinsUn is null))
            {
                Error("« conditions » doit contenir soit « tous », soit « au_moins_un ».");
            }
            else
            {
                mode = group.Tous is not null ? ConditionMode.Tous : ConditionMode.AuMoinsUn;
                conditionDtos = group.Tous ?? group.AuMoinsUn!;
            }
        }
        else
        {
            Error("Aucune condition définie.");
        }

        var conditions = new List<RuleCondition>();
        var minDuration = TimeSpan.Zero;
        foreach (var c in conditionDtos)
        {
            if (string.IsNullOrWhiteSpace(c.Metrique))
            {
                Error("Condition sans « metrique ».");
                continue;
            }

            var op = ParseOperator(c.Operateur);
            if (op is null)
            {
                Error($"Opérateur invalide pour {c.Metrique} (attendu : <, <=, >, >=, ==, !=).");
                continue;
            }

            if (c.Seuil is not { } threshold || !double.IsFinite(threshold))
            {
                Error($"Seuil manquant ou invalide pour {c.Metrique}.");
                continue;
            }

            if (c.Hysteresis is < 0)
            {
                Error($"L'hystérésis de {c.Metrique} doit être positive.");
                continue;
            }

            if (c.DureeMin is not null)
            {
                if (!TryParseDuration(c.DureeMin, out var duration))
                {
                    Error($"Durée « {c.DureeMin} » invalide (exemples : 30s, 10m, 2h, 1j).");
                    continue;
                }

                minDuration = duration > minDuration ? duration : minDuration;
            }

            conditions.Add(new RuleCondition(MetricName.Normalize(c.Metrique), op.Value, threshold, c.Hysteresis ?? 0));
        }

        var corrections = dto.Corrections ?? [];
        foreach (var action in corrections.Where(a => !knownActions.Contains(a)))
        {
            Error($"Correction « {action} » absente du catalogue d'actions.");
        }

        if (errors.Count > 0 || conditions.Count == 0)
        {
            return null;
        }

        return new Rule(
            dto.Id!,
            dto.Version ?? 1,
            domain!.Value,
            dto.Titre!.Trim(),
            severity!.Value,
            mode,
            conditions,
            minDuration,
            dto.CauseProbable!.Trim(),
            dto.ExplicationSimple!.Trim(),
            string.IsNullOrWhiteSpace(dto.ExplicationTechnique) ? null : dto.ExplicationTechnique.Trim(),
            corrections,
            file);
    }

    private static ComparisonOperator? ParseOperator(string? text) => text?.Trim() switch
    {
        "<" => ComparisonOperator.LessThan,
        "<=" => ComparisonOperator.LessThanOrEqual,
        ">" => ComparisonOperator.GreaterThan,
        ">=" => ComparisonOperator.GreaterThanOrEqual,
        "==" => ComparisonOperator.Equal,
        "!=" => ComparisonOperator.NotEqual,
        _ => null,
    };

    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var match = DurationPattern().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        duration = match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" or "min" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
        return true;
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*(-[A-Z0-9]+)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(s|min|m|h|j|d)$")]
    private static partial Regex DurationPattern();

    private sealed class RuleFileDto
    {
        public List<RuleDto>? Regles { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public int? Version { get; set; }
        public string? Domaine { get; set; }
        public string? Titre { get; set; }
        public string? Gravite { get; set; }
        public ConditionDto? Condition { get; set; }
        public ConditionGroupDto? Conditions { get; set; }
        public string? CauseProbable { get; set; }
        public string? ExplicationSimple { get; set; }
        public string? ExplicationTechnique { get; set; }
        public List<string>? Corrections { get; set; }
    }

    private sealed class ConditionGroupDto
    {
        public List<ConditionDto>? Tous { get; set; }
        public List<ConditionDto>? AuMoinsUn { get; set; }
    }

    private sealed class ConditionDto
    {
        public string? Metrique { get; set; }
        public string? Operateur { get; set; }
        public double? Seuil { get; set; }
        public string? DureeMin { get; set; }
        public double? Hysteresis { get; set; }
    }
}
