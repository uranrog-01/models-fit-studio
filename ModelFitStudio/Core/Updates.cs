using System.Net.Http;
using System.Text.Json;

namespace ModelFitStudio.Core;

/// <summary>
/// Is there a newer Models Fit Studio, or a newer llama.cpp? Asks GitHub's public release lists, at most
/// once a day unless asked; nothing is downloaded without the user pressing a button.
/// </summary>
public static class Updates
{
    /// <summary>Where the app is published: its releases, and the shared "Best for agents" results.</summary>
    public const string AppRepo = "uranrog-01/models-fit-studio";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ModelFitStudio");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public sealed record AppRelease(string Tag, string Url, bool Newer);

    /// <summary>The newest published release of the app; null when there is none yet or GitHub cannot be reached.</summary>
    public static async Task<AppRelease?> LatestAppAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{AppRepo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            return new AppRelease(tag, url, IsNewer(tag, currentVersion));
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="tag"/> ("v3.17.0") is a later version than <paramref name="current"/> ("3.16.0").</summary>
    public static bool IsNewer(string tag, string current) =>
        Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var a) &&
        Version.TryParse(current.TrimStart('v', 'V').Split('-')[0], out var b) &&
        a > b;
}
