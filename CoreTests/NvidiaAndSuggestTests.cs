using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// Which engine an NVIDIA card gets, and which models are suggested. Neither can be
/// tried on the PC this was written on (it has no NVIDIA card), so the decisions are
/// pinned here.
/// </summary>
public class NvidiaEngineTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", true)]
    [InlineData("GeForce GTX 1060 6GB", true)]
    [InlineData("NVIDIA RTX A2000", true)]
    [InlineData("Quadro P2000", true)]
    [InlineData("Intel(R) Arc(TM) 140T GPU (16GB)", false)]
    [InlineData("AMD Radeon RX 7800 XT", false)]
    [InlineData("No GPU detected", false)]
    [InlineData("", false)]
    public void Knows_an_nvidia_card_by_its_name(string name, bool expected) =>
        Assert.Equal(expected, NvidiaEngine.IsNvidia(name));

    [Theory]
    [InlineData("580.88", 580.88)]          // nvidia-smi
    [InlineData("32.0.15.8088", 580.88)]    // the driver's file version: the last five digits
    [InlineData("31.0.15.5222", 552.22)]
    [InlineData("32.0.15.6094", 560.94)]
    [InlineData("", 0)]
    [InlineData("unknown", 0)]
    public void Reads_the_driver_number_from_either_form(string text, double expected) =>
        Assert.Equal(expected, NvidiaEngine.DriverVersion(text), 2);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", 581.29, "13.4")]
    [InlineData("NVIDIA GeForce RTX 5090", 590.10, "13.4")]
    [InlineData("NVIDIA GeForce RTX 3060", 552.22, "12.4")]   // a driver too old for CUDA 13
    [InlineData("NVIDIA GeForce GTX 1060 6GB", 581.29, "12.4")] // a card CUDA 13 dropped
    [InlineData("NVIDIA GeForce GTX 970", 581.29, "12.4")]
    [InlineData("NVIDIA GeForce MX150", 581.29, "12.4")]
    [InlineData("NVIDIA GeForce RTX 2060", 472.12, null)]     // too old for both: stays on the built-in engine
    [InlineData("NVIDIA GeForce RTX 4070", 0, null)]          // driver unknown
    [InlineData("AMD Radeon RX 7800 XT", 581.29, null)]
    public void Picks_the_cuda_version_a_card_and_driver_can_run(string name, double driver, string? expected) =>
        Assert.Equal(expected, NvidiaEngine.CudaFor(name, driver));

    [Fact]
    public void Says_plainly_what_it_found()
    {
        Assert.Equal("", NvidiaEngine.Explain("Intel(R) Arc(TM) 140T GPU", 0, false));
        Assert.Contains("is active", NvidiaEngine.Explain("NVIDIA GeForce RTX 4070", 581.29, true));
        Assert.Contains("faster", NvidiaEngine.Explain("NVIDIA GeForce RTX 4070", 581.29, false));
        Assert.Contains("too old", NvidiaEngine.Explain("NVIDIA GeForce RTX 2060", 472.12, false));
    }
}

public class SuggestTests
{
    private static SuggestCandidate Row(string origin, double paramsB, double quality, double tps,
        string category = "Chat", string? released = "2026-01-01") =>
        new("unsloth/" + origin[(origin.LastIndexOf('/') + 1)..] + "-GGUF", origin, paramsB, quality,
            Math.Round(paramsB * 0.606 + 0.1, 1), Math.Round(paramsB * 0.64 + 1.6, 1), tps * 0.85, tps * 1.15, category, released);

    [Theory]
    [InlineData("google/gemma-4-E4B-it", "unsloth", "Multimodal", false, true)]
    [InlineData("Qwen/Qwen3.5-4B", "unsloth", "Multimodal", false, true)]
    [InlineData("meta-llama/Llama-3.2-3B-Instruct", "bartowski", "Chat", false, true)]
    [InlineData("Qwen/Qwen2.5-3B", "bartowski", "General", false, false)]              // an untuned base model
    [InlineData("Qwen/Qwen2.5-3B-Instruct", "bartowski", "General", false, true)]
    [InlineData("Qwen/Qwen2.5-Coder-7B-Instruct", "unsloth", "Coding", false, false)]  // made for one job
    [InlineData("google/medgemma-4b-it", "unsloth", "Multimodal", false, false)]
    [InlineData("dots-studio/dots.ocr", "ggml-org", "Multimodal", false, false)]
    [InlineData("BAAI/bge-large-en", "unsloth", "Embedding", false, false)]
    [InlineData("someone/Qwen3-8B-abliterated", "unsloth", "Chat", false, false)]
    [InlineData("someone/nice-chat-8B-Instruct", "mradermacher", "Chat", false, false)] // a copy from a publisher few use
    [InlineData("google/gemma-4-26B-A4B-it", "unsloth", "Multimodal", true, false)]     // its speed cannot be sized this way
    public void Keeps_general_chat_models_with_a_known_copy(string origin, string provider, string category, bool moe, bool expected) =>
        Assert.Equal(expected, Suggest.Usable(origin, provider, category, moe));

    [Fact]
    public void Suggests_the_best_mix_then_the_smartest_then_the_fastest()
    {
        var picks = Suggest.Pick(
        [
            Row("google/gemma-4-E4B-it", 8.0, 92.5, 9.7, "Multimodal"),
            Row("Qwen/Qwen3.5-4B", 4.7, 77, 15.6, "Multimodal"),
            Row("Qwen/Qwen3.5-2B", 2.3, 62, 28, "Multimodal"),
            Row("HuggingFaceTB/SmolLM2-360M-Instruct", 0.4, 30, 120),
        ]);
        Assert.Equal(["best", "smartest", "fastest"], picks.Select(p => p.Kind));
        Assert.Equal("Qwen3.5-4B", picks[0].Model.Name);
        Assert.Equal("gemma-4-E4B-it", picks[1].Model.Name);
        Assert.Equal("Qwen3.5-2B", picks[2].Model.Name);   // quicker, and not the 360M toy: it must still answer well
    }

    [Fact]
    public void Never_suggests_a_model_slower_than_reading_pace_when_a_comfortable_one_exists()
    {
        var picks = Suggest.Pick(
        [
            Row("big/Smart-27B-it", 27, 97, 3.1),      // smarter, but painful to wait for
            Row("google/gemma-4-E4B-it", 8.0, 92.5, 9.7),
            Row("Qwen/Qwen3.5-4B", 4.7, 77, 15.6),
        ]);
        Assert.DoesNotContain(picks, p => p.Model.Name == "Smart-27B-it");
        Assert.Equal("gemma-4-E4B-it", picks.Single(p => p.Kind == "smartest").Model.Name);
    }

    [Fact]
    public void Counts_the_same_model_once()
    {
        var picks = Suggest.Pick(
        [
            Row("Qwen/Qwen3-VL-8B-Instruct", 8.8, 89, 9.0),
            Row("Qwen/Qwen3-VL-8B-Thinking", 8.8, 89, 9.0),
            Row("unsloth/Qwen3-VL-8B", 8.8, 89, 9.0),
        ]);
        Assert.Single(picks);
        Assert.Equal("best", picks[0].Kind);
    }

    [Fact]
    public void A_weak_pc_still_gets_an_answer()
    {
        // nothing reaches reading pace: the picks come from what fits at all
        var picks = Suggest.Pick([Row("Qwen/Qwen3.5-2B", 2.3, 62, 4.0), Row("Qwen/Qwen3.5-4B", 4.7, 77, 2.2)]);
        Assert.NotEmpty(picks);
        Assert.Equal("best", picks[0].Kind);
    }

    [Fact]
    public void Nothing_that_fits_gives_no_suggestion() => Assert.Empty(Suggest.Pick([]));
}
