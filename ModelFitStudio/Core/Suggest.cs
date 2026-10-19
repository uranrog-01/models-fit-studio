using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>A model that could be suggested: already sized and timed for this PC.</summary>
public sealed record SuggestCandidate(
    string Repo,        // the GGUF repo to check and download from
    string Origin,      // the model's own repo, e.g. "google/gemma-4-E4B-it"
    double ParamsB,
    double Quality,     // 0–100
    double SizeGb,      // a Q4_K_M file
    double MemGb,
    double TpsLow,
    double TpsHigh,
    string Category,
    string? Released)
{
    public string Name => Origin[(Origin.LastIndexOf('/') + 1)..];
    public double Tps => SpeedEstimate.Typical(TpsLow, TpsHigh);
}

/// <summary>One suggestion and why it is there: "best", "smartest" or "fastest".</summary>
public sealed record Suggestion(string Kind, SuggestCandidate Model);

/// <summary>
/// Picks two or three models worth trying on this PC out of everything that fits:
/// the highest quality that still feels quick, the smartest one that still writes at
/// reading pace, and the fastest one that still answers well. Pure: the rows come in sized.
/// </summary>
public static class Suggest
{
    // repos that hold a different format, or a variant nobody should be sent to first
    private static readonly Regex NotPlain = new(
        @"NVFP4|MXFP4|AWQ|GPTQ|MLX|EXL[23]|W4A16|w8a8|bnb|ONNX|OpenVINO|abliterat|uncensor|heretic|distill|-RL-",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // models made for one narrow job; a general suggestion is a model to talk to
    private static readonly Regex Narrow = new(
        @"ocr|medgemma|\bmed\b|guard|embed|rerank|translat|diariz|\basr\b|\btts\b|whisper|function|extract|[-_]rag\b|[-_]tool\b|[-_]base\b|[-_]jp\b|korean|doctor|coder|code",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ChatName = new(@"instruct|chat|[-_]it\b|thinking",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // publishers whose GGUF copies are the widely used ones
    private static readonly HashSet<string> KnownMirrors =
        new(StringComparer.OrdinalIgnoreCase) { "unsloth", "bartowski", "lmstudio-community", "ggml-org" };

    /// <summary>True when a row is a general chat model with a GGUF copy from a publisher people use.</summary>
    public static bool Usable(string origin, string ggufProvider, string category, bool moe)
    {
        if (moe || NotPlain.IsMatch(origin) || Narrow.IsMatch(origin)) return false;
        var org = origin.Contains('/') ? origin[..origin.IndexOf('/')] : "";
        if (!KnownMirrors.Contains(ggufProvider) && !org.Equals(ggufProvider, StringComparison.OrdinalIgnoreCase)) return false;
        return category switch
        {
            "Chat" or "Reasoning" or "Multimodal" => true,
            "General" => ChatName.IsMatch(origin),   // the rest of "General" is mostly untuned base models
            _ => false,
        };
    }

    /// <summary>The same model under two names (an instruct and a thinking variant, a re-upload) counts once.</summary>
    public static string Family(SuggestCandidate c)
    {
        var name = Regex.Replace(c.Name.ToLowerInvariant(), @"[-_](it|instruct|chat|thinking|preview|\d{4})\b", "");
        return $"{name}|{Math.Round(c.ParamsB, 0)}";
    }

    /// <summary>Clearly faster than reading: answers arrive without waiting on each line.</summary>
    public const double ComfortPace = 12;

    /// <summary>
    /// Every model llmfit rates Perfect or Good for this PC, sized and timed the way the model
    /// list does: a Q4_K_M file of 4.85 bits a weight, the memory the list sizes it with, and
    /// the speed range from the machine's decode rate. zer0ne code builds the same rows.
    /// </summary>
    public static List<SuggestCandidate> Rows(IEnumerable<JsonElement> models, double budgetGb, double gbPerSec, string gpuClass, double calibration)
    {
        static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double? N(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var rows = new List<SuggestCandidate>();
        foreach (var m in models)
        {
            if (m.ValueKind != JsonValueKind.Object || S(m, "fit_level") is not ("Perfect" or "Good")) continue;
            if (!m.TryGetProperty("gguf_sources", out var sources) || sources.ValueKind != JsonValueKind.Array ||
                sources.GetArrayLength() == 0) continue;
            var origin = S(m, "name") ?? "";
            var repo = S(sources[0], "repo") ?? "";
            var category = S(m, "category") ?? "";
            var moe = m.TryGetProperty("is_moe", out var mo) && mo.ValueKind == JsonValueKind.True;
            if (repo.Length == 0 || !Usable(origin, S(sources[0], "provider") ?? "", category, moe)) continue;

            var paramsB = N(m, "params_b") ?? 0;
            if (paramsB <= 0) continue;
            // a Q4_K_M file: 4.85 bits per weight; memory as the list sizes it
            var sizeGb = Math.Round(paramsB * 0.606 + 0.1, 1);
            var memGb = Math.Round(sizeGb * 1.05 + 1.5, 1);
            var available = N(m, "memory_available_gb") ?? budgetGb;
            if (available > 0 && memGb > available) continue;

            var (low, high) = SpeedEstimate.Range(SpeedEstimate.RawTps(gbPerSec, sizeGb), gpuClass, calibration);
            var quality = m.TryGetProperty("score_components", out var parts) ? N(parts, "quality") ?? 0 : 0;
            rows.Add(new SuggestCandidate(repo, origin, paramsB, quality, sizeGb, memGb, low, high, category, S(m, "release_date")));
        }
        return rows;
    }

    public static List<Suggestion> Pick(IEnumerable<SuggestCandidate> all)
    {
        // one row per model: the best-rated, then the one from the model's own maker, then the newest
        var pool = all
            .GroupBy(Family)
            .Select(g => g.OrderByDescending(c => c.Quality)
                          .ThenByDescending(c => ChatName.IsMatch(c.Name))
                          .ThenByDescending(c => c.Released ?? "")
                          .First())
            .ToList();
        var picks = new List<Suggestion>();
        if (pool.Count == 0) return picks;

        var quick = pool.Where(c => c.Tps >= ComfortPace).ToList();
        var readable = pool.Where(c => c.Tps >= SpeedEstimate.ReadingPace).ToList();

        // best: the highest quality that still feels quick; on a slower PC, that reads at all;
        // on a PC where nothing does, quality counted down by how far it falls short
        var best = quick.Count > 0 ? quick.OrderByDescending(c => c.Quality).ThenByDescending(c => c.Tps).First()
            : readable.Count > 0 ? readable.OrderByDescending(c => c.Quality).ThenByDescending(c => c.Tps).First()
            : pool.OrderByDescending(c => c.Quality * Math.Min(1, c.Tps / SpeedEstimate.ReadingPace)).First();
        picks.Add(new Suggestion("best", best));

        var smartest = readable.Where(c => c != best).OrderByDescending(c => c.Quality).ThenByDescending(c => c.Tps).FirstOrDefault();
        if (smartest is not null && smartest.Quality > best.Quality) picks.Add(new Suggestion("smartest", smartest));

        var floor = Math.Max(50, best.Quality - 16);
        var fastest = pool.Where(c => c != best && c != smartest && c.Quality >= floor)
                          .OrderByDescending(c => c.Tps).ThenByDescending(c => c.Quality).FirstOrDefault();
        if (fastest is not null && fastest.Tps >= best.Tps * 1.25) picks.Add(new Suggestion("fastest", fastest));
        return picks;
    }
}
