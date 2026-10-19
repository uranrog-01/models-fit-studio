using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// The shared "Best for agents" list. A result is shared by the user posting a GitHub issue the app
/// fills in (their own browser, their own account, nothing sent before they press Submit); a workflow
/// in the app's repository adds checked results to community/agents.json, which the app reads to show
/// what others measured on the same graphics chip. Pure: no network.
/// </summary>
public static partial class AgentShare
{
    public const string IssuePrefix = "[agent-score]";
    public static string BoardUrl => $"https://raw.githubusercontent.com/{Updates.AppRepo}/main/community/agents.json";

    /// <summary>What a shared result says: the machine in general terms and the scores. No names, paths or keys.</summary>
    public static JsonObject Payload(string appVersion, string gpu, string gpuClass, double ramGb, string repo, string file, double sizeGb,
        double readSeconds, double writeTps, int correct, int total, int points, bool thinking, string engine) => new()
    {
        ["v"] = 1,
        ["app"] = appVersion,
        ["gpu"] = Clip(gpu),
        ["gpuClass"] = Clip(gpuClass),
        ["ramGb"] = Math.Round(ramGb),
        ["repo"] = Clip(repo),
        ["file"] = Clip(file),
        ["sizeGb"] = Math.Round(sizeGb, 2),
        ["readSeconds"] = Math.Round(readSeconds, 1),
        ["writeTps"] = Math.Round(writeTps, 1),
        ["correct"] = correct,
        ["total"] = total,
        ["points"] = points,
        ["thinking"] = thinking,
        ["engine"] = Clip(engine),
    };

    /// <summary>The GitHub page that opens with the result filled in, for the user to post.</summary>
    public static string IssueUrl(JsonObject payload)
    {
        var title = $"{IssuePrefix} {payload["file"]} on {payload["gpu"]}: {payload["points"]} points";
        var body = "This result was measured by Models Fit Studio's \"Best for agents\" test. " +
                   "Press **Submit new issue** to add it to the shared list; it holds no personal data.\n\n" +
                   "```json\n" + payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n```\n";
        return $"https://github.com/{Updates.AppRepo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>A graphics chip's name for matching: "Intel(R) Arc(TM) 140T GPU (16GB)" and "intel arc 140t" are the same chip.</summary>
    public static string GpuKey(string? gpu)
    {
        var s = (gpu ?? "").ToLowerInvariant();
        s = Noise().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    [GeneratedRegex(@"\((r|tm)\)|®|™|\(\d+\s*gb\)|\bgpu\b|\bgraphics\b|\bnvidia\b|\bamd\b|\bgeforce\b|\bradeon\b|\bintel\b|[^a-z0-9 ]")]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>One model's shared results on one chip: the median points and how many results there are.</summary>
    public sealed record BoardRow(string File, string Repo, int Points, double ReadSeconds, int Correct, int Total, double WriteTps, int Results);

    /// <summary>The shared results for this graphics chip, best first; the median keeps one odd result from deciding.</summary>
    public static List<BoardRow> ForGpu(JsonArray? board, string? gpu, int max = 8)
    {
        var key = GpuKey(gpu);
        if (board is null || key.Length == 0) return [];
        return board.OfType<JsonObject>()
            .Where(e => GpuKey(e["gpu"]?.ToString()) == key && e["file"] is not null && e["points"] is not null)
            .GroupBy(e => e["file"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = g.ToList();
                double Med(Func<JsonObject, double> f)
                {
                    var v = rows.Select(f).OrderBy(x => x).ToList();
                    return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
                }
                return new BoardRow(g.Key, rows[0]["repo"]?.ToString() ?? "",
                    (int)Math.Round(Med(e => Num(e["points"]))), Math.Round(Med(e => Num(e["readSeconds"])), 1),
                    (int)Math.Round(Med(e => Num(e["correct"]))), (int)Math.Round(Med(e => Num(e["total"]))),
                    Math.Round(Med(e => Num(e["writeTps"])), 1), rows.Count);
            })
            .OrderByDescending(r => r.Points).ThenBy(r => r.ReadSeconds)
            .Take(max)
            .ToList();
    }

    private static double Num(JsonNode? n)
    {
        try { return n?.GetValue<double>() ?? 0; }
        catch { return double.TryParse(n?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0; }
    }

    private static string Clip(string s) => s.Length > 120 ? s[..120] : s;
}
