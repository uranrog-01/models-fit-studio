using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

public sealed record BenchResult(double GenTps, double PromptTps, double FirstTokenMs, double LoadSeconds, int Runs)
{
    /// <summary>True when the GPU could not load the file and the run was made on the CPU instead.</summary>
    public bool Fallback { get; init; }
}

/// <summary>
/// A llama-server that lives for one job: a speed measurement, a quality test, a
/// community benchmark. It listens on a local port only and is stopped when the
/// job ends — the app still does not serve or chat with models.
/// </summary>
public sealed class ModelServer : IAsyncDisposable
{
    public const int ContextTokens = 4096;

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static Process? _current;

    private readonly Process _process;
    public string BaseUrl { get; }
    public double LoadSeconds { get; }
    /// <summary>True when the GPU could not load the file and the CPU took it instead.</summary>
    public bool Fallback { get; private init; }

    private ModelServer(Process process, string baseUrl, double loadSeconds)
    {
        _process = process;
        BaseUrl = baseUrl;
        LoadSeconds = loadSeconds;
    }

    /// <summary>Stops a job's server if one is alive — for app exit, so no model stays loaded behind a closed window.</summary>
    public static void KillCurrent()
    {
        try { _current?.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>
    /// Loads on the GPU when there is one. When the GPU cannot take the file (a
    /// driver without Vulkan, not enough memory), the CPU still can: a slower real
    /// answer beats none, and the session says so.
    /// </summary>
    /// <param name="phase">"loading", then "loading-cpu" if the GPU gave up.</param>
    /// <param name="context">Conversation length; <paramref name="extra"/> more llama-server arguments (the agent test runs as the Agents server does).</param>
    public static async Task<ModelServer> StartAsync(string serverExe, string modelPath, bool useGpu,
        Action<string>? phase = null, CancellationToken ct = default, int context = ContextTokens, string extra = "")
    {
        phase?.Invoke("loading");
        if (!useGpu) return await StartOnceAsync(serverExe, modelPath, false, ct, context, extra);
        try
        {
            return await StartOnceAsync(serverExe, modelPath, true, ct, context, extra);
        }
        catch (InvalidOperationException) when (!ct.IsCancellationRequested)
        {
            // NVIDIA's build refuses a card or driver it does not support; the built-in one draws on any card
            if (Engine.FallbackFor(serverExe) is { } builtIn)
            {
                serverExe = builtIn;
                try { return await StartOnceAsync(serverExe, modelPath, true, ct, context, extra); }
                catch (InvalidOperationException) when (!ct.IsCancellationRequested) { }
            }
            phase?.Invoke("loading-cpu");
            var onCpu = await StartOnceAsync(serverExe, modelPath, false, ct, context, extra);
            return new ModelServer(onCpu._process, onCpu.BaseUrl, onCpu.LoadSeconds) { Fallback = true };
        }
    }

    private static async Task<ModelServer> StartOnceAsync(string serverExe, string modelPath, bool useGpu, CancellationToken ct,
        int context = ContextTokens, string extra = "")
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The model file is not on disk.", modelPath);

        var port = FreePort();
        var log = new LogTail(40);
        var psi = new ProcessStartInfo
        {
            FileName = serverExe,
            Arguments = $"-m \"{modelPath}\" --host 127.0.0.1 --port {port} -c {context} -ngl {(useGpu ? 99 : 0)}" + (extra.Length > 0 ? " " + extra : ""),
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => log.Add(e.Data);
        p.ErrorDataReceived += (_, e) => log.Add(e.Data);
        var clock = Stopwatch.StartNew();
        if (!p.Start()) throw new InvalidOperationException("llama-server did not start.");
        _current = p;
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var baseUrl = $"http://127.0.0.1:{port}";
            await WaitUntilLoadedAsync(p, baseUrl, log, ct);
            return new ModelServer(p, baseUrl, Math.Round(clock.Elapsed.TotalSeconds, 1));
        }
        catch
        {
            Stop(p);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop(_process);
        return ValueTask.CompletedTask;
    }

    private static void Stop(Process p)
    {
        if (ReferenceEquals(_current, p)) _current = null;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private static async Task WaitUntilLoadedAsync(Process p, string baseUrl, LogTail log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (p.HasExited)
                throw new InvalidOperationException("llama.cpp could not load this file. " + log.Reason());
            try
            {
                using var r = await Http.GetAsync(baseUrl + "/health", ct);
                if (r.StatusCode == HttpStatusCode.OK) return;   // 503 while the weights are still loading
            }
            catch (HttpRequestException) { /* not listening yet */ }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The model did not finish loading in 8 minutes.");
            await Task.Delay(400, ct);
        }
    }

    /// <summary>POSTs JSON to the server and returns the body; a failed status becomes an exception that says what the server said.</summary>
    public async Task<string> PostAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await Http.PostAsync(BaseUrl + path,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"llama-server answered HTTP {(int)resp.StatusCode}: {Short(text)}");
        return text;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    internal static string Short(string s) => s.Length > 240 ? s[..240] + "…" : s;

    /// <summary>The last lines the server wrote, to say why a load failed.</summary>
    private sealed class LogTail(int keep)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lines)
            {
                _lines.Enqueue(line.Trim());
                while (_lines.Count > keep) _lines.Dequeue();
            }
        }

        public string Reason()
        {
            lock (_lines)
            {
                var hit = _lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                                    l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                                    l.Contains("unable", StringComparison.OrdinalIgnoreCase))
                          ?? _lines.LastOrDefault();
                return hit is null ? "It gave no reason." : "It said: " + Short(hit);
            }
        }
    }
}

/// <summary>
/// A real speed measurement: loads a downloaded file in llama-server, asks it to
/// write, and reads the server's own timings.
/// </summary>
public static class Bench
{
    public const int Runs = 3;
    private const int ReplyTokens = 128;

    // a short, ordinary request: about 110 tokens of prompt, so the first-token time is a real one
    private const string Prompt =
        "You are a helpful assistant. A user asks: I am planning a small vegetable garden behind my house. " +
        "The space is about four metres by six metres and gets sun for most of the afternoon. I have never grown " +
        "anything before. Please explain, step by step, how I should prepare the soil, which vegetables are the " +
        "easiest to start with, how often I should water them, and what common mistakes beginners make. " +
        "Write a clear and complete answer with practical details.\n\nAnswer:";

    /// <param name="report">phase (loading | loading-cpu | measuring), then the run number while measuring.</param>
    public static async Task<BenchResult> RunAsync(string serverExe, string modelPath, bool useGpu,
        Action<string, int>? report = null, CancellationToken ct = default)
    {
        await using var server = await ModelServer.StartAsync(serverExe, modelPath, useGpu, ph => report?.Invoke(ph, 0), ct);

        // the first request pays one-time costs (shader compile, cache setup); it is not counted
        report?.Invoke("measuring", 0);
        await CompleteAsync(server, 8, ct);

        var gen = new List<double>();
        var prompt = new List<double>();
        var first = new List<double>();
        for (var run = 1; run <= Runs; run++)
        {
            report?.Invoke("measuring", run);
            var t = await CompleteAsync(server, ReplyTokens, ct);
            if (t.PredictedPerSecond > 0) gen.Add(t.PredictedPerSecond);
            if (t.PromptPerSecond > 0) prompt.Add(t.PromptPerSecond);
            if (t.PredictedN > 0) first.Add(t.PromptMs + t.PredictedMs / t.PredictedN);
        }
        if (gen.Count == 0)
            throw new InvalidOperationException("llama-server answered without timings — this file is probably not a text model.");

        return new BenchResult(Math.Round(Median(gen), 1), Math.Round(Median(prompt), 1), Math.Round(Median(first)),
            server.LoadSeconds, gen.Count) { Fallback = server.Fallback };
    }

    private sealed record Timings(double PromptMs, double PromptPerSecond, int PredictedN, double PredictedMs, double PredictedPerSecond);

    private static async Task<Timings> CompleteAsync(ModelServer server, int tokens, CancellationToken ct)
    {
        var text = await server.PostAsync("/completion", new
        {
            prompt = Prompt,
            n_predict = tokens,
            ignore_eos = true,      // a model that stops early would shorten the run, not slow it
            cache_prompt = false,   // every run reads the prompt again, so the first-token time is honest
            stream = false,
        }, ct);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("timings", out var t) || t.ValueKind != JsonValueKind.Object)
            return new Timings(0, 0, 0, 0, 0);
        return new Timings(t.Num("prompt_ms") ?? 0, t.Num("prompt_per_second") ?? 0, (int)(t.Num("predicted_n") ?? 0),
            t.Num("predicted_ms") ?? 0, t.Num("predicted_per_second") ?? 0);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }
}
