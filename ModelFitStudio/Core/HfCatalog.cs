using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

public sealed record CatalogEntry(
    string RepoId,
    string? Pipeline,
    long Downloads,
    int Likes,
    double? ParamsB,
    double EstSizeGb,
    string EstFit,      // good | marginal | small (only a smaller version fits) | slow (runs past fast memory) | tight | est
    string EstFitText,
    double MemGb,       // estimated memory for the build the fit is about; 0 when unknown
    double TpsLow,      // estimated speed range for that build; 0 when it does not fit or is unknown
    double TpsHigh,
    string Format = "gguf");

/// <summary>
/// The one model list: a searchable view of Hugging Face, sized against this
/// machine. One cached API call serves a search with the "fits this PC" filter on
/// or off. Each result carries a fit and speed estimate for a Q4-class build (or a
/// smaller one, when only that fits), read from the parameter count — the real
/// verdict still comes from the full scan behind the card's Check button.
/// </summary>
public static class HfCatalog
{
    // a Q4_K_M file stores 4.85 bits a weight; the cache and the engine's buffers are
    // added by the shared rule (FitRule), the same one the full check uses
    private const double BytesPerParam = 4.85 / 8;
    // the smallest builds worth running (Q2-class) store about 0.42: when the Q4 one is
    // over budget and this one is not, Check will find a version that runs, so the card says so
    private const double SmallBytesPerParam = 0.42;
    // asked for in one request, so the filter has enough left to show after it drops what does not fit
    private const int FetchLimit = 100;

    /// <param name="gbPerSec">This machine's raw decode rate (<see cref="MachineSpeed"/>); 0 leaves the speed out.</param>
    public static async Task<(List<CatalogEntry> Items, string? Error)> SearchAsync(
        string query, string sort, string formatFilter, int limit, HardwareInfo hw, double gbPerSec,
        bool fitsOnly, CancellationToken ct = default, bool refresh = false)
    {
        var q = (query ?? "").Trim();
        var fmt = (formatFilter ?? "gguf").ToLowerInvariant();
        // "expand" replaces the default fields, so every field the list reads is named;
        // gguf carries the real parameter count, which a repo name often does not
        var url = $"api/models?search={Uri.EscapeDataString(q)}&limit={FetchLimit}&sort={Uri.EscapeDataString(sort)}&direction=-1" +
                  "&expand[]=gguf&expand[]=downloads&expand[]=likes&expand[]=pipeline_tag";
        if (fmt == "gguf") url += "&filter=gguf";
        else if (fmt == "mlx") url += "&filter=mlx";

        // one network request per (query, sort, filter) combination, off the
        // request budget entirely for six hours — unless the user hits Refresh,
        // which skips the cache and fetches the list live
        var key = $"models {q}|{sort}|{fmt}|{FetchLimit}";
        var (body, status) = await HfClient.FetchCachedJsonAsync(key, url, TimeSpan.FromHours(6), ct,
            bypassCache: refresh);
        if (body is null)
            return ([], status == 429
                ? "Hugging Face is rate-limiting this machine (HTTP 429). Searches repeat straight from cache in a minute."
                : $"Hugging Face answered HTTP {status} for the search.");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ([], "Unexpected response from Hugging Face.");

            var items = new List<CatalogEntry>();
            var calibration = Measurements.Calibration(hw.GpuName);
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                var id = m.Str("id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var isMlx = id.Contains("mlx", StringComparison.OrdinalIgnoreCase) || fmt == "mlx";
                var ggufParams = m.TryGetProperty("gguf", out var gg) && gg.Num("total") is > 0 and var t ? t / 1e9 : (double?)null;
                var entry = Size(id, m.Str("pipeline_tag"), (long)(m.Num("downloads") ?? 0), (int)(m.Num("likes") ?? 0),
                    ggufParams, isMlx ? "mlx" : "gguf", hw, gbPerSec, calibration);
                if (fitsOnly && entry.EstFit is not ("good" or "marginal" or "small" or "slow")) continue;
                items.Add(entry);
                if (items.Count >= limit) break;
            }
            return (items, null);
        }
        catch (Exception ex)
        {
            return ([], $"Could not read the search: {ex.Message}");
        }
    }

    /// <summary>One list entry sized against a machine: the fit, and the speed range when it fits.</summary>
    public static CatalogEntry Size(string repoId, string? pipeline, long downloads, int likes, double? ggufParamsB,
        string format, HardwareInfo hw, double gbPerSec, double calibration = 0)
    {
        var paramsB = Params(repoId, ggufParamsB);
        // an MoE reads only its active experts per token, so the bytes read are a share of the file
        var (totalB, activeB, moe) = TurboFit.ParamsFromName(HfClient.BaseModelName(repoId));
        var limits = FitRule.LimitsFor(hw.BudgetGb, hw.TotalRamGb);
        (double Gb, FitRule.Fit Fit)? Build(double bytesPerParam)
        {
            if (paramsB is not > 0) return null;
            var gb = paramsB.Value * bytesPerParam;
            return (gb, FitRule.Rate(gb, 0, FitRule.KvFromParams(paramsB.Value, moe ? activeB : 0), FitRule.LongestContext, limits));
        }
        var q4 = Build(BytesPerParam);
        var small = Build(SmallBytesPerParam);
        // the usual build when it runs inside fast memory; else a smaller one that does;
        // else whichever still runs with part of it in system memory
        var (fit, shown) = q4 is null ? ("est", q4)
            : q4.Value.Fit.Runs ? ("good", q4)
            : q4.Value.Fit.Verdict == FitRule.Tight ? ("marginal", q4)
            : small!.Value.Fit.Verdict is FitRule.Perfect or FitRule.Good or FitRule.Tight ? ("small", small)
            : q4.Value.Fit.Verdict == FitRule.Slow ? ("slow", q4)
            : small.Value.Fit.Verdict == FitRule.Slow ? ("slow", small)
            : ("tight", q4);
        var estGb = shown?.Gb ?? 0.0;
        var memGb = shown?.Fit.NeedGb ?? 0.0;
        var text = fit switch
        {
            "est" => "size unknown — run a check",
            "good" => $"≈{memGb:0.#} GB — fits",
            "marginal" => $"≈{memGb:0.#} GB — very tight",
            "small" => $"≈{memGb:0.#} GB as a smaller version",
            "slow" => $"≈{memGb:0.#} GB — runs slower, partly from system memory",
            _ => $"≈{memGb:0.#} GB — over budget",
        };

        (double Low, double High) speed = (0, 0);
        // tok/s only means something for a model that writes text, not for a detector or a voice
        var writesText = pipeline is null or "text-generation" or "text2text-generation" or "any-to-any"
                         || pipeline.EndsWith("text-to-text", StringComparison.Ordinal);
        if (fit is "good" or "marginal" or "small" or "slow" && format == "gguf" && writesText)
        {
            var readGb = moe && activeB > 0 && totalB > activeB ? estGb * activeB / totalB : estGb;
            var raw = MachineSpeed.RawTps(gbPerSec, readGb) * FitRule.SpillFactor(shown!.Value.Fit, limits, 2, hw.GpuClass == SpeedEstimate.Integrated);
            speed = SpeedEstimate.Range(raw, hw.GpuClass, calibration);
        }
        return new CatalogEntry(repoId, pipeline, downloads, likes, paramsB, estGb, fit, text, Math.Round(memGb, 1), speed.Low, speed.High, format);
    }

    /// <summary>
    /// The parameter count in billions. The GGUF header is exact, but in some repos it
    /// describes a side file (a vision projector, a draft head), so when the name states
    /// a size and the header is far from it, the name — the author's own — wins.
    /// </summary>
    public static double? Params(string repoId, double? ggufParamsB)
    {
        var named = ParamsFromName(repoId);
        if (ggufParamsB is not > 0) return named;
        if (named is > 0 && (ggufParamsB < named * 0.5 || ggufParamsB > named * 2)) return named;
        return Math.Round(ggufParamsB.Value, 2);
    }

    /// <summary>Parameter count from the repo name ("Qwen2.5-Coder-3B-GGUF" → 3). Mirrors hide the original org, so the prefix goes first.</summary>
    private static double? ParamsFromName(string repoId)
    {
        var name = HfClient.BaseModelName(repoId);
        var m = Regex.Match(name, @"(\d+(?:[.,]\d+)?)\s?[bB](?![\w])");
        if (!m.Success) return null;
        var num = double.TryParse(m.Groups[1].Value.Replace(",", "."), out var b) ? b : 0;
        return num > 0 && num < 20_000 ? num : null;
    }
}
