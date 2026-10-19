using CoreTests.TestOnly;
using ModelFitStudio.Core;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests;

/// <summary>
/// Turbo Fit decides what to pass to llama.cpp for a file on a given machine. The
/// planner is pure, so every case here is a machine this test runner is not: an
/// Arc iGPU laptop, a 24 GB desktop card, an 8 GB card, a machine too small.
/// Tier, context and flags are asserted; the tok/s estimate is a bandwidth model
/// (±), so it is only checked for being plausible.
/// </summary>
public class TurboFitTests
{
    private readonly ITestOutputHelper _log;
    private const string T = nameof(TurboFitTests);

    public TurboFitTests(ITestOutputHelper log) => _log = log;

    private static TurboInput Input(string fileName, double sizeGb, double vramGb, double ramGb,
        int ctx = 8192, double diskGb = 500, string? gpu = "NVIDIA GeForce RTX 4090", bool shared = false)
    {
        var (total, active, moe) = TurboFit.ParamsFromName(fileName);
        return new TurboInput
        {
            FileName = fileName,
            FileBytes = (long)(sizeGb * 1e9),
            ParamsTotalB = total,
            ParamsActiveB = active,
            IsMoe = moe,
            VramGb = vramGb,
            RamGb = ramGb,
            FreeDiskGb = diskGb,
            WantedCtx = ctx,
            GpuName = gpu,
            Shared = shared,
        };
    }

    [Theory]
    [InlineData("Qwen3-30B-A3B-Instruct-Q4_K_M.gguf", 30, 3, true)]
    [InlineData("Mixtral-8x7B-Instruct-v0.1.Q4_K_M.gguf", 56, 14, true)]
    [InlineData("gpt-oss-20b-Q4_K_M.gguf", 20, 3.6, true)]
    [InlineData("DeepSeek-V3-671B-Q4_K_M.gguf", 671, 67.1, true)]
    [InlineData("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", 8, 0, false)]
    [InlineData("gemma-4-12b-it-Q4_K_M.gguf", 12, 0, false)]
    // a distill has its student's architecture, whatever family name it carries
    [InlineData("MiMo-V2.6-Distill-Qwen-9B-IQ4_XS.gguf", 9, 0, false)]
    public void ParamsFromName_Reads_Total_Active_And_Moe(string name, double total, double active, bool moe)
    {
        var (t, a, m) = TurboFit.ParamsFromName(name);
        var observed = $"{name} -> total={t} active={a} moe={m}";
        Obs.Log($"{T}.ParamsFromName", observed);
        _log.WriteLine(observed);
        Assert.Equal(total, t, 3);
        Assert.Equal(active, a, 3);
        Assert.Equal(moe, m);
    }

    [Fact]
    public void A_Small_Dense_Model_Fits_On_A_24GB_Card_With_No_Extra_Flags()
    {
        var plan = TurboFit.Plan(Input("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", 4.85, 24, 64));
        var observed = $"{plan.Tier} | {plan.Label} | kv={plan.Kv} | ctx={plan.Ctx} | " +
                       $"args=[{plan.ArgsText()}] | est={plan.EstTps}";
        Obs.Log($"{T}.SmallDense", observed);
        _log.WriteLine(observed);

        Assert.Equal("gpu", plan.Tier);
        Assert.Equal("f16", plan.Kv);
        Assert.Empty(plan.Args);
        Assert.Equal(8192, plan.Ctx);
        Assert.True(plan.EstTps > 0, "a model that fits still needs a speed estimate");
    }

    [Fact]
    public void A_30B_A3B_On_A_Shared_iGPU_Gets_No_Split_Advice()
    {
        // An Arc iGPU sharing 16 GB has no second pool of memory: its "VRAM" is the same
        // RAM the processor reads. Measured there, a model divided between chip and
        // processor wrote slower than on either alone, so no expert offload is advised.
        var plan = TurboFit.Plan(Input("Qwen3-30B-A3B-Instruct-Q4_K_M.gguf", 18.2, 15.36, 32,
            gpu: "Intel(R) Arc(TM) 140V GPU (16GB)", shared: true));
        var observed = $"{plan.Tier} | {plan.Label} | args=[{plan.ArgsText()}] | est={plan.EstTps} | notes={string.Join(" / ", plan.Notes)}";
        Obs.Log($"{T}.SharedNoSplit", observed);
        _log.WriteLine(observed);

        Assert.Equal("ram", plan.Tier);
        Assert.Contains("runs slower", plan.Label);
        Assert.DoesNotContain("GPU + RAM", plan.Label);
        Assert.DoesNotContain(plan.Args, a => a == "--cpu-moe" || a == "--n-cpu-moe");
        Assert.Contains(plan.Notes, n => n.Contains("smaller build"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("VRAM"));
        Assert.True(plan.EstTps > 0);

        // the same file with nothing shared keeps the split a card with its own memory gains from
        var own = TurboFit.Plan(Input("Qwen3-30B-A3B-Instruct-Q4_K_M.gguf", 18.2, 15.36, 32));
        Assert.Equal("hybrid", own.Tier);
        Assert.True(plan.EstTps < own.EstTps, "past shared memory is never estimated faster than a real split");
    }

    [Fact]
    public void A_30B_A3B_On_A_16GB_Card_Offloads_Experts_To_RAM()
    {
        // ~18.2 GB of Q4_K_M weights on a card with 16 GB of its own: the MoE's experts
        // go to system RAM and only ~3B parameters are read per token.
        var plan = TurboFit.Plan(Input("Qwen3-30B-A3B-Instruct-Q4_K_M.gguf", 18.2, 15.36, 32,
            gpu: "NVIDIA GeForce RTX 4080"));
        var observed = $"{plan.Tier} | {plan.Label} | kv={plan.Kv} | ctx={plan.Ctx} | " +
                       $"args=[{plan.ArgsText()}] | est={plan.EstTps}";
        Obs.Log($"{T}.MoeOffload", observed);
        _log.WriteLine(observed);

        Assert.Equal("hybrid", plan.Tier);
        Assert.True(plan.Moe, "the plan has to know it is an MoE to offload experts");
        Assert.Contains(plan.Args, a => a == "--cpu-moe" || a == "--n-cpu-moe");
        Assert.True(plan.Kv is "q8_0" or "q4_0", $"a compact cache is expected, got {plan.Kv}");
        Assert.True(plan.Ctx >= 4096, $"context must stay usable, got {plan.Ctx}");
    }

    [Fact]
    public void A_Long_Context_Quantises_Only_The_Cache_When_The_Weights_Fit()
    {
        var plan = TurboFit.Plan(Input("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", 4.85, 8, 32, ctx: 65536));
        var observed = $"{plan.Tier} | kv={plan.Kv} | ctx={plan.Ctx} | args=[{plan.ArgsText()}]";
        Obs.Log($"{T}.KvOnly", observed);
        _log.WriteLine(observed);

        Assert.Equal("gpu-kv", plan.Tier);
        Assert.Equal("q8_0", plan.Kv);
        Assert.InRange(plan.Ctx, 4096, 65536);
        Assert.Contains("-ctk", plan.ArgsText());
    }

    [Fact]
    public void A_Model_Larger_Than_Memory_And_Disk_Is_Called_Out_As_Not_Running()
    {
        var plan = TurboFit.Plan(Input("Qwen2.5-72B-Instruct-Q8_0.gguf", 77, 2, 8, diskGb: 20));
        var observed = $"{plan.Tier} | {plan.Label} | est={plan.EstTps}";
        Obs.Log($"{T}.TooLarge", observed);
        _log.WriteLine(observed);

        Assert.Equal("no", plan.Tier);
        Assert.Equal(0, plan.EstTps);
    }

    [Fact]
    public void KvBytesPerToken_Is_Between_The_Active_And_Total_Shapes_For_An_Moe()
    {
        var dense = TurboFit.KvBytesPerToken(30);
        var effective = TurboFit.KvBytesPerToken(Math.Sqrt(30 * 3));
        var small = TurboFit.KvBytesPerToken(3);
        var observed = $"active={small:0} effective={effective:0} total={dense:0} bytes/token";
        Obs.Log($"{T}.KvShape", observed);
        _log.WriteLine(observed);

        Assert.True(small < effective && effective < dense,
            "an MoE keeps attention between its active and total size, so its cache sits between them");
    }

    [Fact]
    public void A_Quant_That_Would_Fit_Is_Suggested_When_The_File_Does_Not()
    {
        var plan = TurboFit.Plan(Input("Qwen3-30B-A3B-Instruct-Q8_0.gguf", 31.9, 15.36, 32,
            gpu: "Intel(R) Arc(TM) 140V GPU (16GB)"));
        var observed = $"{plan.Tier} | suggest={plan.SuggestQuant} | notes={string.Join(" / ", plan.Notes)}";
        Obs.Log($"{T}.SuggestQuant", observed);
        _log.WriteLine(observed);

        Assert.NotEqual("gpu", plan.Tier);
        Assert.NotNull(plan.SuggestQuant);
        Assert.Contains(plan.Notes, n => n.Contains(plan.SuggestQuant!, StringComparison.OrdinalIgnoreCase));
    }
}
