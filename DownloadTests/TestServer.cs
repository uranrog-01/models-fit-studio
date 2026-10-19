using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DownloadTests;

/// <summary>
/// A minimal stand-in for Hugging Face's CDN: one big file behind one URL,
/// honoring Range requests (206), If-Range, and ETags — with fault switches
/// for dropping connections mid-stream or ignoring Range entirely.
/// </summary>
public sealed class TestServer : IDisposable
{
    private WebApplication? _app;
    public int Port { get; private set; }

    public byte[] Payload = [];
    public string Etag = "\"etag-empty\"";

    /// <summary>When > 0, the next N responses stream a few KB and then abort.</summary>
    public int DropNext;
    public int StallNext;
    public int StallHeadersNext;
    public int WrongRangeNext;

    /// <summary>When true, Range headers are ignored — every response is 200 with the full body.</summary>
    public bool IgnoreRange;

    /// <summary>Per-chunk delay (ms) that simulates a slow real-world pipe.</summary>
    public int SlowMs;

    public string Url => $"http://127.0.0.1:{Port}/dl";

    public async Task StartAsync()
    {
        // a high, unassigned port
        for (Port = 19000; Port < 19100; Port++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, Port);
                l.Start(); l.Stop();
                break;
            }
            catch { }
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        var app = builder.Build();

        app.MapGet("/dl", async (HttpContext ctx) =>
        {
            var resp = ctx.Response;
            resp.Headers.ETag = Etag;

            long? rangeStart = null;
            long? rangeEnd = null;
            if (!IgnoreRange)
            {
                var raw = ctx.Request.Headers.Range.ToString();
                if (raw.StartsWith("bytes="))
                {
                    var bounds = raw[6..].Split('-');
                    if (long.TryParse(bounds[0], out var s) && s >= 0 && s < Payload.Length) rangeStart = s;
                    if (bounds.Length > 1 && long.TryParse(bounds[1], out var end)) rangeEnd = end;
                }
            }

            // If-Range: an old ETag means the entity changed — serve the whole
            // body with 200, exactly like the real CDN does
            var ifRange = ctx.Request.Headers.IfRange.ToString();
            if (rangeStart is not null && ifRange.Length > 0 && ifRange != Etag) rangeStart = null;

            long from = rangeStart ?? 0;
            long to = rangeStart is not null ? Math.Min(rangeEnd ?? Payload.Length - 1, Payload.Length - 1) : Payload.Length - 1;
            resp.StatusCode = rangeStart is not null ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
            resp.ContentType = "application/octet-stream";
            if (rangeStart is not null)
                resp.Headers.ContentRange = $"bytes {from}-{to}/{Payload.Length}";
            resp.Headers.ContentLength = to - from + 1;

            if (WrongRangeNext > 0 && rangeStart is not null)
            {
                WrongRangeNext--;
                resp.Headers.ContentRange = $"bytes {from + 1}-{to + 1}/{Payload.Length}";
            }
            if (StallHeadersNext > 0)
            {
                StallHeadersNext--;
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                return;
            }
            if (StallNext > 0)
            {
                StallNext--;
                await resp.StartAsync();
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                return;
            }

            if (DropNext > 0)
            {
                DropNext--;
                await resp.Body.WriteAsync(Payload.AsMemory((int)from, (int)Math.Min(64_000, to - from + 1)));
                ctx.Abort(); // kill the connection mid-stream
                return;
            }

            if (SlowMs <= 0)
            {
                await resp.Body.WriteAsync(Payload.AsMemory((int)from, (int)(to - from + 1)));
                return;
            }

            // drip the body out in 256 KB chunks so a mid-stream pause lands
            // reliably on loopback
            const int chunk = 256 * 1024;
            for (var off = from; off <= to; off += chunk)
            {
                var n = (int)Math.Min(chunk, to - off + 1);
                await resp.Body.WriteAsync(Payload.AsMemory((int)off, n));
                await Task.Delay(SlowMs);
            }
        });

        await app.StartAsync();
        _app = app;
    }

    public void SetPayload(byte[] data)
    {
        Payload = data;
        Etag = $"\"{Convert.ToHexString(SHA256.HashData(data))[..16]}\"";
    }

    public void Dispose()
    {
        try { _app?.StopAsync().Wait(2000); } catch { }
        try { _app?.DisposeAsync().AsTask().Wait(2000); } catch { }
    }
}
