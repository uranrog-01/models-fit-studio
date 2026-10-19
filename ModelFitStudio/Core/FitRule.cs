namespace ModelFitStudio.Core;

/// <summary>
/// The memory rule: does one file of a model fit this PC, and how well. It is the
/// same rule zer0ne code uses (engine/src/localCatalog.ts, fitMemory), with the same
/// numbers; fit-cases.json holds both apps to it.
///
/// What a file needs is its own size, the vision projector when the model has one,
/// the cache for the conversation, and the engine's own buffers. The conversation
/// length is the longest of the usual sizes that keeps the need within 85% of the
/// fast memory, else the shortest.
///
/// A PC has two limits. Fast memory is what the graphics chip can hold: the model
/// runs at full speed inside it. The ceiling is what the whole machine can hold: past
/// fast memory a model still runs, with part of it in system memory, slower.
/// </summary>
public static class FitRule
{
    public const double PerfectShare = 0.60;
    public const double GoodShare = 0.85;
    public const double TightShare = 0.98;
    /// <summary>The share of the ceiling a model may take and still be said to run.</summary>
    public const double CeilingShare = 0.92;
    public const double RuntimeGb = 0.35;
    /// <summary>An 8-bit cache next to a 16-bit one, with its bookkeeping.</summary>
    public const double LightKv = 34.0 / 64;
    /// <summary>The share of system memory a model may use at all.</summary>
    public const double SystemShare = 0.8;
    public const int LongestContext = 32768;
    /// <summary>Tokens a second a reader keeps up with: below it a version is not recommended while a faster one fits.</summary>
    public const double ReadingPace = 7;
    /// <summary>
    /// Speed kept by a model too big for a graphics chip that shares system memory.
    /// Measured on an Intel Arc 140T laptop (llama.cpp b11574, Vulkan): a 1.7B Q4_K_M
    /// wrote 41 tok/s on the chip, 31 on the processor alone and 19 to 36 split between
    /// the two; a 7B Q2_K wrote 8, 13 and 6. A split was never the fastest, so the
    /// estimate is the processor running it all.
    /// </summary>
    public const double SharedSpill = 0.75;
    private static readonly int[] Contexts = [16384, 8192, 4096];

    public const string Perfect = "perfect", Good = "good", Tight = "marginal", Slow = "slow", No = "no";

    public sealed record Limits(double FastGb, double CeilingGb);

    public sealed record Fit(string Verdict, double Util, double NeedGb, int Context)
    {
        /// <summary>Runs inside fast memory with room to spare.</summary>
        public bool Runs => Verdict is Perfect or Good;
        /// <summary>The word the page shows.</summary>
        public string Level => Verdict switch
        {
            Perfect => "Perfect",
            Good => "Good",
            Tight => "Marginal",
            Slow => "Slower",
            _ => "Too tight",
        };
    }

    /// <summary>The two limits of a PC, from its graphics memory budget and its system memory.</summary>
    public static Limits LimitsFor(double budgetGb, double totalRamGb)
    {
        var system = totalRamGb * SystemShare;
        var fast = budgetGb > 0 ? budgetGb : system * 0.6;   // no graphics memory: a comfortable share of RAM
        return new Limits(fast, Math.Max(fast, system));
    }

    /// <summary>Cache bytes per token at 16-bit, from the model's attention shape.</summary>
    public static double KvFromShape(double layers, double kvHeads, double headDim) => 4 * layers * kvHeads * headDim;

    /// <summary>
    /// Cache bytes per token at 16-bit when the shape is unknown, from the size in
    /// billions. A model with experts keeps the attention of a bigger model than the
    /// part it reads, so its size here sits between the two.
    /// </summary>
    public static double KvFromParams(double paramsB, double activeB = 0)
    {
        var eff = activeB > 0 ? Math.Sqrt(paramsB * activeB) : paramsB;
        return 16384 * Math.Pow(Math.Max(0.5, eff), 0.8);
    }

    /// <summary>The usual versions, the best trade first: what is offered when nothing reaches the pace asked for.</summary>
    public static readonly string[] UsualOrder =
    [
        "Q4_K_M", "Q4_K_S", "UD-Q4_K_XL", "IQ4_XS", "IQ4_NL", "Q4_0", "UD-TQ1_0",
        "Q5_K_M", "Q3_K_M", "UD-Q3_K_XL", "IQ3_XXS", "Q8_0", "Q6_K", "Q2_K", "MXFP4_MOE", "BF16", "F16",
    ];

    /// <summary>The place of a version in <see cref="UsualOrder"/>; a publisher's own mix ("UD-Q4_K_M") takes the place of the plain one; 98 when it is not listed.</summary>
    public static int UsualRank(string? quant)
    {
        var q = (quant ?? "").ToUpperInvariant();
        var at = Array.IndexOf(UsualOrder, q);
        if (at < 0 && q.StartsWith("UD-", StringComparison.Ordinal)) at = Array.IndexOf(UsualOrder, q[3..]);
        return at < 0 ? 98 : at;
    }

    public static bool IsUnquantised(string? quant) => (quant ?? "").ToUpperInvariant() is "BF16" or "F16" or "F32";

    /// <summary>One version as the pick sees it.</summary>
    public sealed record Build(string Quant, double SizeGb, string Verdict, double TokS, double NeedGb);

    /// <summary>
    /// The recommended version, by the rule zer0ne code uses too (pickBuild): the largest
    /// that fits with room to spare and still writes at the pace asked for (an
    /// unquantised one buys nothing a Q8 does not give). When none reaches that pace,
    /// the usual best trade. When none fits with room to spare, the one that needs the
    /// least memory. A version that does not run at all is never picked: null.
    /// </summary>
    public static Build? Pick(IReadOnlyCollection<Build> builds, double pace = ReadingPace)
    {
        var runs = builds.Where(b => b.Verdict is Perfect or Good).ToList();
        var readable = runs.Where(b => b.TokS >= pace && !IsUnquantised(b.Quant)).OrderByDescending(b => b.SizeGb).FirstOrDefault();
        if (readable is not null) return readable;
        if (runs.Count > 0) return runs.OrderBy(b => UsualRank(b.Quant)).ThenBy(b => b.SizeGb).First();
        return builds.Where(b => b.Verdict != No).OrderBy(b => b.NeedGb).FirstOrDefault();
    }

    public static Fit Rate(double fileGb, double projectorGb, double kvBytesPerToken, int maxContext, Limits limits)
    {
        var perToken = kvBytesPerToken * LightKv / 1e9;
        var fixedGb = fileGb + projectorGb + RuntimeGb;
        var max = Math.Clamp(maxContext <= 0 ? 8192 : maxContext, 1, LongestContext);
        var contexts = new[] { max }.Concat(Contexts).Where(n => n <= max).Distinct().ToArray();
        var ctx = contexts.Cast<int?>().FirstOrDefault(n => fixedGb + perToken * n!.Value <= limits.FastGb * GoodShare) ?? contexts[^1];
        var need = fixedGb + perToken * ctx;
        var util = limits.FastGb > 0 ? need / limits.FastGb : double.PositiveInfinity;
        var verdict = util <= PerfectShare ? Perfect
            : util <= GoodShare ? Good
            : util <= TightShare ? Tight
            : need <= limits.CeilingGb * CeilingShare ? Slow
            : No;
        return new Fit(verdict, util, need, ctx);
    }

    /// <summary>
    /// What a model past fast memory keeps of its speed (also said of a file that does
    /// not run, as if it could). On a chip that shares system memory: <see cref="SharedSpill"/>,
    /// measured. On a card with its own memory the part outside it is read from system
    /// memory for every token, at <paramref name="slowerBy"/> times the cost: an
    /// estimate, not measured.
    /// </summary>
    public static double SpillFactor(Fit fit, Limits limits, double slowerBy = 2, bool shared = false)
    {
        if (fit.Verdict is not (Slow or No) || fit.NeedGb <= 0) return 1;
        if (shared) return SharedSpill;
        var fast = Math.Clamp(limits.FastGb / fit.NeedGb, 0, 1);
        return 1 / (fast + (1 - fast) * Math.Max(1, slowerBy));
    }
}
