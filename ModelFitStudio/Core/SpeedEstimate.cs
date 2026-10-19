using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// The speed the app shows is a range, corrected for the kind of GPU.
///
/// The raw estimate is bandwidth over the bytes read per token. It lands close on
/// a graphics card with its own memory and far too high on a GPU that shares
/// system RAM: on an Arc 140T laptop it said about 15 tok/s for a 9B model that
/// measured 7–10. A range is honest about that; a single number was not.
/// </summary>
public static class SpeedEstimate
{
    /// <summary>Tokens per second at which text arrives about as fast as it is read.</summary>
    public const double ReadingPace = 7;

    public const string Cpu = "cpu";
    public const string Integrated = "integrated";
    public const string Discrete = "discrete";

    /// <summary>cpu | integrated | discrete, read from the adapter name: Windows does not report shared memory reliably.</summary>
    public static string GpuClass(string? gpuName, bool hasGpu, bool unifiedMemory = false)
    {
        if (!hasGpu) return Cpu;
        if (unifiedMemory) return Integrated;
        var g = (gpuName ?? "").ToLowerInvariant().Replace("(r)", "").Replace("(tm)", "");
        // Arc 140T / 130V are built into the CPU; Arc A770 / B580 are cards (letter first)
        if (Regex.IsMatch(g, @"\barc\s*(pro\s*)?\d{3}[a-z]\b|arc\s*graphics")) return Integrated;
        if (Regex.IsMatch(g, @"iris|uhd|hd graphics|radeon\s*graphics|radeon\s*\d{3}m\b|\b\d{4}s\b|vega|adreno|integrated"))
            return Integrated;
        return Discrete;
    }

    /// <summary>
    /// What a raw estimate becomes in real use, as low and high factors. Only the
    /// integrated pair is measured (Arc 140T, llama.cpp Vulkan); the other two are
    /// a cautious band below the raw number, which real runs rarely beat.
    /// </summary>
    public static (double Low, double High) Factors(string gpuClass) => gpuClass switch
    {
        Integrated => (0.45, 0.65),
        Cpu => (0.7, 1.0),
        _ => (0.75, 1.0),
    };

    /// <summary>How far a real run sits from a calibrated estimate: other models differ from the one that was measured.</summary>
    private const double CalibratedSpread = 0.15;

    /// <summary>
    /// The range to show for a raw estimate. Zero stays zero: no estimate, no range.
    /// A calibration (measured ÷ raw, from a real run on this machine) replaces the
    /// per-class guess and narrows the range: the machine has spoken for itself.
    /// </summary>
    public static (double Low, double High) Range(double rawTps, string gpuClass, double calibration = 0)
    {
        if (!(rawTps > 0)) return (0, 0);
        var (low, high) = calibration > 0
            ? (calibration * (1 - CalibratedSpread), calibration * (1 + CalibratedSpread))
            : Factors(gpuClass);
        return (Math.Round(rawTps * low, 1), Math.Round(rawTps * high, 1));
    }

    /// <summary>
    /// One correction factor from real runs: the median of measured ÷ raw. Pairs that
    /// cannot be right (no raw estimate, or a ratio far outside what hardware does) are
    /// dropped, so one broken run does not bend every other estimate. Zero when nothing is left.
    /// </summary>
    public static double Calibration(IEnumerable<(double MeasuredTps, double RawTps)> runs)
    {
        var ratios = runs
            .Where(r => r.MeasuredTps > 0 && r.RawTps > 0)
            .Select(r => r.MeasuredTps / r.RawTps)
            .Where(x => x is >= 0.1 and <= 3)
            .OrderBy(x => x)
            .ToList();
        if (ratios.Count == 0) return 0;
        var mid = ratios.Count / 2;
        var median = ratios.Count % 2 == 1 ? ratios[mid] : (ratios[mid - 1] + ratios[mid]) / 2;
        return Math.Round(median, 3);
    }

    /// <summary>One number for places that need one (the preview's typing pace): the middle of the range.</summary>
    public static double Typical(double low, double high) => Math.Round((low + high) / 2, 1);

    /// <summary>The machine's decode rate: the median of tok/s × size over llmfit rows. The median shrugs off the rows that are off (MoE, offload).</summary>
    public static double GbPerSecFromRows(IEnumerable<JsonElement> models)
    {
        var samples = new List<double>();
        foreach (var m in models)
        {
            var tps = Number(m, "estimated_tps");
            var size = Number(m, "disk_size_gb");
            if (tps <= 0 || size < 0.5) continue;
            samples.Add(tps * size);
            if (samples.Count >= 400) break;
        }
        if (samples.Count < 5) return 0;
        samples.Sort();
        return Math.Round(samples[samples.Count / 2], 1);
    }

    /// <summary>Raw speed for a file of this size, from the machine's decode rate.</summary>
    public static double RawTps(double gbPerSec, double sizeGb)
    {
        if (gbPerSec <= 0 || sizeGb <= 0) return 0;
        // small models do not scale linearly — per-token overhead and the KV
        // cache dominate, so damp the tail instead of promising 300 tok/s
        var tps = gbPerSec / (sizeGb + 0.4);
        return Math.Round(Math.Min(220, Math.Max(1.5, tps)), 1);
    }

    private static double Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
