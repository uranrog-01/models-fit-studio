using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>The server for AI agents: the name agents use, the key, the llama-server line, and reading its log.</summary>
public class AgentPlanTests
{
    [Theory]
    [InlineData("Qwen3-1.7B-Q4_K_M.gguf", "qwen3-1.7b-q4_k_m")]
    [InlineData("BF16/gemma-4-26B-A4B-it-BF16-00001-of-00002.gguf", "gemma-4-26b-a4b-it-bf16")]
    [InlineData("Some Model (v2).gguf", "some-model-v2")]
    [InlineData(".gguf", "local-model")]
    public void The_model_name_comes_from_the_file(string file, string id)
    {
        Assert.Equal(id, AgentPlan.ModelId(file));
    }

    [Fact]
    public void A_new_key_is_a_key_and_never_the_same_twice()
    {
        var a = AgentPlan.NewKey();
        var b = AgentPlan.NewKey();
        Assert.True(AgentPlan.IsKey(a));
        Assert.StartsWith("sk-local-", a);
        Assert.NotEqual(a, b);
        Assert.False(AgentPlan.IsKey("hello"));
        Assert.False(AgentPlan.IsKey(null));
    }

    [Fact]
    public void The_line_serves_one_model_with_the_key_on_this_pc_only()
    {
        var args = AgentPlan.Arguments(@"C:\m\Qwen3-1.7B-Q4_K_M.gguf", 8686, 32768, true, "qwen3-1.7b-q4_k_m", @"C:\k\key.txt", true);
        Assert.Contains("--host 127.0.0.1 --port 8686", args);
        Assert.Contains("-c 32768", args);
        Assert.Contains("-ngl 99", args);
        Assert.Contains("--alias qwen3-1.7b-q4_k_m", args);
        Assert.Contains("--api-key-file \"C:\\k\\key.txt\"", args);
        Assert.EndsWith("-fa off -ub 128", args);
        Assert.DoesNotContain("-fa off", AgentPlan.Arguments("m.gguf", 1, 16384, false, "m", "k", false));
    }

    [Theory]
    [InlineData(16384, 16384)]
    [InlineData(65536, 65536)]
    [InlineData(0, 0)]
    [InlineData(12345, 0)]
    public void Only_the_offered_lengths_are_used_and_anything_else_is_auto(int asked, int used)
    {
        Assert.Equal(used, AgentPlan.ClampContext(asked));
    }

    [Fact]
    public void The_router_preset_lists_every_model_and_loads_the_chosen_one()
    {
        var models = new[]
        {
            new AgentPlan.ServeModel("qwen3-1.7b-q4_k_m", @"C:\m\Qwen3-1.7B-Q4_K_M.gguf", PlainAttention: true, Mtp: false),
            new AgentPlan.ServeModel("qwen3.5-2b-mtp-q4_k_m", @"C:\m\Qwen3.5-2B-MTP-Q4_K_M.gguf", PlainAttention: true, Mtp: true),
            new AgentPlan.ServeModel("gemma-4-e4b-it-q4_k_m", @"C:\m\gemma-4-E4B-it-Q4_K_M.gguf", PlainAttention: false, Mtp: false),
        };
        var ini = AgentPlan.Preset(models, "qwen3-1.7b-q4_k_m", 0, gpu: true, thinking: false);
        Assert.StartsWith("version = 1", ini);
        Assert.Contains("[*]\njinja = true\nngl = 99\nreasoning = off\n", ini);
        Assert.DoesNotContain("\nc = ", ini);                                  // Auto: the model's own length
        Assert.Contains("[qwen3-1.7b-q4_k_m]\nmodel = C:\\m\\Qwen3-1.7B-Q4_K_M.gguf\nfa = off\nub = 128\nload-on-startup = true\n", ini);
        Assert.Contains("[qwen3.5-2b-mtp-q4_k_m]\nmodel = C:\\m\\Qwen3.5-2B-MTP-Q4_K_M.gguf\nfa = off\nub = 128\nspec-type = draft-mtp\n", ini);
        Assert.Contains("[gemma-4-e4b-it-q4_k_m]\nmodel = C:\\m\\gemma-4-E4B-it-Q4_K_M.gguf\n", ini);
        Assert.Single(ini.Split("load-on-startup").Skip(1));

        var cpu = AgentPlan.Preset(models, "x", 16384, gpu: false, thinking: true);
        Assert.Contains("ngl = 0\nc = 16384\n", cpu);
        Assert.DoesNotContain("reasoning", cpu);
        Assert.DoesNotContain("fa = off", cpu);                                // plain attention is a graphics-chip setting
        Assert.Contains("--models-preset \"p.ini\" --models-max 1 --api-key-file \"k.txt\"", AgentPlan.RouterArguments(9686, "p.ini", "k.txt"));
    }

    [Fact]
    public void Two_files_with_the_same_name_get_different_ids()
    {
        var ids = AgentPlan.UniqueIds(new[] { ("Qwen3-1.7B-Q4_K_M.gguf", 1), ("Qwen3-1.7B-Q4_K_M.gguf", 2), ("other.gguf", 3) });
        Assert.Equal(["qwen3-1.7b-q4_k_m", "qwen3-1.7b-q4_k_m-2", "other"], ids.Select(x => x.id));
    }

    [Theory]
    [InlineData("unsloth/Qwen3-1.7B-GGUF/Qwen3-1.7B-Q4_K_M.gguf", true)]
    [InlineData("ggml-org/whisper-large-v3/ggml-model.gguf", false)]
    [InlineData("nomic-ai/nomic-embed-text-v1.5-GGUF/nomic-embed-text-v1.5.Q8_0.gguf", false)]
    [InlineData("x/y/mmproj-F16.gguf", false)]
    public void Speech_and_embedding_models_are_not_offered(string path, bool chat)
    {
        Assert.Equal(chat, AgentPlan.LooksLikeChat(path));
    }

    [Fact]
    public void A_shorter_read_is_scaled_up_to_12000_tokens_by_more_than_its_length()
    {
        Assert.Equal(19.0, AgentPlan.Read12kFrom(12_000, 19.0));
        Assert.Equal(16.2, AgentPlan.Read12kFrom(6_000, 5.0));                 // 5 s × 2^1.7
        Assert.Equal(0, AgentPlan.Read12kFrom(0, 5));
    }

    [Fact]
    public void Drafting_is_asked_for_only_when_the_model_has_draft_layers()
    {
        Assert.EndsWith(" --spec-type draft-mtp", AgentPlan.Arguments("m.gguf", 8686, 0, true, "m", "k", false, mtp: true));
        Assert.DoesNotContain("--spec-type", AgentPlan.Arguments("m.gguf", 8686, 0, true, "m", "k", false));
    }

    [Fact]
    public void Auto_leaves_the_length_to_llama_cpp()
    {
        var args = AgentPlan.Arguments("m.gguf", 8686, 0, true, "m", "k", false);
        Assert.DoesNotContain(" -c ", args);
        Assert.Contains(" -c 16384 ", AgentPlan.Arguments("m.gguf", 8686, 16384, true, "m", "k", false));
    }

    [Fact]
    public void A_request_too_long_for_the_model_is_caught()
    {
        var a = new AgentPlan.Activity();
        Assert.True(AgentPlan.Read(a, "0.02.114.236 E srv    send_error: task id = 0, error: request (282124 tokens) exceeds the available context size (32768 tokens), try increasing it"));
        Assert.Equal(282124, a.TooBigTokens);
        Assert.Equal(32768, a.TooBigContext);
        Assert.Equal("idle", a.Phase);

        // a request that fits clears it
        AgentPlan.Read(a, "I slot print_timing: id  2 | task 41 | prompt eval time =   1000.00 ms /  1200 tokens (    0.88 ms per token,  1136.42 tokens per second)");
        Assert.Equal(0, a.TooBigTokens);
    }

    [Theory]
    [InlineData("Qwen3-1.7B-Q4_K_M.gguf", true, true)]
    [InlineData("Qwen3-1.7B-Q4_K_M.gguf", false, false)]   // a card with its own memory keeps the default
    [InlineData("gemma-4-E4B-it-Q4_K_M.gguf", true, false)] // Gemma got slower follow-up turns with it
    public void Plain_attention_only_for_qwen_on_shared_memory(string file, bool integrated, bool plain)
    {
        Assert.Equal(plain, AgentPlan.PlainAttention(file, integrated));
    }

    [Fact]
    public void The_log_tells_what_a_request_is_doing()
    {
        var a = new AgentPlan.Activity();
        Assert.True(AgentPlan.Read(a, "0.05.339.798 I slot launch_slot_: id  2 | task 41 | processing task, is_child = 0"));
        Assert.Equal("reading", a.Phase);
        Assert.Equal(1, a.Requests);

        AgentPlan.Read(a, "0.06.000.000 I slot update_slots: id  2 | task 41 | prompt processing progress, n_tokens = 8192, batch.n_tokens = 2048, progress = 0.452100");
        Assert.Equal(0.4521, a.Progress, 4);
        Assert.Equal(8192, a.ReadSoFar);

        // the line as llama.cpp b11574 writes it
        AgentPlan.Read(a, "0.53.277.398 I slot print_timing: id  1 | task 35 | prompt processing, n_tokens =   10240, progress = 0.68, t =  13.63 s / 601.25 tokens per second");
        Assert.Equal("reading", a.Phase);
        Assert.Equal(0.68, a.Progress, 2);
        Assert.Equal(10240, a.ReadSoFar);
        Assert.Equal(0, a.LastTps);

        AgentPlan.Read(a, "0.09.986.034 I slot print_timing: id  2 | task 41 | prompt eval time =   52138.15 ms / 18157 tokens (    0.88 ms per token,  1136.42 tokens per second)");
        Assert.Equal("writing", a.Phase);
        Assert.Equal(18157, a.LastRead);
        Assert.Equal(52.1, a.LastReadSeconds);

        AgentPlan.Read(a, "0.09.986.059 I slot print_timing: id  2 | task 41 |        eval time =     525.33 ms /    24 tokens (   22.84 ms per token,    43.78 tokens per second)");
        Assert.Equal(24, a.LastWritten);
        Assert.Equal(43.8, a.LastTps);

        AgentPlan.Read(a, "0.09.986.123 I slot      release: id  2 | task 41 | stop processing: n_tokens = 180, truncated = 0");
        Assert.Equal("idle", a.Phase);
        Assert.False(AgentPlan.Read(a, "0.10.000.000 I srv  update_slots: all slots are idle"));
    }
}
