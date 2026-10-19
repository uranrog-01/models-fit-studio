using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace ModelFitStudio.Core;

/// <summary>
/// The address agents use. Every request goes straight on to llama-server and its answer comes
/// straight back (streamed as it is written); the app reads nothing of it. Only when llama-server
/// refuses a request as longer than the model's memory does the proxy make it fit
/// (<see cref="RequestFitter"/>) and send it once more, so an agent with too many tools or a long
/// history still gets an answer instead of an error.
/// </summary>
public sealed class AgentProxy : IDisposable
{
    private static readonly HttpClient Upstream = new(new SocketsHttpHandler
    {
        UseProxy = false,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Connection", "Transfer-Encoding", "Expect", "Keep-Alive", "Content-Type" };
    private static readonly HashSet<string> SkipResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Server", "Date" };

    private readonly HttpListener _listener = new();
    private readonly int _upstreamPort;
    private readonly Func<bool> _fitOn;
    private readonly Action<FitEvent> _fitted;
    private readonly Func<string?, int> _contextFor;
    private readonly Func<string?, double> _read12kFor;
    private readonly Func<string> _defaultModel;
    private readonly Func<IReadOnlyList<string>> _models;

    private static JsonObject? TryParse(byte[] body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch { return null; }
    }
    private readonly CancellationTokenSource _cts = new();

    /// <summary>A request that was made to fit: its size as sent, the model's length, and what was left out.</summary>
    public sealed record FitEvent(int FromTokens, int Context, RequestFitter.Report Report, bool Answered);

    /// <param name="contextFor">A model's conversation length once known (0 before).</param>
    /// <param name="read12kFor">Seconds that model takes here to read 12,000 tokens (0 when unmeasured): long requests are sized from it.</param>
    /// <param name="defaultModel">Where a request naming no model, or a model this PC does not have, is sent.</param>
    public AgentProxy(int port, int upstreamPort, Func<bool> fitOn, Action<FitEvent> fitted,
        Func<string?, int> contextFor, Func<string?, double> read12kFor, Func<string> defaultModel, Func<IReadOnlyList<string>> models)
    {
        _upstreamPort = upstreamPort;
        _fitOn = fitOn;
        _fitted = fitted;
        _contextFor = contextFor;
        _read12kFor = read12kFor;
        _defaultModel = defaultModel;
        _models = models;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.IgnoreWriteExceptions = true;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            byte[] body = [];
            if (req.HasEntityBody)
            {
                using var ms = new MemoryStream();
                await req.InputStream.CopyToAsync(ms, _cts.Token);
                body = ms.ToArray();
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var format = RequestFitter.FormatOf(path);

            // the model to use: one of this PC's by its name, else the chosen one (agents send names like
            // "claude-sonnet-4-5" or "gpt-4o"); the router loads it if another is in memory
            JsonObject? early = null;
            string? model = null;
            if (req.HttpMethod == "POST" && body.Length > 0 && TryParse(body) is { } parsed)
            {
                early = parsed;
                var asked = parsed["model"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                model = _models().FirstOrDefault(id => string.Equals(id, asked, StringComparison.OrdinalIgnoreCase)) ?? _defaultModel();
                if (!string.Equals(asked, model, StringComparison.Ordinal))
                {
                    parsed["model"] = model;
                    body = Encoding.UTF8.GetBytes(parsed.ToJsonString());
                }
            }

            // a long request is shortened before llama-server sees it: reading time grows much faster
            // than the length, and on a very long request llama-server takes seconds just to refuse it
            var context = _contextFor(model);
            var read12k = _read12kFor(model);
            if (format is not null && _fitOn() && context > 0 && body.Length > RequestFitter.FastTokens && early is not null)
            {
                var memory = MemoryFor(RequestFitter.ConversationKey(early, format.Value));
                if (RequestFitter.ShouldShorten(body.Length, context, memory, read12k))
                {
                    var perToken = RequestFitter.PerTokenOf(memory);
                    using var direct = await FitAndSendAsync(req, early, format.Value, (int)(body.Length / perToken), context, perToken, read12k);
                    if (direct is not null)
                    {
                        await CopyAsync(direct, res);
                        return;
                    }
                }
            }

            using var first = await SendAsync(req, body, _cts.Token);
            if (format is not null && first.StatusCode == HttpStatusCode.BadRequest && _fitOn())
            {
                var text = await first.Content.ReadAsStringAsync(_cts.Token);
                if (RequestFitter.TooBig(text) is { } big && (early ?? TryParse(body)) is { } json)
                {
                    using var fitted = await FitAndSendAsync(req, json, format.Value, big.promptTokens, big.context, null, read12k);
                    if (fitted is not null)
                    {
                        await CopyAsync(fitted, res);
                        return;
                    }
                }
                await WriteAsync(res, first, Encoding.UTF8.GetBytes(text));
                return;
            }
            await CopyAsync(first, res);
        }
        catch (HttpRequestException)
        {
            await Fail(res, 503, "The model is not ready yet. Start it in Models Fit Studio → Agents.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Fail(res, 502, "Models Fit Studio could not pass this request on: " + ex.Message);
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    /// <summary>Makes the request fit and sends it again; tighter each time llama-server still says no (three tries).</summary>
    private async Task<HttpResponseMessage?> FitAndSendAsync(HttpListenerRequest req, JsonObject json, RequestFitter.Format format,
        int promptTokens, int context, double? knownPerToken, double read12k)
    {
        var memory = MemoryFor(RequestFitter.ConversationKey(json, format));
        // characters per token, learnt from what llama-server counted
        var perToken = knownPerToken ?? (double)RequestFitter.Length(json) / Math.Max(1, promptTokens);
        if (knownPerToken is null) memory.PerToken = perToken;
        var asked = promptTokens;
        // short enough to be read quickly, with room left for the answer; tighter on each refusal
        var scale = 1.0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var budget = (int)(perToken * RequestFitter.TargetTokens(context, read12k) * scale);
            // always from the agent's own request, so what is remembered means the same thing each time
            var (smaller, report) = RequestFitter.Fit(json, format, budget, memory);
            if (!report.Changed) return null;
            var resp = await SendAsync(req, Encoding.UTF8.GetBytes(smaller.ToJsonString()), _cts.Token);
            if (resp.StatusCode == HttpStatusCode.BadRequest)
            {
                var text = await resp.Content.ReadAsStringAsync(_cts.Token);
                if (RequestFitter.TooBig(text) is { } again)
                {
                    resp.Dispose();
                    perToken = (double)RequestFitter.Length(smaller) / Math.Max(1, again.promptTokens);
                    memory.PerToken = perToken;
                    scale *= 0.85;
                    continue;
                }
                resp.Dispose();
                _fitted(new FitEvent(asked, context, report, false));
                return null;
            }
            _fitted(new FitEvent(asked, context, report, true));
            return resp;
        }
        return null;
    }

    // what was decided per conversation, so its next request starts the same way and llama.cpp reuses what it read
    private readonly Dictionary<string, (RequestFitter.Memory memory, DateTime used)> _memories = new();

    private RequestFitter.Memory MemoryFor(string key)
    {
        lock (_memories)
        {
            if (_memories.TryGetValue(key, out var hit))
            {
                _memories[key] = (hit.memory, DateTime.UtcNow);
                return hit.memory;
            }
            if (_memories.Count >= 32)
                _memories.Remove(_memories.MinBy(kv => kv.Value.used).Key);
            var memory = new RequestFitter.Memory();
            _memories[key] = (memory, DateTime.UtcNow);
            return memory;
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpListenerRequest req, byte[] body, CancellationToken ct)
    {
        // the router asks GET requests about one model to name it; without a name, the chosen model is meant
        var pathAndQuery = req.Url?.PathAndQuery ?? "/";
        if (req.HttpMethod == "GET" && req.Url?.AbsolutePath is "/props" or "/slots" or "/metrics" && !(req.Url?.Query ?? "").Contains("model="))
            pathAndQuery += (pathAndQuery.Contains('?') ? "&" : "?") + "model=" + Uri.EscapeDataString(_defaultModel());
        var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), $"http://127.0.0.1:{_upstreamPort}{pathAndQuery}");
        if (body.Length > 0 || req.HttpMethod is "POST" or "PUT" or "PATCH")
        {
            msg.Content = new ByteArrayContent(body);
            if (req.ContentType is { } type) msg.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        foreach (string? name in req.Headers.AllKeys)
        {
            if (name is null || SkipRequestHeaders.Contains(name)) continue;
            msg.Headers.TryAddWithoutValidation(name, req.Headers.GetValues(name));
        }
        return Upstream.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Copies an answer as it arrives, so a streamed reply reaches the agent token by token.</summary>
    private async Task CopyAsync(HttpResponseMessage from, HttpListenerResponse to)
    {
        CopyHead(from, to);
        to.SendChunked = true;
        await using var src = await from.Content.ReadAsStreamAsync(_cts.Token);
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await src.ReadAsync(buffer, _cts.Token)) > 0)
        {
            await to.OutputStream.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
            await to.OutputStream.FlushAsync(_cts.Token);
        }
    }

    private static async Task WriteAsync(HttpListenerResponse to, HttpResponseMessage head, byte[] body)
    {
        CopyHead(head, to);
        to.ContentLength64 = body.Length;
        await to.OutputStream.WriteAsync(body);
    }

    private static void CopyHead(HttpResponseMessage from, HttpListenerResponse to)
    {
        to.StatusCode = (int)from.StatusCode;
        foreach (var h in from.Headers.Concat(from.Content.Headers))
        {
            if (SkipResponseHeaders.Contains(h.Key)) continue;
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { to.ContentType = string.Join(", ", h.Value); continue; }
            try { to.Headers[h.Key] = string.Join(", ", h.Value); } catch { }
        }
    }

    private static async Task Fail(HttpListenerResponse res, int status, string message)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["error"] = new JsonObject { ["code"] = status, ["message"] = message, ["type"] = "server_error" },
            }.ToJsonString());
            res.StatusCode = status;
            res.ContentType = "application/json";
            res.ContentLength64 = body.Length;
            await res.OutputStream.WriteAsync(body);
        }
        catch { }
    }
}
