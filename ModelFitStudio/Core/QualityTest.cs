using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

public sealed record QualityResult(double Score, int Correct, int Total, Dictionary<string, int> Parts, bool Fallback);

/// <summary>
/// Runs the quality test on a downloaded file: every question goes to llama-server's
/// chat endpoint (so the model's own chat template is used), the reply is checked
/// by <see cref="QualityQuestions.Grade"/>, and the score is the share answered right.
/// </summary>
public static class QualityTest
{
    // room to reason before the final line; the answer is read from the end
    private const int ReplyTokens = 320;

    /// <param name="report">phase (loading | loading-cpu | testing), then the question number while testing.</param>
    public static async Task<QualityResult> RunAsync(string serverExe, string modelPath, bool useGpu,
        Action<string, int>? report = null, CancellationToken ct = default)
    {
        await using var server = await ModelServer.StartAsync(serverExe, modelPath, useGpu, ph => report?.Invoke(ph, 0), ct);

        var parts = new Dictionary<string, int>();
        var correct = 0;
        var all = QualityQuestions.All;
        for (var i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            report?.Invoke("testing", i + 1);
            var q = all[i];
            parts.TryAdd(q.Skill, 0);
            var reply = await AskAsync(server, q.Prompt, ct);
            if (!QualityQuestions.Grade(q, reply)) continue;
            correct++;
            parts[q.Skill]++;
        }
        return new QualityResult(Math.Round(100.0 * correct / all.Count), correct, all.Count, parts, server.Fallback);
    }

    private static async Task<string> AskAsync(ModelServer server, string prompt, CancellationToken ct)
    {
        var text = await server.PostAsync("/v1/chat/completions", new
        {
            messages = new object[] { new { role = "user", content = prompt + QualityQuestions.Ending } },
            max_tokens = ReplyTokens,
            temperature = 0,
            seed = 1,
            cache_prompt = false,
            // hidden "thinking" is switched off: the reasoning is written in the reply, where its length is bounded
            chat_template_kwargs = new { enable_thinking = false },
        }, ct);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out var msg))
            return "";
        return msg.Str("content") ?? "";
    }
}
