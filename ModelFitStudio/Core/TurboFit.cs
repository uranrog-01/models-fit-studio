using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// Turbo Fit: how to run a model that does not fit comfortably, as fast as this
/// machine allows, using only what llama.cpp can really do — no magic:
///
///  1. KV cache in 8-bit or 4-bit (-ctk/-ctv): a long context in half or a
///     quarter of the memory.
///  2. MoE expert offload (--cpu-moe / --n-cpu-moe): attention and shared weights
///     stay on the GPU, the big expert tables live in system RAM. A 30B-A3B model
///     reads only ~3B parameters per token, so experts in RAM cost little speed.
///  3. Partial GPU offload for dense models (llama.cpp's own --fit picks the split). Only a
///     card with memory of its own gains from it: on shared graphics memory no split is advised.
///  4. A context sized to what is left once the weights are placed.
///  5. The right quant: the largest file that fits beats a bigger one that spills.
///
/// Every number here is an estimate from file size and memory bandwidth, shown on
/// the Check page as advice. Nothing is written to disk and nothing leaves the machine.
/// </summary>
public sealed record TurboInput
{
    public required string FileName { get; init; }
    public required long FileBytes { get; init; }
    /// <summary>Total parameters in billions (0 when the name says nothing).</summary>
    public double ParamsTotalB { get; init; }
    /// <summary>Parameters read per token for an MoE, in billions (0 when dense).</summary>
    public double ParamsActiveB { get; init; }
    public bool IsMoe { get; init; }
    /// <summary>Memory models run at full speed in (GB) — the corrected VRAM budget.</summary>
    public double VramGb { get; init; }
    public double RamGb { get; init; }
    public double FreeDiskGb { get; init; }
    public int WantedCtx { get; init; } = 8192;
    public string? GpuName { get; init; }
    /// <summary>
    /// The graphics chip has no memory of its own: it reads the same system memory the
    /// processor does. Dividing a model between the two gains nothing there (measured on
    /// an Arc 140T: a split wrote slower than either alone), so no split is advised.
    /// </summary>
    public bool Shared { get; init; }
}

/// <summary>What to pass to llama-server, and why.</summary>
public sealed record TurboPlan
{
    /// <summary>gpu | gpu-kv | hybrid | ram | disk | no</summary>
    public required string Tier { get; init; }
    public required string Label { get; init; }
    /// <summary>Extra llama.cpp arguments only (MoE offload, warmup). The KV flags come from <see cref="Kv"/>, the context from <see cref="Ctx"/>.</summary>
    public required List<string> Args { get; init; }
    public int Ctx { get; init; }
    /// <summary>f16 | q8_0 | q4_0</summary>
    public required string Kv { get; init; }
    public bool Moe { get; init; }
    /// <summary>Estimated generation speed in tokens/s, before any real measurement.</summary>
    public double EstTps { get; init; }
    /// <summary>A quant that would run fully fast here, when the file is too big.</summary>
    public string? SuggestQuant { get; init; }
    public required List<string> Notes { get; init; }

    /// <summary>The flags this plan asks the server to add, for display and copying.</summary>
    public string ArgsText()
    {
        var all = new List<string>();
        if (Kv is "q8_0" or "q4_0") all.AddRange(["-ctk", Kv, "-ctv", Kv]);
        all.AddRange(Args);
        return string.Join(' ', all);
    }
}

public static class TurboFit
{
    /// <summary>The plan as the UI sees it: plain fields, so nothing has to be called to read it.</summary>
    public sealed record TurboInfo(string Tier, string Label, int Ctx, string Kv, double EstTps, string Args, string? SuggestQuant, List<string> Notes);

    public static TurboInfo Info(TurboPlan p) =>
        new(p.Tier, p.Label, p.Ctx, p.Kv, p.EstTps, p.ArgsText(), p.SuggestQuant, p.Notes);

    private const double GB = 1e9;
    /// <summary>Weights plus the runtime's own buffers must fit, not just the weights.</summary>
    private const double Overhead = 1.03;

    // bits per weight for the shipped quants. Approximate by design: the real file
    // size decides, and this only answers "which quant would fit in fast memory".
    private static readonly (string Q, double Bits)[] Quants =
        [("Q8_0", 8.5), ("Q6_K", 6.6), ("Q5_K_M", 5.7), ("Q4_K_M", 4.85), ("Q3_K_M", 3.9), ("Q2_K", 2.7)];

    // ---------------------------------------------------------------- bandwidth

    private static readonly Lazy<double?> MeasuredRam = new(MeasureRamBandwidth, isThreadSafe: true);

    /// <summary>
    /// Effective system RAM bandwidth in GB/s, measured once per process.
    ///
    /// Token generation reads the weights once per token, so this — not the CPU's
    /// spec sheet — is what a RAM-resident model reaches. Several threads copy a
    /// 16 MB buffer for ~100 ms and both the read and the write count (STREAM-copy
    /// convention): one thread cannot saturate a modern memory controller. Best of
    /// two runs, so a moment of background load does not become the answer.
    /// </summary>
    public static double RamBandwidthGbs()
    {
        var measured = MeasuredRam.Value;
        return measured is > 2 and < 4000 ? measured!.Value : 40; // dual-channel desktop default
    }

    private static double? MeasureRamBandwidth()
    {
        if (!OperatingSystem.IsWindows()) return null;
        const int bufBytes = 16 * 1024 * 1024;
        var threads = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var perThread = new double[threads, 2];
        try
        {
            Parallel.For(0, threads, i =>
            {
                // Large managed arrays stay committed until a later full GC.
                // Own native buffers for this short probe and free them before
                // returning, instead of leaving up to 512 MiB on the LOH.
                var src = IntPtr.Zero;
                var dst = IntPtr.Zero;
                try
                {
                    src = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufBytes);
                    dst = System.Runtime.InteropServices.Marshal.AllocHGlobal(bufBytes);
                    for (var page = 0; page < bufBytes; page += 4096)
                        System.Runtime.InteropServices.Marshal.WriteByte(src, page, 1);
                    CopyProbeMemory(dst, src, (UIntPtr)bufBytes);
                    for (var run = 0; run < 2; run++)
                    {
                        var sw = Stopwatch.StartNew();
                        long passes = 0;
                        while (sw.ElapsedMilliseconds < 100)
                        {
                            CopyProbeMemory(dst, src, (UIntPtr)bufBytes);
                            passes++;
                        }
                        perThread[i, run] = passes * (2.0 * bufBytes) / sw.Elapsed.TotalSeconds / 1e9;
                    }
                }
                finally
                {
                    if (dst != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(dst);
                    if (src != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(src);
                }
            });
            double best = 0;
            for (var run = 0; run < 2; run++)
            {
                var total = Enumerable.Range(0, threads).Sum(i => perThread[i, run]);
                if (total is > 2 and < 4000) best = Math.Max(best, total);
            }
            return best > 0 ? Math.Round(best, 1) : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyProbeMemory(IntPtr destination, IntPtr source, UIntPtr length);

    /// <summary>
    /// Achievable streaming bandwidth of the GPU in GB/s (the spec peak × the
    /// ~0.7 a real generation reaches), read from the adapter name. An unknown
    /// name gets a conservative 200 GB/s rather than a wildly wrong one.
    /// </summary>
    public static double GpuBandwidthGbs(string? gpuName)
    {
        var g = (gpuName ?? "").ToLowerInvariant().Replace("(r)", "").Replace("(tm)", "");
        if (g.Length == 0) return 0;
        if (Regex.IsMatch(g, @"rtx\s*50|b100|b200|h100|h200")) return 900;
        if (Regex.IsMatch(g, @"rtx\s*40[89]0|l40|a100|a6000|rtx\s*pro\s*6000")) return 700;
        if (Regex.IsMatch(g, @"rtx\s*40[567]0|rtx\s*4060")) return 400;
        if (Regex.IsMatch(g, @"rtx\s*30[89]0")) return 550;
        if (Regex.IsMatch(g, @"rtx\s*30[3-7]0|rtx\s*20\d\d|gtx\s*16\d\d|gtx\s*10\d\d")) return 300;
        if (Regex.IsMatch(g, @"rx\s*9\d{3}")) return 640;
        if (Regex.IsMatch(g, @"rx\s*7\d{3}|radeon pro")) return 500;
        if (Regex.IsMatch(g, @"rx\s*[56]\d{3}")) return 280;
        if (Regex.IsMatch(g, @"arc\s*b5[78]0|arc\s*a7[57]0")) return 450;
        if (Regex.IsMatch(g, @"arc\s*[ab]\d{3}|arc.*graphics")) return 220;
        if (Regex.IsMatch(g, @"8060s|strix halo|ai max")) return 256;
        if (Regex.IsMatch(g, @"890m|880m|780m|760m|680m|iris|uhd|integrated")) return 90;
        if (Regex.IsMatch(g, @"apple|m[1-5]\s*(max|ultra|pro)\b")) return 400;
        return 200;
    }

    // ---------------------------------------------------------------- the names

    /// <summary>
    /// Total parameters, the parameters an MoE reads per token, and whether it is
    /// one — read from the file name: "…-30B-A3B-…", "…-8x7B-…", "…-17B-16E-…".
    /// Zero means unknown; the plan then treats the file as dense.
    /// </summary>
    public static (double TotalB, double ActiveB, bool Moe) ParamsFromName(string name)
    {
        var n = name ?? "";
        double total = 0, active = 0;
        var moe = false;

        // Qwen / GLM style: 30B-A3B (total-active)
        var ma = Regex.Match(n, @"(\d+(?:\.\d+)?)b[-_.]a(\d+(?:\.\d+)?)b", RegexOptions.IgnoreCase);
        if (ma.Success)
        {
            total = double.Parse(ma.Groups[1].Value);
            active = double.Parse(ma.Groups[2].Value);
            moe = true;
        }
        else
        {
            // Mixtral style: 8x7B
            var mx = Regex.Match(n, @"(\d+)x(\d+(?:\.\d+)?)b", RegexOptions.IgnoreCase);
            if (mx.Success)
            {
                total = int.Parse(mx.Groups[1].Value) * double.Parse(mx.Groups[2].Value);
                active = 2 * double.Parse(mx.Groups[2].Value);
                moe = true;
            }
            else
            {
                // Llama 4 style: 17B-16E (active size, expert count)
                var le = Regex.Match(n, @"(\d+(?:\.\d+)?)b[-_](\d+)e\b", RegexOptions.IgnoreCase);
                if (le.Success)
                {
                    active = double.Parse(le.Groups[1].Value);
                    moe = true;
                }
                else
                {
                    var plain = Regex.Match(n, @"(?:^|[^.\d])(\d+(?:\.\d+)?)b(?![a-z])", RegexOptions.IgnoreCase);
                    if (plain.Success) total = double.Parse(plain.Groups[1].Value);
                }
            }
        }

        // gpt-oss states its active size in the card, not the name
        if (!moe && Regex.IsMatch(n, "gpt-oss", RegexOptions.IgnoreCase))
        {
            var m = Regex.Match(n, @"(\d+)b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                total = double.Parse(m.Groups[1].Value);
                active = total >= 100 ? 5.1 : 3.6;
                moe = true;
            }
        }
        // Named families that are MoE even without an "-A N B" in the file name. A distill
        // has its student's architecture ("MiMo-V2.6-Distill-Qwen-9B" is a dense 9B Qwen).
        if (!moe && total > 0 && !Regex.IsMatch(n, "distill", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(n, @"mixtral|deepseek-v[2-9]|kimi-k[2-9]|glm-4\.[5-9]|glm-[5-9]|minimax-m\d|qwen3-?coder-?480|qwen3-next|llama-?4|mimo-v2",
                RegexOptions.IgnoreCase))
        {
            moe = true;
            active = Math.Round(total * 0.1, 1);
        }
        return (total, active, moe);
    }

    // ---------------------------------------------------------------- the plan

    /// <summary>
    /// KV cache bytes per token at 16-bit. The cache holds attention keys and
    /// values, so it follows the attention shape, not the parameter count: an MoE
    /// reads few parameters but keeps the attention of a bigger model, so its
    /// dense-equivalent size sits between active and total.
    /// A GQA-era heuristic — treat it as ±40%, never as a measurement.
    /// </summary>
    public static double KvBytesPerToken(double denseEquivalentB)
    {
        return 16 * 1024 * Math.Pow(Math.Max(0.5, denseEquivalentB), 0.85);
    }

    /// <summary>The plan for one file on this machine: the tier, the context, the cache and the flags.</summary>
    public static TurboPlan Plan(TurboInput input)
    {
        var size = (double)input.FileBytes;
        var fast = input.VramGb * GB;                 // runs at full speed below this
        var ram = input.RamGb * GB;
        var ramPlusGpu = Math.Max(fast, ram * 0.8);   // runs at all below this
        var bwGpu = GpuBandwidthGbs(input.GpuName);
        var bwCpu = RamBandwidthGbs();

        var totalB = Math.Max(0, input.ParamsTotalB);
        var moe = input.IsMoe && input.ParamsActiveB > 0 && (totalB <= 0 || input.ParamsActiveB < totalB);
        var activeB = moe ? input.ParamsActiveB : totalB;
        var denseEquivalentB = moe && totalB > 0 ? Math.Sqrt(totalB * input.ParamsActiveB) : totalB;
        // bytes read per token: the active weights, scaled by the quant this file is in
        var activeBytes = activeB > 0 && totalB > 0 ? activeB * GB * (size / (totalB * GB)) : size;
        var want = Math.Clamp(input.WantedCtx <= 0 ? 8192 : input.WantedCtx, 2048, 131072);
        var kv16 = denseEquivalentB > 0 ? KvBytesPerToken(denseEquivalentB) : 96 * 1024;

        string tier, label, kv = "f16";
        var ctx = want;
        var args = new List<string>();
        var notes = new List<string>();
        double est;

        if (size * Overhead + want * kv16 <= fast)
        {
            tier = "gpu";
            label = bwGpu > 0 ? "Fits on the GPU — no Turbo needed" : "Fits in memory — no Turbo needed";
            est = Speed(bwGpu > 0 ? bwGpu : bwCpu, activeBytes, bwGpu > 0 ? 0.55 : 0.45);
        }
        else if (size * Overhead + 4096 * kv16 * 0.25 <= fast)
        {
            // Weights fit, the cache for the wanted context does not: quantise the cache only.
            (ctx, kv) = FitContext(fast - size * Overhead, kv16, want);
            tier = "gpu-kv";
            label = bwGpu > 0 ? "Fits on the GPU with a compact cache" : "Fits in memory with a compact cache";
            est = Speed(bwGpu > 0 ? bwGpu : bwCpu, activeBytes, bwGpu > 0 ? 0.55 : 0.45) * 0.97;
            notes.Add(kv == "q4_0"
                ? "A 4-bit KV cache keeps a long context on the GPU."
                : "An 8-bit KV cache: half the memory, near-identical output.");
        }
        else if (moe && size * Overhead <= ramPlusGpu && bwGpu > 0 && !input.Shared)
        {
            var onCpu = Math.Clamp((size * Overhead - fast * 0.9) / Math.Max(1, size * 0.85), 0, 1);
            args.AddRange(onCpu > 0.8
                ? ["--cpu-moe"]
                : ["--n-cpu-moe", Math.Max(1, Math.Ceiling(onCpu * 48)).ToString("0")]);
            (ctx, kv) = FitContext(Math.Max(0, ramPlusGpu - size * Overhead), kv16, want);
            tier = "hybrid";
            label = "GPU + RAM (MoE experts offloaded)";
            est = 1.0 / ((activeBytes * (1 - onCpu)) / (bwGpu * GB * 0.7) + (activeBytes * onCpu) / (bwCpu * GB * 0.55));
            notes.Add($"{activeB:0.#}B of {totalB:0.#}B parameters are read per token, so experts in RAM cost little speed.");
        }
        else if (size * Overhead <= ramPlusGpu)
        {
            (ctx, kv) = FitContext(Math.Max(0, ramPlusGpu - size * Overhead), kv16, want);
            var gpuShare = bwGpu > 0 && fast > 0 ? Math.Min(1, fast / (size * Overhead)) : 0;
            tier = "ram";
            var shared = input.Shared && bwGpu > 0;
            label = shared ? "Larger than graphics memory — runs slower" : bwGpu > 0 ? "Splits across GPU + RAM" : "Runs from RAM";
            args.Add("--no-warmup");
            if (shared)
            {
                // the same factor the fit rule uses for a build past shared graphics memory
                est = Speed(bwGpu, activeBytes, 0.55) * FitRule.SharedSpill;
                notes.Add("The graphics chip and the processor read the same memory here, so dividing the model between them gains nothing. A smaller build is the faster choice.");
            }
            else
            {
                est = 1.0 / ((activeBytes * gpuShare) / (Math.Max(1, bwGpu) * GB * 0.7)
                           + (activeBytes * (1 - gpuShare)) / (bwCpu * GB * 0.55));
                notes.Add("Dense model: every layer is read for each token, so whatever ends up in RAM sets the speed.");
                if (gpuShare < 0.99 && bwGpu > 0)
                    notes.Add($"Only about {fast / GB:0.#} GB of the weights fit in VRAM — if llama.cpp cannot split it, this loads on the CPU instead.");
            }
        }
        else if (size <= input.FreeDiskGb * GB && size <= ram * 2.5)
        {
            (ctx, kv) = FitContext(Math.Max(0, ramPlusGpu - size * Overhead), kv16, want);
            tier = "disk";
            label = "Streams from disk (very slow)";
            args.Add("--no-warmup");
            est = Math.Min(Speed(bwCpu, activeBytes, 0.45), 1.5 * GB / Math.Max(1, activeBytes));
            notes.Add("Larger than memory: Windows pages the weights in from the SSD. Usable for a test, not for work.");
        }
        else
        {
            tier = "no";
            label = "Too large for this machine";
            est = 0;
        }

        if (tier != "no" && ctx < want)
            notes.Add($"Context capped at {ctx / 1024}K — that is what fits after the weights and the cache.");

        string? suggest = null;
        if (tier is not ("gpu" or "no") && totalB > 0)
        {
            foreach (var (q, bits) in Quants)
            {
                var bytes = totalB * GB * bits / 8;
                if (bytes * Overhead <= fast * 0.85)
                {
                    suggest = q;
                    notes.Add($"{q} (~{bytes / GB:0.#} GB) would run fully fast here — the largest build that fits.");
                    break;
                }
            }
        }

        return new TurboPlan
        {
            Tier = tier,
            Label = label,
            Args = args,
            Ctx = ctx,
            Kv = tier == "gpu" ? "f16" : kv,
            Moe = moe,
            EstTps = Math.Round(est, 1),
            SuggestQuant = suggest,
            Notes = notes,
        };
    }

    /// <summary>Generation speed estimate: bandwidth over the bytes read per token, times the efficiency a real run reaches.</summary>
    private static double Speed(double bandwidthGbs, double activeBytes, double efficiency)
    {
        return activeBytes <= 0 ? 0 : bandwidthGbs * GB / activeBytes * efficiency;
    }

    /// <summary>A measured correction may bend the estimate; it may never invert or erase it.</summary>


    /// <summary>The wanted context if it fits the room left, else the largest that does, dropping to a 4-bit cache when needed.</summary>
    private static (int Ctx, string Kv) FitContext(double roomBytes, double kv16PerToken, int want)
    {
        foreach (var (name, bits) in new[] { ("q8_0", 8.0), ("q4_0", 4.0) })
        {
            var perToken = kv16PerToken * bits / 16.0;
            if (roomBytes <= 0 || perToken <= 0) continue;
            var fits = (int)(roomBytes / perToken);
            if (fits >= 4096) return (Math.Min(want, fits), name);
        }
        return (4096, "q4_0");
    }
}




