using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace ModelFitStudio.Core;

/// <summary>A model the app started in llama.cpp's server: where its chat page is, and the process behind it.</summary>
public sealed record LocalRun(string Repo, string File, string Url, int Context, bool Gpu, Process Process)
{
    /// <summary>True once the weights are loaded and the chat page answers.</summary>
    public bool Ready { get; set; }
}

/// <summary>
/// The models the app started on this PC. Each runs in llama.cpp's own console
/// window, so the user can always see it and close it; the app only remembers
/// which ones are alive, to offer "Open chat" and "Stop" for them.
/// </summary>
public static class LocalRuns
{
    private static readonly ConcurrentDictionary<string, LocalRun> Runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a model starts, becomes ready, or stops (by the app or by closing its window).</summary>
    public static event Action? Changed;

    private static string Key(string repo, string file) => repo + "/" + file;

    public static LocalRun? Find(string repo, string file) =>
        Runs.TryGetValue(Key(repo, file), out var run) && Alive(run) ? run : null;

    public static IReadOnlyList<LocalRun> List() => Runs.Values.Where(Alive).ToList();

    private static bool Alive(LocalRun run)
    {
        try { return !run.Process.HasExited; }
        catch { return false; }
    }

    /// <summary>The arguments llama-server gets; the same line the page shows as the command.</summary>
    public static string Arguments(string modelPath, int port, int context, bool gpu) =>
        $"-m \"{modelPath}\" --host 127.0.0.1 --port {port} -c {Math.Clamp(context, 2048, 32768)} -ngl {(gpu ? 99 : 0)} --jinja";

    public static LocalRun Start(string serverExe, string modelPath, string repo, string file, int context, bool gpu)
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = serverExe,
            Arguments = Arguments(modelPath, port, context, gpu),
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
            UseShellExecute = true,   // its own console window: the user sees it and closes it
        }) ?? throw new InvalidOperationException("llama.cpp did not start.");

        var run = new LocalRun(repo, file, $"http://127.0.0.1:{port}", Math.Clamp(context, 2048, 32768), gpu, process);
        Runs[Key(repo, file)] = run;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // only this run's own entry: the same file may have been started again since
            Runs.TryRemove(new KeyValuePair<string, LocalRun>(Key(repo, file), run));
            Changed?.Invoke();
        };
        Changed?.Invoke();
        return run;
    }

    public static void MarkReady(LocalRun run)
    {
        run.Ready = true;
        Changed?.Invoke();
    }

    public static bool Stop(string repo, string file)
    {
        if (!Runs.TryRemove(Key(repo, file), out var run)) return false;
        try
        {
            if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true);
        }
        catch { }
        Changed?.Invoke();
        return true;
    }
}

/// <summary>Other programs on this PC that can run a downloaded model.</summary>
public static class LocalTools
{
    /// <summary>LM Studio's command-line tool, which can add a file to LM Studio's own list.</summary>
    public static string? Lms() => FirstExisting(
        Path.Combine(Home, ".lmstudio", "bin", "lms.exe"),
        Path.Combine(Home, ".cache", "lm-studio", "bin", "lms.exe")) ?? OnPath("lms.exe");

    public static string? Ollama() => FirstExisting(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"))
        ?? OnPath("ollama.exe");

    /// <summary>
    /// How LM Studio should take a file it is given: a hard link costs no disk space but
    /// needs both folders on one drive; otherwise the file is copied.
    /// </summary>
    public static string LmsImportMode(string modelPath)
    {
        var a = Path.GetPathRoot(Path.GetFullPath(modelPath));
        var b = Path.GetPathRoot(Home);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? "--hard-link" : "--copy";
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
