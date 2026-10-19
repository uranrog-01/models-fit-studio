using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ModelFitStudio.Core;

public sealed class LlmfitRun
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool Ok => ExitCode == 0 && !string.IsNullOrWhiteSpace(StdOut);
}

public static class LlmfitRunner
{
    public static string ExePath
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "tools", "llmfit.exe");
            if (File.Exists(local)) return local;

            // a published build keeps the engine beside the exe; in a
            // single-file bundle Assembly.Location is empty, so BaseDirectory
            // is the only reliable root
            var beside = Path.Combine(AppContext.BaseDirectory, "llmfit.exe");
            if (File.Exists(beside)) return beside;

            // dev runs land in bin\<cfg>\<tfm>: walk up to a project checkout that still has tools\
            for (var up = new DirectoryInfo(AppContext.BaseDirectory); up is not null; up = up.Parent)
            {
                var upNear = Path.Combine(up.FullName, "tools", "llmfit.exe");
                if (File.Exists(upNear)) return upNear;
            }

            var env = Environment.GetEnvironmentVariable("LLMFIT_EXE");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var cand = Path.Combine(p.Trim(), "llmfit.exe");
                if (File.Exists(cand)) return cand;
            }
            return local;
        }
    }

    public static bool Available => File.Exists(ExePath);

    public static async Task<LlmfitRun> RunAsync(string arguments, string? customModelsPath = null,
        int timeoutMs = 90_000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(customModelsPath))
            psi.EnvironmentVariables["LLMFIT_CUSTOM_MODELS"] = customModelsPath;
        else
            psi.EnvironmentVariables.Remove("LLMFIT_CUSTOM_MODELS");

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            if (!p.Start()) return new LlmfitRun { ExitCode = -1, StdErr = "failed to start llmfit" };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
        }
        catch (TimeoutException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new LlmfitRun { ExitCode = -2, StdErr = $"llmfit timed out after {timeoutMs} ms" };
        }
        catch (Exception ex)
        {
            return new LlmfitRun { ExitCode = -3, StdErr = ex.Message };
        }

        return new LlmfitRun { ExitCode = p.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
    }

    /// <summary>
    /// Runs llmfit and hands every line it writes to <paramref name="onLine"/> as it comes:
    /// for the steps where the user has to read what it says (a share preview, a sign-in code).
    /// </summary>
    public static async Task<int> RunStreamAsync(string arguments, IDictionary<string, string>? env, Action<string> onLine,
        int timeoutMs, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables.Remove("LLMFIT_CUSTOM_MODELS");
        if (env is not null)
            foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        if (!p.Start()) return -1;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            return p.ExitCode;
        }
        catch
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>Run llmfit and parse stdout as JSON. Returns null when it is not valid JSON.
    /// Structured error payloads (e.g. <c>info</c> exit 1 with an <c>invalid_selector</c>
    /// body carrying suggestions) still parse, so callers can surface them.</summary>
    public static async Task<JsonElement?> RunJsonAsync(string arguments, string? customModelsPath = null,
        int timeoutMs = 90_000, CancellationToken ct = default)
    {
        var run = await RunAsync(arguments, customModelsPath, timeoutMs, ct);
        if (string.IsNullOrWhiteSpace(run.StdOut)) return null;
        try
        {
            using var doc = JsonDocument.Parse(run.StdOut);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}

public static class JsonExt
{
    // Nullable-struct friendly wrappers, for rows returned as JsonElement?
    public static double? Num2(this JsonElement? e, string name) => e.HasValue ? e.Value.Num(name) : null;

    public static string? Str2(this JsonElement? e, string name) => e.HasValue ? e.Value.Str(name) : null;

    public static bool Has2(this JsonElement? e, string name) =>
        e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(name, out _);

    public static string? Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    public static double? Num(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static int? Int(this JsonElement e, string name)
    {
        var d = e.Num(name);
        return d.HasValue ? (int)Math.Round(d.Value) : null;
    }

    public static bool? B(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static string[] Arr(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return [];
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return [.. list];
    }
}
