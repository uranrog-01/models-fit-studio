using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelFitStudio.Core;

public sealed record AgentTestResult(double ReadSeconds, int ReadTokens, double WriteTps, int Correct, int Total, int Points, bool Fallback);

/// <summary>
/// Runs one downloaded model the way the Agents server does (same length, attention, drafting and
/// thinking settings) and measures what decides how it feels behind an agent: reading a
/// 12,000-token request, picking the right tool in <see cref="AgentCases"/>, and writing speed.
/// </summary>
public static class AgentTest
{
    /// <param name="report">phase (loading | loading-cpu | reading | tools | writing), then the case number while on tools.</param>
    public static async Task<AgentTestResult> RunAsync(string serverExe, string modelPath, string file, bool useGpu, bool integratedGpu,
        bool thinking, Action<string, int>? report = null, CancellationToken ct = default)
    {
        var extra = "--jinja" +
            (useGpu && AgentPlan.PlainAttention(file, integratedGpu) ? " -fa off -ub 128" : "") +
            (GgufInfo.TryRead(modelPath)?.HasMtp == true ? " --spec-type draft-mtp" : "") +
            (thinking ? "" : " --reasoning off");
        await using var server = await ModelServer.StartAsync(serverExe, modelPath, useGpu, ph => report?.Invoke(ph, 0), ct,
            context: 16384, extra: extra);

        // warm-up: the first request pays for shaders and buffers
        await server.PostAsync("/completion", new { prompt = "Hello", n_predict = 4, cache_prompt = false }, ct);

        report?.Invoke("reading", 0);
        var read = JsonNode.Parse(await server.PostAsync("/completion",
            new { prompt = AgentCases.LongPrompt(AgentCases.ReadTokens), n_predict = 1, cache_prompt = false }, ct));
        var readMs = read?["timings"]?["prompt_ms"]?.GetValue<double>() ?? 0;
        var readN = read?["timings"]?["prompt_n"]?.GetValue<int>() ?? 0;
        // the same 12,000 tokens for every model, whatever its tokenizer made of the text
        var readSeconds = readN > 0 ? Math.Round(readMs / 1000 * AgentCases.ReadTokens / readN, 1) : 0;

        var correct = 0;
        for (var i = 0; i < AgentCases.All.Length; i++)
        {
            report?.Invoke("tools", i + 1);
            var c = AgentCases.All[i];
            try
            {
                var answer = JsonNode.Parse(await server.PostAsync("/v1/chat/completions", AgentCases.Request(c), ct));
                var msg = answer?["choices"]?[0]?["message"];
                var call = (msg?["tool_calls"] as JsonArray)?.FirstOrDefault();
                if (AgentCases.Grade(c, call?["function"]?["name"]?.GetValue<string>(), call?["function"]?["arguments"]?.ToString(),
                        msg?["content"]?.ToString()))
                    correct++;
            }
            catch (InvalidOperationException) { /* a refused or broken answer is a wrong one */ }
        }

        report?.Invoke("writing", 0);
        var write = JsonNode.Parse(await server.PostAsync("/completion",
            new { prompt = "Write a short story about a garden.", n_predict = 96, ignore_eos = true, cache_prompt = false }, ct));
        var writeTps = Math.Round(write?["timings"]?["predicted_per_second"]?.GetValue<double>() ?? 0, 1);

        var total = AgentCases.All.Length;
        return new AgentTestResult(readSeconds, AgentCases.ReadTokens, writeTps, correct, total,
            AgentCases.Points(correct, total, readSeconds, writeTps), server.Fallback);
    }
}
