using System.Text.Json;

namespace SoluPulse.Core.Updates;

/// <summary>Une version publiée de SoluPulse : numéro, page, installeur MSI s'il y en a un.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? MsiUrl, bool Prerelease);

/// <summary>Lecture de la liste des versions publiées sur GitHub (API « releases »).</summary>
public static class ReleaseFeed
{
    /// <summary>La version la plus récente publiée, préversions comprises (les versions non signées en sont).</summary>
    public static ReleaseInfo? Pick(JsonElement releases)
    {
        ReleaseInfo? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
                || release.GetProperty("tag_name").GetString() is not { } tag
                || !Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                continue;
            }

            var msi = release.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray()
                    .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString()))
                    .FirstOrDefault(a => a.Name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase)).Url
                : null;
            var candidate = new ReleaseInfo(version, tag, release.GetProperty("html_url").GetString() ?? "", msi, release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
            if (best is null || candidate.Version > best.Version)
            {
                best = candidate;
            }
        }

        return best;
    }

}
