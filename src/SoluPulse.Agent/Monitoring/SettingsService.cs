using System.Globalization;
using SoluPulse.Core;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Storage;

namespace SoluPulse.Agent.Monitoring;

public sealed record AgentSettings(
    bool NotificationsEnabled,
    Severity NotificationMinSeverity,
    int DetailRetentionDays,
    bool InternetProbes,
    string Language,
    IReadOnlyDictionary<string, double> ThresholdOverrides,
    bool AutomaticCorrections = true);

/// <summary>Paramètres modifiables depuis l'application (EF-IHM-06), avec valeurs par défaut et bornes.</summary>
public sealed class SettingsService(SettingsStore store, NetworkProbeOptions networkOptions)
{
    public const int MinRetentionDays = 7;
    public const int MaxRetentionDays = 365;
    private const string ThresholdPrefix = "seuil.";
    private const string AutomaticKey = "corrections.automatiques";

    public async Task<AgentSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var values = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var overrides = values
            .Where(kv => kv.Key.StartsWith(ThresholdPrefix, StringComparison.Ordinal))
            .Select(kv => (Rule: kv.Key[ThresholdPrefix.Length..], Ok: double.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v), Value: v))
            .Where(t => t.Ok)
            .ToDictionary(t => t.Rule, t => t.Value, StringComparer.Ordinal);

        var settings = new AgentSettings(
            Bool(values, "notifications.actives", true),
            Enum.TryParse<Severity>(values.GetValueOrDefault("notifications.gravite_min"), true, out var severity) ? severity : Severity.Attention,
            Math.Clamp(int.TryParse(values.GetValueOrDefault("retention.detail_jours"), CultureInfo.InvariantCulture, out var days) ? days : 90, MinRetentionDays, MaxRetentionDays),
            Bool(values, "reseau.sondes_internet", true),
            values.GetValueOrDefault("langue") ?? "fr",
            overrides,
            Bool(values, AutomaticKey, true));
        networkOptions.InternetProbes = settings.InternetProbes;
        return settings;
    }

    public async Task UpdateAsync(
        bool? notificationsEnabled,
        Severity? minSeverity,
        int? retentionDays,
        bool? internetProbes,
        IReadOnlyDictionary<string, double> thresholds,
        IEnumerable<string> resetThresholds,
        bool? automaticCorrections = null,
        CancellationToken cancellationToken = default)
    {
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (automaticCorrections is { } automatic)
        {
            changes[AutomaticKey] = automatic ? "true" : "false";
        }

        if (notificationsEnabled is { } enabled)
        {
            changes["notifications.actives"] = enabled ? "true" : "false";
        }

        if (minSeverity is { } severity)
        {
            changes["notifications.gravite_min"] = severity.ToString();
        }

        if (retentionDays is { } days)
        {
            if (days is < MinRetentionDays or > MaxRetentionDays)
            {
                throw new ArgumentOutOfRangeException(nameof(retentionDays), $"La rétention doit être comprise entre {MinRetentionDays} et {MaxRetentionDays} jours.");
            }

            changes["retention.detail_jours"] = days.ToString(CultureInfo.InvariantCulture);
        }

        if (internetProbes is { } probes)
        {
            changes["reseau.sondes_internet"] = probes ? "true" : "false";
        }

        foreach (var (rule, value) in thresholds)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(thresholds), "Seuil invalide.");
            }

            changes[ThresholdPrefix + rule] = value.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var rule in resetThresholds)
        {
            changes[ThresholdPrefix + rule] = null;
        }

        await store.SetAsync(changes, cancellationToken).ConfigureAwait(false);
        await GetAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Bool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;
}
