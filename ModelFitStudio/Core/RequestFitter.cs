using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// Makes an agent's request fit the model's conversation length, for any agent: OpenAI chat,
/// OpenAI Responses and Anthropic messages. Used only after llama-server refused a request as
/// too long, so a request that fits is never touched. In order, until it fits:
/// tool descriptions are shortened, the tools the conversation does not use are left out
/// (the ones it used, the agent's own and the ones the last message names stay), very long
/// tool outputs are cut in the middle, the oldest turns are dropped, and as a last step the
/// middle of the instructions is cut. Pure: JSON in, JSON out.
/// </summary>
public static partial class RequestFitter
{
    public enum Format { OpenAiChat, Responses, Anthropic }

    /// <summary>What was done, for the Agents tab.</summary>
    public sealed record Report(int ToolsBefore, int ToolsAfter, int TurnsDropped, int StepsDropped, int OutputsCut, bool InstructionsCut)
    {
        public bool Changed => ToolsAfter < ToolsBefore || TurnsDropped > 0 || StepsDropped > 0 || OutputsCut > 0 || InstructionsCut;
    }

    public static Format? FormatOf(string path)
    {
        var p = path.TrimEnd('/').ToLowerInvariant();
        if (p.EndsWith("/messages") && !p.EndsWith("/count_tokens")) return Format.Anthropic;
        if (p.EndsWith("/chat/completions")) return Format.OpenAiChat;
        if (p.EndsWith("/responses")) return Format.Responses;
        return null;
    }

    /// <summary>
    /// What was decided for one conversation, given back on its next request. llama.cpp reuses what it
    /// already read only while the start of the request stays the same, so the same tools are kept and
    /// the same turns left out, turn after turn; when more must go, a good deal more goes at once, so the
    /// next several turns fit without changing the start again.
    /// </summary>
    public sealed class Memory
    {
        public HashSet<string>? Tools { get; set; }
        public int TurnsDropped { get; set; }
        public int StepsDropped { get; set; }
        /// <summary>The words of the turn the dropped steps belong to; another turn starts with none dropped.</summary>
        public string StepsTurn { get; set; } = "";
        /// <summary>Characters of request JSON per token, as llama-server counted them for this conversation; 0 until known.</summary>
        public double PerToken { get; set; }
    }

    /// <summary>
    /// How long a request is kept, in tokens. Reading time grows much faster than the length: on an
    /// Arc 140T with Qwen3-1.7B, 2,200 tokens are read in 1.9 s, 11,000 in 19 s, 22,000 in 103 s and
    /// 33,000 in 211 s. So a long agent request is shortened to about 12,000 tokens (and never past
    /// 72% of the model's memory, which leaves room for the answer).
    /// </summary>
    public const int FastTokens = 12_000;

    /// <summary>How long the first read of a request may take: the length is chosen to fit this.</summary>
    public const double ReadGoalSeconds = 20;

    /// <summary>
    /// The length a request is kept to on this PC. With <paramref name="read12kSeconds"/> (how long this model
    /// took to read 12,000 tokens here, measured) it is what reads in about <see cref="ReadGoalSeconds"/>: a fast
    /// graphics card keeps far more of an agent's request, a slow chip less. Unmeasured, 12,000 tokens.
    /// Never past 72% of the model's memory, never under 4,000.
    /// </summary>
    public static int TargetTokens(int context, double read12kSeconds = 0)
    {
        var n = read12kSeconds > 0
            ? (int)(12_000 * Math.Pow(ReadGoalSeconds / read12kSeconds, 1 / 1.7))
            : FastTokens;
        n = Math.Max(4_000, n);
        return context > 0 ? Math.Min((int)(context * 0.72), n) : n;
    }

    /// <summary>Characters of request JSON per token: this conversation's own count once known, else a typical 4.</summary>
    public static double PerTokenOf(Memory memory) => memory.PerToken > 0 ? memory.PerToken : 4.0;

    /// <summary>
    /// True when a request is clearly longer than <see cref="TargetTokens"/> and is shortened before it
    /// is sent: llama-server would read it for minutes, or take seconds just to refuse it.
    /// </summary>
    public static bool ShouldShorten(int chars, int context, Memory memory, double read12kSeconds = 0) =>
        context > 0 && chars / PerTokenOf(memory) > TargetTokens(context, read12kSeconds) * 1.15;

    /// <summary>The same conversation on each request: the instructions, the tool names and its first message.</summary>
    public static string ConversationKey(JsonObject body, Format f)
    {
        var msgs = Messages(body, f);
        var sys = f switch
        {
            Format.Anthropic => body["system"]?.ToJsonString() ?? "",
            Format.Responses => body["instructions"]?.ToString() ?? "",
            _ => string.Join("\n", msgs?.Where(m => Role(m) is "system" or "developer").Select(m => m?["content"]?.ToJsonString()) ?? []),
        };
        var names = (body["tools"] as JsonArray)?.Select(t => ToolName(t, f)) ?? [];
        var firstTurn = msgs is not null && TurnStarts(msgs, f) is { Count: > 0 } s ? TextOf(msgs[s[0]]?["content"]) : "";
        var bytes = System.Text.Encoding.UTF8.GetBytes(f + "\n" + sys + "\n" + string.Join(",", names) + "\n" + firstTurn);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..24];
    }

    /// <param name="budgetChars">How long the request may be, in characters of its JSON.</param>
    /// <param name="memory">What was decided for this conversation before; updated in place.</param>
    public static (JsonObject body, Report report) Fit(JsonObject original, Format format, int budgetChars, Memory? memory = null)
    {
        memory ??= new Memory();
        var body = (JsonObject)original.DeepClone();
        var msgs = Messages(body, format);
        var tools = body["tools"] as JsonArray;
        var toolsBefore = tools?.Count ?? 0;
        int outputsCut = 0, turnsDropped = 0, stepsDropped = 0;
        var instructionsCut = false;

        bool Fits() => Length(body) <= budgetChars;
        // when something must go, aim lower, so the next turns fit as they are
        var roomy = budgetChars * 60 / 100;
        bool Roomy() => Length(body) <= roomy;
        if (Fits()) return (body, new Report(toolsBefore, toolsBefore, 0, 0, 0, false));

        // 1. tools: short descriptions, then only the ones that matter, within 35% of the room;
        //    the same ones as last time while they still fit, plus any the conversation has since used
        if (tools is not null && tools.Count > 0)
        {
            foreach (var t in tools) ShortenTool(t, format);
            var toolRoom = budgetChars * 35 / 100;
            if (Length(tools) > toolRoom)
            {
                JsonArray? kept = null;
                if (memory.Tools is { } remembered)
                {
                    var used = UsedToolNames(msgs, format);
                    var again = new JsonArray();
                    foreach (var t in tools)
                        if (remembered.Contains(ToolName(t, format)) || used.Contains(ToolName(t, format))) again.Add(t!.DeepClone());
                    if (Length(again) <= toolRoom) kept = again;
                }
                kept ??= KeepTools(tools, format, msgs, toolRoom);
                body["tools"] = tools = kept;
                memory.Tools = kept.Select(t => ToolName(t, format)).ToHashSet();
            }
            if (tools.Count == 0)
            {
                body.Remove("tools");
                body.Remove("tool_choice");
            }
        }

        // 2. very long tool outputs and pasted texts: cut in the middle, each to 15% of the room
        if (!Fits() && msgs is not null)
            outputsCut = CutLongOutputs(msgs, format, Math.Max(2000, budgetChars * 15 / 100));

        // 3. the oldest turns, never the last one: the same ones as last time first, then more at once
        if (msgs is not null)
        {
            var first = FirstNonSystem(msgs, format);
            void DropFirstTurn()
            {
                var starts = TurnStarts(msgs, format);
                var count = starts[1] - first;
                for (var i = 0; i < count; i++) msgs.RemoveAt(first);
                turnsDropped++;
            }
            while (turnsDropped < memory.TurnsDropped && TurnStarts(msgs, format).Count > 1) DropFirstTurn();
            if (!Fits())
                while (!Roomy() && TurnStarts(msgs, format).Count > 1) DropFirstTurn();
            memory.TurnsDropped = turnsDropped;

            // one long task is one turn with many steps: the oldest steps go, the request and the last two steps stay
            var start = TurnStarts(msgs, format) is { Count: > 0 } s ? s[^1] : first;
            var turnText = start < msgs.Count ? TextOf(msgs[start]?["content"]) : "";
            if (memory.StepsTurn != turnText) memory.StepsDropped = 0;
            while (stepsDropped < memory.StepsDropped && msgs.Count - (start + 1) > 2)
            {
                DropStep(msgs, format, start + 1);
                stepsDropped++;
            }
            if (!Fits())
                while (!Roomy() && msgs.Count - (start + 1) > 2)
                {
                    DropStep(msgs, format, start + 1);
                    stepsDropped++;
                }
            memory.StepsDropped = stepsDropped;
            memory.StepsTurn = turnText;
            if (turnsDropped > 0 || stepsDropped > 0) AddNote(body, format, msgs,
                "[Models Fit Studio: earlier parts of this conversation were left out to fit the model's memory.]");
        }

        // 4. still too long: the instructions themselves, cut in the middle
        if (!Fits())
        {
            var over = Length(body) - budgetChars;
            instructionsCut = CutInstructions(body, format, msgs, over + 200);
        }

        return (body, new Report(toolsBefore, (body["tools"] as JsonArray)?.Count ?? 0, turnsDropped, stepsDropped, outputsCut, instructionsCut));
    }

    /// <summary>Removes one step at <paramref name="i"/>: a tool call together with its results, so no result is left without its call.</summary>
    private static void DropStep(JsonArray msgs, Format f, int i)
    {
        var m = msgs[i];
        msgs.RemoveAt(i);
        switch (f)
        {
            case Format.OpenAiChat:
                if (Role(m) == "assistant")
                    while (i < msgs.Count && Role(msgs[i]) == "tool") msgs.RemoveAt(i);
                break;
            case Format.Anthropic:
                if (Role(m) == "assistant" && i < msgs.Count && Role(msgs[i]) == "user" &&
                    msgs[i]?["content"] is JsonArray blocks && blocks.Count > 0 && blocks.All(b => TypeOf(b) == "tool_result"))
                    msgs.RemoveAt(i);
                break;
            case Format.Responses:
                if (TypeOf(m) == "function_call" && m?["call_id"]?.ToString() is { } id)
                    for (var j = msgs.Count - 1; j >= 0; j--)
                        if (TypeOf(msgs[j]) == "function_call_output" && msgs[j]?["call_id"]?.ToString() == id) msgs.RemoveAt(j);
                break;
        }
    }

    public static int Length(JsonNode? node) => node is null ? 0 : node.ToJsonString().Length;

    // ---- the parts of each format ---------------------------------------------------

    private static JsonArray? Messages(JsonObject body, Format f) => f switch
    {
        Format.Responses => body["input"] as JsonArray,
        _ => body["messages"] as JsonArray,
    };

    private static string Role(JsonNode? m) => m?["role"]?.GetValue<string>() ?? "";

    private static string TypeOf(JsonNode? m) => (m as JsonObject)?["type"] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    /// <summary>The index of each item that starts a turn: a user message with words of its own (not only tool results).</summary>
    private static List<int> TurnStarts(JsonArray msgs, Format f)
    {
        var starts = new List<int>();
        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (Role(m) != "user") continue;
            if (f == Format.Responses && TypeOf(m) is not ("" or "message")) continue;
            if (f == Format.Anthropic && m?["content"] is JsonArray blocks && blocks.Count > 0 &&
                blocks.All(b => TypeOf(b) == "tool_result")) continue;
            starts.Add(i);
        }
        return starts;
    }

    private static int FirstNonSystem(JsonArray msgs, Format f)
    {
        var i = 0;
        while (i < msgs.Count && Role(msgs[i]) is "system" or "developer") i++;
        return i;
    }

    // ---- tools ------------------------------------------------------------------------

    private static JsonObject? ToolFn(JsonNode? t, Format f) =>
        f == Format.OpenAiChat ? t?["function"] as JsonObject : t as JsonObject;

    private static string ToolName(JsonNode? t, Format f) => ToolFn(t, f)?["name"]?.GetValue<string>() ?? "";

    private static void ShortenTool(JsonNode? t, Format f)
    {
        var fn = ToolFn(t, f);
        if (fn is null) return;
        if (fn["description"] is JsonValue d && d.TryGetValue(out string? text) && text is { Length: > 300 })
            fn["description"] = text[..300] + "…";
        var schema = fn[f == Format.Anthropic ? "input_schema" : "parameters"];
        ShortenSchema(schema);
    }

    // descriptions inside a schema: the first sentence is enough for a model to fill a field
    private static void ShortenSchema(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            if (o["description"] is JsonValue d && d.TryGetValue(out string? text) && text is { Length: > 120 })
                o["description"] = text[..120] + "…";
            o.Remove("examples");
            foreach (var kv in o.ToList()) ShortenSchema(kv.Value);
        }
        else if (node is JsonArray a)
            foreach (var x in a) ShortenSchema(x);
    }

    private static JsonArray KeepTools(JsonArray tools, Format f, JsonArray? msgs, int room)
    {
        var used = UsedToolNames(msgs, f);
        var words = Words(LastUserText(msgs, f));
        var ranked = tools
            .Select((t, i) => (t, i, name: ToolName(t, f), size: Length(t)))
            .Select(x => (x.t, x.i, x.size, rank:
                used.Contains(x.name) ? 0 :
                !IsNamespaced(x.name) ? 1 :
                Words(x.name + " " + (ToolFn(x.t, f)?["description"]?.ToString() ?? "")).Overlaps(words) ? 2 : 3))
            .OrderBy(x => x.rank).ThenBy(x => x.size)
            .ToList();

        var keep = new List<(JsonNode t, int i)>();
        var total = 2;
        foreach (var x in ranked)
        {
            if (total + x.size + 1 > room && x.rank > 0) continue;
            keep.Add((x.t, x.i));
            total += x.size + 1;
        }
        var result = new JsonArray();
        foreach (var (t, _) in keep.OrderBy(k => k.i)) result.Add(t.DeepClone());
        return result;
    }

    /// <summary>Tools from an MCP server or a plugin carry a namespace ("mcp__rea__open_binary", "rea.open"); an agent's own tools do not.</summary>
    public static bool IsNamespaced(string name) =>
        name.Contains("__") || name.Contains('.') || name.Contains('/') || name.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> UsedToolNames(JsonArray? msgs, Format f)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (msgs is null) return used;
        foreach (var m in msgs)
        {
            switch (f)
            {
                case Format.OpenAiChat:
                    if (m?["tool_calls"] is JsonArray calls)
                        foreach (var c in calls) if (c?["function"]?["name"]?.ToString() is { } n) used.Add(n);
                    break;
                case Format.Anthropic:
                    if (m?["content"] is JsonArray blocks)
                        foreach (var b in blocks) if (TypeOf(b) == "tool_use" && b?["name"]?.ToString() is { } n) used.Add(n);
                    break;
                case Format.Responses:
                    if (TypeOf(m) == "function_call" && m?["name"]?.ToString() is { } rn) used.Add(rn);
                    break;
            }
        }
        return used;
    }

    private static string LastUserText(JsonArray? msgs, Format f)
    {
        if (msgs is null) return "";
        var starts = TurnStarts(msgs, f);
        return starts.Count == 0 ? "" : TextOf(msgs[starts[^1]]?["content"]);
    }

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s ?? "",
        JsonArray a => string.Join(" ", a.Select(b => b?["text"]?.ToString() ?? "")),
        _ => "",
    };

    private static HashSet<string> Words(string text) =>
        WordRx().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 3).ToHashSet();

    [GeneratedRegex(@"[a-z][a-z0-9]+")]
    private static partial Regex WordRx();

    // ---- long outputs -----------------------------------------------------------------

    private static int CutLongOutputs(JsonArray msgs, Format f, int max)
    {
        var cut = 0;
        // the last turn's own words stay whole; outputs inside it are fair game
        foreach (var m in msgs)
        {
            if (m is not JsonObject o) continue;
            switch (f)
            {
                case Format.OpenAiChat:
                    if (Role(o) is "tool" or "user" or "assistant") cut += CutContent(o, "content", max);
                    break;
                case Format.Anthropic:
                    if (o["content"] is JsonArray blocks)
                        foreach (var b in blocks.OfType<JsonObject>())
                        {
                            if (TypeOf(b) == "tool_result") cut += CutContent(b, "content", max);
                            else if (TypeOf(b) == "text") cut += CutContent(b, "text", max);
                        }
                    else cut += CutContent(o, "content", max);
                    break;
                case Format.Responses:
                    if (TypeOf(o) == "function_call_output") cut += CutContent(o, "output", max);
                    else cut += CutContent(o, "content", max);
                    break;
            }
        }
        return cut;
    }

    private static int CutContent(JsonObject o, string key, int max)
    {
        switch (o[key])
        {
            case JsonValue v when v.TryGetValue(out string? s) && s is not null && s.Length > max:
                o[key] = Middle(s, max);
                return 1;
            case JsonArray a:
                var n = 0;
                foreach (var b in a.OfType<JsonObject>())
                {
                    if (b["text"] is JsonValue t && t.TryGetValue(out string? ts) && ts is not null && ts.Length > max)
                    {
                        b["text"] = Middle(ts, max);
                        n++;
                    }
                }
                return n;
        }
        return 0;
    }

    public static string Middle(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max * 2 / 3;
        var tail = max - head;
        return s[..head] + $"\n[… {s.Length - max:N0} characters left out by Models Fit Studio to fit the model …]\n" + s[^tail..];
    }

    // ---- instructions -----------------------------------------------------------------

    private static void AddNote(JsonObject body, Format f, JsonArray msgs, string note)
    {
        switch (f)
        {
            case Format.Anthropic:
                if (body["system"] is JsonValue sv && sv.TryGetValue(out string? s)) body["system"] = s + "\n\n" + note;
                else if (body["system"] is JsonArray sa) sa.Add(new JsonObject { ["type"] = "text", ["text"] = note });
                else body["system"] = note;
                break;
            case Format.Responses:
                body["instructions"] = (body["instructions"]?.ToString() is { Length: > 0 } i ? i + "\n\n" : "") + note;
                break;
            default:
                var sys = msgs.FirstOrDefault(m => Role(m) is "system" or "developer") as JsonObject;
                if (sys is not null && sys["content"] is JsonValue cv && cv.TryGetValue(out string? c)) sys["content"] = c + "\n\n" + note;
                else msgs.Insert(0, new JsonObject { ["role"] = "system", ["content"] = note });
                break;
        }
    }

    private static bool CutInstructions(JsonObject body, Format f, JsonArray? msgs, int over)
    {
        JsonObject? holder = null;
        string key = "";
        switch (f)
        {
            case Format.Anthropic:
                if (body["system"] is JsonValue) { holder = body; key = "system"; }
                else if (body["system"] is JsonArray sa)
                {
                    holder = sa.OfType<JsonObject>().OrderByDescending(b => b["text"]?.ToString().Length ?? 0).FirstOrDefault();
                    key = "text";
                }
                break;
            case Format.Responses:
                holder = body; key = "instructions";
                break;
            default:
                holder = msgs?.Where(m => Role(m) is "system" or "developer").OfType<JsonObject>()
                    .OrderByDescending(m => m["content"]?.ToString().Length ?? 0).FirstOrDefault();
                key = "content";
                break;
        }
        if (holder?[key] is not JsonValue v || !v.TryGetValue(out string? text) || text is null) return false;
        var keep = Math.Max(1000, text.Length - over);
        if (keep >= text.Length) return false;
        holder[key] = Middle(text, keep);
        return true;
    }

    /// <summary>The numbers in llama-server's refusal: {"error":{"type":"exceed_context_size_error","n_prompt_tokens":…,"n_ctx":…}}.</summary>
    public static (int promptTokens, int context)? TooBig(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("error", out var e)) return null;
            if (!(e.TryGetProperty("type", out var t) && t.GetString() == "exceed_context_size_error")) return null;
            return (e.GetProperty("n_prompt_tokens").GetInt32(), e.GetProperty("n_ctx").GetInt32());
        }
        catch { return null; }
    }
}
