using System.Net.Http;
using System.Text.Json;

namespace DejaVu;

/// <summary>
/// User-initiated update check — the app never phones home on its own, and the README
/// says so. One request to the GitHub releases API, newest stable vX.Y.Z tag wins.
/// </summary>
internal static class UpdateCheck
{
    public const string Repo = "blancodagoat/DejaVu";

    public static Version Current
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return Normalize(v);
        }
    }

    /// <summary>The newest released version above <paramref name="current"/>, with its
    /// release-page URL, or null when up to date.</summary>
    public static async Task<(Version Version, string Url)?> FindNewer(Version current)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Name);
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=10");
        return ParseNewest(json, current);
    }

    /// <summary>Pure parsing half, kept separate so the tests never touch the network.</summary>
    public static (Version Version, string Url)? ParseNewest(string releasesJson, Version current)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        (Version Version, string Url)? best = null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                continue;
            }

            var tag = release.GetProperty("tag_name").GetString();
            if (tag is null || tag.Length < 2 || tag[0] != 'v'
                || !Version.TryParse(tag[1..], out var version))
            {
                continue;  // the rolling "latest" tag lands here
            }

            version = Normalize(version);
            if (version > Normalize(current) && (best is null || version > best.Value.Version))
            {
                var url = release.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                best = (version, url ?? $"https://github.com/{Repo}/releases");
            }
        }

        return best;
    }

    /// <summary>Missing components compare as -1 in <see cref="Version"/>, so 1.0.0 and
    /// 1.0.0.0 would disagree; everything is flattened to three parts.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}
