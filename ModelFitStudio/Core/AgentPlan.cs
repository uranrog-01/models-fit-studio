using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// The pure half of the server for AI agents: the model's name, the key, the
/// llama-server line, and what its log says it is doing. No process, no network.
/// </summary>
public static partial class AgentPlan
{
    public const int DefaultPort = 8686;
    /// <summary>Conversation lengths offered; 0 is Auto: the model's own longest, made shorter by llama.cpp when memory is short.</summary>
    public static readonly int[] Contexts = [0, 16384, 32768, 65536];

    /// <summary>The name agents use for the model: the file's own name, lower case, without ".gguf" or a split suffix.</summary>
    public static string ModelId(string file)
    {
        var name = file.Replace('\\', '/').Split('/').Last();
        name = Regex.Replace(name, @"\.gguf$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"-\d{5}-of-\d{5}$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-');
        return name.Length == 0 ? "local-model" : name;
    }

    /// <summary>A new key: "sk-local-" and 40 letters and digits.</summary>
    public static string NewKey()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return "sk-local-" + RandomNumberGenerator.GetString(chars, 40);
    }

    public static bool IsKey(string? s) => s is not null && Regex.IsMatch(s.Trim(), @"^sk-local-[A-Za-z0-9]{20,}$");

    /// <summary>
    /// Plain attention reads long prompts about twice as fast on a graphics chip that shares
    /// system memory, for the Qwen families (measured on an Arc 140T). Gemma got slower
    /// follow-up turns with it, so only these are switched.
    /// </summary>
    public static bool PlainAttention(string file, bool integratedGpu) =>
        integratedGpu && Regex.IsMatch(file.Replace('\\', '/').Split('/').Last(), @"qwen", RegexOptions.IgnoreCase);

    public static int ClampContext(int ctx) => Contexts.Contains(ctx) ? ctx : 0;

    /// <summary>The llama-server line. Any model name an agent sends is answered by this one model; without -c llama.cpp picks the length.</summary>
    /// <param name="mtp">The model has its own draft layers: llama.cpp guesses a few tokens and checks them in one pass (same answer, sooner).</param>
    /// <param name="thinking">
    /// False: the model answers at once. A thinking model otherwise reasons first on every reply; measured on
    /// Qwen3-1.7B, "What is 17 times 23?" used all 400 tokens thinking and gave no answer (9.5 s), and with
    /// thinking off answered 391 in 0.7 s. Tool calls work either way.
    /// </param>
    public static string Arguments(string modelPath, int port, int context, bool gpu, string modelId, string keyFile, bool plainAttention,
        bool mtp = false, bool thinking = true) =>
        $"-m \"{modelPath}\" --host 127.0.0.1 --port {port} " +
        (ClampContext(context) > 0 ? $"-c {ClampContext(context)} " : "") +
        $"-ngl {(gpu ? 99 : 0)} --jinja " +
        $"--alias {modelId} --api-key-file \"{keyFile}\"" +
        (plainAttention ? " -fa off -ub 128" : "") +
        (mtp ? " --spec-type draft-mtp" : "") +
        (thinking ? "" : " --reasoning off");

    // ---- every downloaded model, loaded when an agent asks for it --------------------

    /// <summary>Not a chat model by its name: speech, embeddings, rerankers and projectors are not offered to agents.</summary>
    public static bool LooksLikeChat(string repoAndFile) =>
        !Regex.IsMatch(repoAndFile, @"(^|[-_./])(asr|stt|tts|whisper|speech|vocoder|embed(ding)?s?|rerank(er)?|mmproj)([-_./]|$)", RegexOptions.IgnoreCase);

    /// <summary>One model the server can load: its name for agents, its file, and how it runs best.</summary>
    public sealed record ServeModel(string Id, string Path, bool PlainAttention, bool Mtp);

    /// <summary>Names agents use, made unique: a second file with the same name gets "-2".</summary>
    public static List<(string id, T item)> UniqueIds<T>(IEnumerable<(string file, T item)> files)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, T)>();
        foreach (var (file, item) in files)
        {
            var id = ModelId(file);
            var unique = id;
            for (var n = 2; !used.Add(unique); n++) unique = $"{id}-{n}";
            list.Add((unique, item));
        }
        return list;
    }

    /// <summary>
    /// llama-server's router preset: one section per model (keys are its command-line options), the
    /// chosen one loaded at start. The router keeps one model in memory and swaps on request.
    /// </summary>
    public static string Preset(IEnumerable<ServeModel> models, string defaultId, int context, bool gpu, bool thinking)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("version = 1\n\n[*]\njinja = true\n");
        sb.Append($"ngl = {(gpu ? 99 : 0)}\n");
        if (ClampContext(context) > 0) sb.Append($"c = {ClampContext(context)}\n");
        if (!thinking) sb.Append("reasoning = off\n");
        foreach (var m in models)
        {
            sb.Append($"\n[{m.Id}]\nmodel = {m.Path}\n");
            if (gpu && m.PlainAttention) sb.Append("fa = off\nub = 128\n");
            if (m.Mtp) sb.Append("spec-type = draft-mtp\n");
            if (string.Equals(m.Id, defaultId, StringComparison.OrdinalIgnoreCase)) sb.Append("load-on-startup = true\n");
        }
        return sb.ToString();
    }

    public static string RouterArguments(int port, string presetFile, string keyFile) =>
        $"--host 127.0.0.1 --port {port} --models-preset \"{presetFile}\" --models-max 1 --api-key-file \"{keyFile}\"";

    /// <summary>
    /// Seconds to read 12,000 tokens, from one real read of <paramref name="tokens"/> tokens. Reading time grows
    /// faster than the length (measured exponent about 1.7 on an Arc 140T), so a short read is scaled up by more.
    /// </summary>
    public static double Read12kFrom(int tokens, double seconds) =>
        tokens > 0 && seconds > 0 ? Math.Round(seconds * Math.Pow(12_000.0 / tokens, 1.7), 1) : 0;

    // ---- what the server is doing, read from its log --------------------------------

    /// <summary>One request's progress, as llama-server's log tells it.</summary>
    public sealed class Activity
    {
        /// <summary>"idle", "reading" (the agent's request) or "writing" (the answer).</summary>
        public string Phase { get; set; } = "idle";
        public int Requests { get; set; }
        /// <summary>While reading: how far, 0–1, and how many tokens are read so far.</summary>
        public double Progress { get; set; }
        public int ReadSoFar { get; set; }
        /// <summary>The last request: tokens read and how long that took, tokens written and how fast.</summary>
        public int LastRead { get; set; }
        public double LastReadSeconds { get; set; }
        public int LastWritten { get; set; }
        public double LastTps { get; set; }
        /// <summary>The last request refused as longer than the conversation length: its tokens and that length. 0 again once a request fits.</summary>
        public int TooBigTokens { get; set; }
        public int TooBigContext { get; set; }
    }

    /// <summary>Moves the activity on by one log line; returns true when something shown changed.</summary>
    public static bool Read(Activity a, string line)
    {
        // "send_error: … request (282124 tokens) exceeds the available context size (32768 tokens)"
        if (line.Contains("exceeds the available context size"))
        {
            if (Number(TooBigRx(), line) is { } t) a.TooBigTokens = (int)t;
            if (Number(CtxSizeRx(), line) is { } c) a.TooBigContext = (int)c;
            a.Phase = "idle";
            return true;
        }
        if (line.Contains("launch_slot_") && line.Contains("processing task"))
        {
            a.Phase = "reading";
            a.Progress = 0;
            a.ReadSoFar = 0;
            a.Requests++;
            return true;
        }
        // "prompt processing, n_tokens = 4096, progress = 0.34, …" (older builds: "prompt processing progress, …")
        if (line.Contains("prompt processing"))
        {
            a.Phase = "reading";
            if (Number(ProgressRx(), line) is { } p) a.Progress = Math.Clamp(p, 0, 1);
            if (Number(NTokensRx(), line) is { } n) a.ReadSoFar = (int)n;
            return true;
        }
        if (line.Contains("prompt eval time"))
        {
            var m = EvalRx().Match(line);
            if (m.Success)
            {
                a.LastReadSeconds = Math.Round(Double(m.Groups[1].Value) / 1000, 1);
                a.LastRead = (int)Double(m.Groups[2].Value);
            }
            a.Phase = "writing";
            a.Progress = 1;
            a.TooBigTokens = 0;
            return true;
        }
        if (line.Contains(" eval time") && !line.Contains("prompt"))
        {
            var m = EvalRx().Match(line);
            if (m.Success) a.LastWritten = (int)Double(m.Groups[2].Value);
            if (Number(TpsRx(), line) is { } tps) a.LastTps = Math.Round(tps, 1);
            return true;
        }
        if (line.Contains("slot") && line.Contains("release:"))
        {
            a.Phase = "idle";
            return true;
        }
        return false;
    }

    private static double Double(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static double? Number(Regex rx, string line)
    {
        var m = rx.Match(line);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"progress =\s*([0-9.]+)")]
    private static partial Regex ProgressRx();

    [GeneratedRegex(@"n_tokens =\s*(\d+)")]
    private static partial Regex NTokensRx();

    [GeneratedRegex(@"eval time =\s*([0-9.]+) ms /\s*(\d+) tokens")]
    private static partial Regex EvalRx();

    [GeneratedRegex(@"request \((\d+) tokens\)")]
    private static partial Regex TooBigRx();

    [GeneratedRegex(@"context size \((\d+) tokens\)")]
    private static partial Regex CtxSizeRx();

    [GeneratedRegex(@"([0-9.]+) tokens per second")]
    private static partial Regex TpsRx();
}
