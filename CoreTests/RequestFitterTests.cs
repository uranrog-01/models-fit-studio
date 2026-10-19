using System.Text.Json.Nodes;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// Making an agent's too-long request fit, in all three formats. The case behind it: an agent with
/// three MCP servers sent 282,128 tokens (mostly tool descriptions) to a model that holds 40,960.
/// </summary>
public class RequestFitterTests
{
    private static readonly string Long = new('x', 600);

    private static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "The path. " + Long },
            ["address"] = new JsonObject { ["type"] = "string", ["description"] = "An address. " + Long },
        },
    };

    private static IEnumerable<(string name, string desc)> ToolList()
    {
        yield return ("read_file", "Read a file from the project");
        yield return ("run_shell", "Run a shell command");
        for (var i = 0; i < 400; i++) yield return ($"mcp__rea__tool_{i}", "Reverse engineering helper number " + i + ". " + Long);
        yield return ("mcp__weather__get_forecast", "Get the weather forecast for a city");
    }

    // ---------- Anthropic ----------

    private static JsonObject AnthropicRequest()
    {
        var tools = new JsonArray();
        foreach (var (name, desc) in ToolList())
            tools.Add(new JsonObject { ["name"] = name, ["description"] = desc, ["input_schema"] = Schema() });
        var msgs = new JsonArray();
        for (var turn = 0; turn < 6; turn++)
        {
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = $"Old question {turn}" });
            msgs.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = $"t{turn}", ["name"] = "mcp__rea__tool_7", ["input"] = new JsonObject() }),
            });
            msgs.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = $"t{turn}", ["content"] = new string('y', 20000) }),
            });
            msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Done " + turn });
        }
        msgs.Add(new JsonObject { ["role"] = "user", ["content"] = "What is the weather forecast in Paris?" });
        return new JsonObject { ["model"] = "claude-sonnet-4-5", ["max_tokens"] = 1000, ["system"] = "You are a coding agent.", ["tools"] = tools, ["messages"] = msgs };
    }

    [Fact]
    public void An_anthropic_request_is_made_to_fit_and_stays_valid()
    {
        var req = AnthropicRequest();
        var budget = 60_000;
        Assert.True(RequestFitter.Length(req) > 5 * budget);

        var (fit, report) = RequestFitter.Fit(req, RequestFitter.Format.Anthropic, budget);

        Assert.True(RequestFitter.Length(fit) <= budget, $"length {RequestFitter.Length(fit)}");
        Assert.True(report.Changed);
        var names = ((JsonArray)fit["tools"]!).Select(t => t!["name"]!.ToString()).ToHashSet();
        Assert.Contains("read_file", names);                 // the agent's own tools
        Assert.Contains("run_shell", names);
        Assert.Contains("mcp__rea__tool_7", names);          // the one the conversation used
        Assert.Contains("mcp__weather__get_forecast", names); // the one the last message is about
        Assert.True(report.ToolsAfter < report.ToolsBefore);

        var msgs = (JsonArray)fit["messages"]!;
        Assert.Equal("What is the weather forecast in Paris?", msgs[^1]!["content"]!.ToString());
        Assert.Equal("user", msgs[0]!["role"]!.ToString());
        AssertNoOrphanAnthropic(msgs);
        Assert.Contains("left out", fit["system"]!.ToString());
        Assert.Equal(403, ((JsonArray)req["tools"]!).Count);   // the original is untouched
    }

    private static void AssertNoOrphanAnthropic(JsonArray msgs)
    {
        var uses = new HashSet<string>();
        foreach (var m in msgs)
            if (m!["content"] is JsonArray blocks)
                foreach (var b in blocks)
                {
                    if (b!["type"]!.ToString() == "tool_use") uses.Add(b["id"]!.ToString());
                    if (b["type"]!.ToString() == "tool_result") Assert.Contains(b["tool_use_id"]!.ToString(), uses);
                }
    }

    [Fact]
    public void The_next_turn_starts_the_same_way_so_the_model_need_not_read_it_again()
    {
        var memory = new RequestFitter.Memory();
        var turn1 = AnthropicRequest();
        var (fit1, _) = RequestFitter.Fit(turn1, RequestFitter.Format.Anthropic, 60_000, memory);

        // the agent sends the whole conversation again, with the answer and a new question that names other tools
        var turn2 = (JsonObject)turn1.DeepClone();
        var msgs2 = (JsonArray)turn2["messages"]!;
        msgs2.Add(new JsonObject { ["role"] = "assistant", ["content"] = "It is sunny." });
        msgs2.Add(new JsonObject { ["role"] = "user", ["content"] = "Now decompile tool number 300 with the reverse engineering helper" });
        Assert.Equal(RequestFitter.ConversationKey(turn1, RequestFitter.Format.Anthropic), RequestFitter.ConversationKey(turn2, RequestFitter.Format.Anthropic));
        var (fit2, _) = RequestFitter.Fit(turn2, RequestFitter.Format.Anthropic, 60_000, memory);

        Assert.Equal(fit1["system"]!.ToJsonString(), fit2["system"]!.ToJsonString());
        Assert.Equal(fit1["tools"]!.ToJsonString(), fit2["tools"]!.ToJsonString());
        var m1 = (JsonArray)fit1["messages"]!;
        var m2 = (JsonArray)fit2["messages"]!;
        for (var i = 0; i < m1.Count; i++) Assert.Equal(m1[i]!.ToJsonString(), m2[i]!.ToJsonString());
        Assert.Equal(m1.Count + 2, m2.Count);
    }

    // ---------- OpenAI chat ----------

    [Fact]
    public void One_long_task_drops_its_oldest_steps_but_keeps_calls_with_their_results()
    {
        var tools = new JsonArray();
        foreach (var (name, desc) in ToolList())
            tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = desc, ["parameters"] = Schema() } });
        var msgs = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding agent." },
            new JsonObject { ["role"] = "user", ["content"] = "Fix the build" },
        };
        for (var step = 0; step < 30; step++)
        {
            msgs.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = $"c{step}", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "run_shell", ["arguments"] = "{}" } }),
            });
            msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = $"c{step}", ["content"] = new string('z', 3000) });
        }
        var req = new JsonObject { ["model"] = "gpt-4o", ["tools"] = tools, ["messages"] = msgs };

        var (fit, report) = RequestFitter.Fit(req, RequestFitter.Format.OpenAiChat, 30_000);

        Assert.True(RequestFitter.Length(fit) <= 30_000, $"length {RequestFitter.Length(fit)}");
        Assert.True(report.StepsDropped > 0);
        var kept = (JsonArray)fit["messages"]!;
        Assert.Equal("system", kept[0]!["role"]!.ToString());
        Assert.Equal("Fix the build", kept[1]!["content"]!.ToString());
        var calls = new HashSet<string>();
        foreach (var m in kept)
        {
            if (m!["tool_calls"] is JsonArray tc) foreach (var c in tc) calls.Add(c!["id"]!.ToString());
            if (m["role"]!.ToString() == "tool") Assert.Contains(m["tool_call_id"]!.ToString(), calls);
        }
        Assert.Equal("c29", kept[^1]!["tool_call_id"]!.ToString());   // the newest step stays
    }

    // ---------- Responses ----------

    [Fact]
    public void A_responses_request_is_made_to_fit()
    {
        var tools = new JsonArray();
        foreach (var (name, desc) in ToolList())
            tools.Add(new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = desc, ["parameters"] = Schema() });
        var input = new JsonArray();
        for (var turn = 0; turn < 5; turn++)
        {
            input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Old " + turn });
            input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = $"f{turn}", ["name"] = "read_file", ["arguments"] = "{}" });
            input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = $"f{turn}", ["output"] = new string('o', 10000) });
        }
        input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Now read the README" });
        var req = new JsonObject { ["model"] = "gpt-5", ["instructions"] = "Be brief.", ["tools"] = tools, ["input"] = input };

        var (fit, report) = RequestFitter.Fit(req, RequestFitter.Format.Responses, 25_000);

        Assert.True(RequestFitter.Length(fit) <= 25_000, $"length {RequestFitter.Length(fit)}");
        Assert.True(report.Changed);
        var items = (JsonArray)fit["input"]!;
        Assert.Equal("Now read the README", items[^1]!["content"]!.ToString());
        var calls = items.Where(i => i!["type"]!.ToString() == "function_call").Select(i => i!["call_id"]!.ToString()).ToHashSet();
        foreach (var o in items.Where(i => i!["type"]!.ToString() == "function_call_output"))
            Assert.Contains(o!["call_id"]!.ToString(), calls);
    }

    [Fact]
    public void A_request_that_fits_is_left_alone()
    {
        var req = new JsonObject { ["model"] = "x", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) };
        var (fit, report) = RequestFitter.Fit(req, RequestFitter.Format.OpenAiChat, 10_000);
        Assert.False(report.Changed);
        Assert.Equal(req.ToJsonString(), fit.ToJsonString());
    }

    [Theory]
    [InlineData("/v1/messages", RequestFitter.Format.Anthropic)]
    [InlineData("/v1/chat/completions", RequestFitter.Format.OpenAiChat)]
    [InlineData("/chat/completions", RequestFitter.Format.OpenAiChat)]
    [InlineData("/v1/responses", RequestFitter.Format.Responses)]
    public void The_format_comes_from_the_path(string path, RequestFitter.Format format)
    {
        Assert.Equal(format, RequestFitter.FormatOf(path));
        Assert.Null(RequestFitter.FormatOf("/v1/messages/count_tokens"));
    }

    [Fact]
    public void The_length_kept_follows_how_fast_this_pc_reads()
    {
        // an integrated chip that reads 12,000 tokens in 26 s keeps a little less than that
        Assert.InRange(RequestFitter.TargetTokens(40960, 26), 10_000, 11_000);
        // a graphics card that reads them in 1.5 s keeps far more: here all the model's room (72% of 40,960)
        Assert.Equal(29491, RequestFitter.TargetTokens(40960, 1.5));
        Assert.InRange(RequestFitter.TargetTokens(262144, 1.5), 50_000, 60_000);
        // a very slow processor never goes under 4,000
        Assert.Equal(4_000, RequestFitter.TargetTokens(40960, 400));
        // unmeasured: 12,000
        Assert.Equal(12_000, RequestFitter.TargetTokens(40960, 0));
    }

    [Fact]
    public void A_long_request_is_shortened_to_what_is_read_quickly()
    {
        Assert.Equal(12_000, RequestFitter.TargetTokens(40960));
        Assert.Equal(5898, RequestFitter.TargetTokens(8192));                  // a small memory: 72% of it
        var fresh = new RequestFitter.Memory();
        Assert.True(RequestFitter.ShouldShorten(3_000_000, 40960, fresh));     // ~750K tokens
        Assert.True(RequestFitter.ShouldShorten(90_000, 40960, fresh));        // ~22K tokens: 100 s of reading on an iGPU
        Assert.False(RequestFitter.ShouldShorten(40_000, 40960, fresh));       // ~10K tokens: left as it is
        var known = new RequestFitter.Memory { PerToken = 6.0 };
        Assert.False(RequestFitter.ShouldShorten(80_000, 40960, known));       // this conversation counts 6 a token: 13K
        Assert.False(RequestFitter.ShouldShorten(3_000_000, 0, fresh));        // length not known yet
    }

    [Fact]
    public void Llama_servers_refusal_is_read()
    {
        Assert.Equal((282128, 40960), RequestFitter.TooBig(
            "{\"error\":{\"code\":400,\"message\":\"request (282128 tokens) exceeds the available context size (40960 tokens), try increasing it\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":282128,\"n_ctx\":40960}}"));
        Assert.Null(RequestFitter.TooBig("{\"error\":{\"code\":400,\"type\":\"invalid_request_error\"}}"));
        Assert.Null(RequestFitter.TooBig("not json"));
    }
}
