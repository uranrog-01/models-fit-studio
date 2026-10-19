using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// The models on this PC served to AI agents: llama-server's router on 127.0.0.1 behind
/// <see cref="AgentProxy"/>, answering OpenAI-style (/v1/chat/completions, /v1/responses) and
/// Anthropic-style (/v1/messages) requests with a key. Every downloaded model is listed; the
/// chosen one is loaded at start and any other is loaded when an agent asks for it by name
/// (one in memory at a time), as Ollama does. It stops with the app.
/// </summary>
public static partial class AgentServer
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio", "agents");
    public static string KeyFile => Path.Combine(Folder, "key.txt");
    public static string LogFile => Path.Combine(Folder, "server.log");
    private static string PresetFile => Path.Combine(Folder, "models.ini");
    // the router also lists llama.cpp's own download cache; it is pointed at an empty folder so the list is the app's
    private static string EmptyCache => Path.Combine(Folder, "no-cache");

    private static readonly object Gate = new();
    private static Process? _process;
    private static AgentProxy? _proxy;
    /// <summary>Where llama-server itself listens; agents use <see cref="Port"/>, the proxy in front of it.</summary>
    private static int _upstreamPort;
    private static StreamWriter? _log;
    private static readonly Queue<string> Tail = new();
    private static readonly Dictionary<string, int> Contexts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> Reads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"off", "loading", "on" or "error".</summary>
    public static string State { get; private set; } = "off";
    public static string? Error { get; private set; }
    /// <summary>The chosen model: loaded at start, and the one an unknown model name is sent to.</summary>
    public static string? Repo { get; private set; }
    public static string? File { get; private set; }
    public static string ModelId { get; private set; } = "";
    /// <summary>Every model agents can ask for, and the one in memory now.</summary>
    public static IReadOnlyList<string> ModelIds { get; private set; } = [];
    public static string Loaded { get; private set; } = "";
    public static int Port { get; private set; } = AgentPlan.DefaultPort;
    /// <summary>The length asked for (0 = Auto) and the one the chosen model really has, known once it is ready.</summary>
    public static int Asked { get; private set; }
    public static int Context { get; private set; }
    public static bool Gpu { get; private set; }
    public static bool OnCpuInstead { get; private set; }
    /// <summary>The chosen model drafts its own next tokens (MTP) and llama.cpp checks them in one pass.</summary>
    public static bool Drafting { get; private set; }
    public static AgentPlan.Activity Activity { get; private set; } = new();
    /// <summary>Keep long requests short (on by default), and the last time that was done.</summary>
    public static bool FitRequests { get; set; } = true;
    /// <summary>Let a thinking model reason before answering; read when the server starts.</summary>
    public static bool Thinking { get; set; }
    public static AgentProxy.FitEvent? LastFit { get; private set; }

    /// <summary>Raised when the state or the activity changes (from any thread).</summary>
    public static event Action? Changed;

    /// <summary>The key agents send. Made once and kept, so an agent set up yesterday still works today.</summary>
    public static string Key
    {
        get
        {
            try
            {
                if (System.IO.File.Exists(KeyFile))
                {
                    var saved = System.IO.File.ReadAllText(KeyFile).Trim();
                    if (AgentPlan.IsKey(saved)) return saved;
                }
            }
            catch { }
            return NewKey();
        }
    }

    /// <summary>Replaces the key. A running server is restarted by the caller, so the old key stops working.</summary>
    public static string NewKey()
    {
        var key = AgentPlan.NewKey();
        Directory.CreateDirectory(Folder);
        System.IO.File.WriteAllText(KeyFile, key + Environment.NewLine);
        return key;
    }

    /// <summary>A downloaded model to serve; <paramref name="Read12k"/> is a measured 12,000-token read here, 0 when unknown.</summary>
    public sealed record Offer(string Repo, string File, string Path, double Read12k);

    /// <summary>Seconds this model takes to read 12,000 tokens here, as measured (Best for agents) or seen while serving; 0 when unknown.</summary>
    public static double Read12kFor(string? id)
    {
        lock (Gate) return id is not null && Reads.TryGetValue(id, out var r) ? r : 0;
    }

    /// <summary>The conversation length of a model once it has been loaded; 0 until then.</summary>
    public static int ContextFor(string? id)
    {
        lock (Gate) return id is not null && Contexts.TryGetValue(id, out var c) ? c : 0;
    }

    /// <summary>
    /// Starts the router with every model in <paramref name="offers"/> and waits until the chosen one
    /// answers. On a graphics failure the built-in engine is tried, then the processor; a chosen model
    /// with draft layers that will not load with them is tried without.
    /// </summary>
    public static async Task StartAsync(string serverExe, string? fallbackExe, IReadOnlyList<Offer> offers, string repo, string file,
        int context, bool gpu, bool integratedGpu, int preferredPort)
    {
        // a restart (new key, other model) must keep the address: wait for the old server to let go of the port
        if (Stop()) await WaitForPortAsync(preferredPort);
        _ = Key;   // make sure the key file exists before llama-server reads it

        var named = AgentPlan.UniqueIds(offers.Select(o => (o.File, o)));
        var chosen = named.FirstOrDefault(n => n.item.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) &&
                                               n.item.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (chosen.item is null) throw new InvalidOperationException("That model is not in the list of downloaded models.");
        var models = named.Select(n => new AgentPlan.ServeModel(n.id, n.item.Path,
            AgentPlan.PlainAttention(n.item.File, integratedGpu), GgufInfo.TryRead(n.item.Path)?.HasMtp == true)).ToList();

        lock (Gate)
        {
            Repo = repo;
            File = file;
            ModelId = chosen.id;
            ModelIds = models.Select(m => m.Id).ToList();
            Loaded = "";
            Asked = AgentPlan.ClampContext(context);
            Context = Asked;
            Gpu = gpu;
            OnCpuInstead = false;
            Drafting = false;
            Error = null;
            Activity = new AgentPlan.Activity();
            Port = FreePort(preferredPort);
            _upstreamPort = FreePort(Port + 1000);
            LastFit = null;
            Contexts.Clear();
            Reads.Clear();
            foreach (var (id, o) in named) if (o.Read12k > 0) Reads[id] = o.Read12k;
            State = "loading";
        }
        Changed?.Invoke();

        var mtp = models.First(m => m.Id == chosen.id).Mtp;
        var tries = new List<(string exe, bool gpu, bool mtp)> { (serverExe, gpu, mtp) };
        if (mtp) tries.Add((serverExe, gpu, false));
        if (gpu && fallbackExe is not null) tries.Add((fallbackExe, true, false));
        if (gpu) tries.Add((fallbackExe ?? serverExe, false, false));

        string reason = "It gave no reason.";
        foreach (var (exe, onGpu, drafts) in tries)
        {
            var list = models.Select(m => m.Id == chosen.id && !drafts ? m with { Mtp = false } : m);
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllText(PresetFile, AgentPlan.Preset(list, chosen.id, Asked, onGpu, Thinking));
            var p = Launch(exe);
            var outcome = await WaitUntilReadyAsync(p, chosen.id);
            if (outcome is null)
            {
                var real = await ReadContextAsync(chosen.id);
                // the agents' address opens only now, in front of a model that is ready
                AgentProxy proxy;
                try
                {
                    proxy = new AgentProxy(Port, _upstreamPort, () => FitRequests, OnFitted,
                        model => ContextFor(model ?? ModelId), model => Read12kFor(model ?? ModelId),
                        () => ModelId, () => ModelIds);
                }
                catch (Exception ex)
                {
                    Kill(p);
                    lock (Gate)
                    {
                        _process = null;
                        State = "error";
                        Error = $"Port {Port} could not be opened for agents: {ex.Message}";
                    }
                    Changed?.Invoke();
                    return;
                }
                lock (Gate)
                {
                    if (!ReferenceEquals(_process, p))
                    {
                        proxy.Dispose();   // stopped by the user meanwhile
                        return;
                    }
                    _proxy = proxy;
                    Gpu = onGpu;
                    OnCpuInstead = gpu && !onGpu;
                    Drafting = drafts;
                    if (real > 0)
                    {
                        Context = real;
                        Contexts[chosen.id] = real;
                    }
                    State = "on";
                }
                Changed?.Invoke();
                return;
            }
            reason = outcome;
            if (!ReferenceEquals(_process, p)) return;   // stopped by the user while loading
            Kill(p);
        }
        lock (Gate)
        {
            State = "error";
            Error = "The model did not load. " + reason;
            _process = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops the server and every model it loaded; true when one was running.</summary>
    public static bool Stop()
    {
        Process? p;
        lock (Gate)
        {
            p = _process;
            _process = null;
            State = "off";
            Error = null;
            Loaded = "";
            Activity = new AgentPlan.Activity();
        }
        StopProxy();
        if (p is not null) Kill(p);
        SweepLeftovers();
        Changed?.Invoke();
        return p is not null;
    }

    /// <summary>
    /// Ends any agent server this app started that is still running without the app knowing it (from a
    /// run that ended badly): a llama-server whose command line names this app's agents folder. A model
    /// started from the Library's Run, or by another program, is left alone.
    /// </summary>
    public static void SweepLeftovers()
    {
        try
        {
            var mine = Environment.ProcessId;
            using var search = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'llama-server.exe'");
            var all = search.Get().Cast<System.Management.ManagementObject>()
                .Select(o => (pid: Convert.ToInt32(o["ProcessId"]), parent: Convert.ToInt32(o["ParentProcessId"]), cmd: o["CommandLine"] as string ?? ""))
                .ToList();
            var routers = all.Where(x => x.cmd.Contains(Folder, StringComparison.OrdinalIgnoreCase)).Select(x => x.pid).ToHashSet();
            foreach (var (pid, parent, _) in all)
            {
                // the router itself, or a model it spawned (a child whose router is among them or gone)
                if (!routers.Contains(pid) && !routers.Contains(parent)) continue;
                if (pid == mine) continue;
                try { using var proc = Process.GetProcessById(pid); proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { /* WMI not available: the job object still ends them with the app */ }
    }

    private static void StopProxy()
    {
        var proxy = Interlocked.Exchange(ref _proxy, null);
        try { proxy?.Dispose(); } catch { }
    }

    private static void OnFitted(AgentProxy.FitEvent e)
    {
        LastFit = e;
        Changed?.Invoke();
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (var i = 0; i < 25 && !IsFree(port); i++) await Task.Delay(200);
    }

    private static Process Launch(string exe)
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(EmptyCache);
        lock (Tail) Tail.Clear();
        try { _log?.Dispose(); } catch { }
        try { _log = new StreamWriter(LogFile, append: false) { AutoFlush = true }; } catch { _log = null; }

        var args = AgentPlan.RouterArguments(_upstreamPort, PresetFile, KeyFile);
        _log?.WriteLine($"> llama-server {args}");
        try { _log?.WriteLine(System.IO.File.ReadAllText(PresetFile)); } catch { }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["LLAMA_CACHE"] = EmptyCache;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(p, e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(p, e.Data);
        p.Exited += (_, _) =>
        {
            var changed = false;
            lock (Gate)
            {
                // a server that dies on its own while serving is an error the page shows
                if (ReferenceEquals(_process, p) && State == "on")
                {
                    State = "error";
                    Error = "The server stopped by itself. " + LastReason();
                    _process = null;
                    changed = true;
                }
            }
            if (changed)
            {
                StopProxy();
                Changed?.Invoke();
            }
        };
        if (!p.Start()) throw new InvalidOperationException("llama-server did not start.");
        KillWithApp.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (Gate) _process = p;
        return p;
    }

    [GeneratedRegex(@"spawning server instance with name=(\S+)")]
    private static partial Regex SpawnRx();

    [GeneratedRegex(@"instance name=(\S+) exited with status (-?\d+)")]
    private static partial Regex ExitRx();

    private static void OnLine(Process p, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try { _log?.WriteLine(line); } catch { }
        lock (Tail)
        {
            Tail.Enqueue(line.Trim());
            while (Tail.Count > 60) Tail.Dequeue();
        }
        if (!ReferenceEquals(_process, p)) return;
        var changed = false;
        lock (Gate)
        {
            if (SpawnRx().Match(line) is { Success: true } spawn)
            {
                Loaded = spawn.Groups[1].Value;
                changed = true;
            }
            else if (ExitRx().Match(line) is { Success: true } exit && exit.Groups[1].Value == Loaded)
            {
                Loaded = "";
                changed = true;
            }
            if (AgentPlan.Read(Activity, line))
            {
                changed = true;
                // a long read tells how fast this model reads here: the next requests are sized from it
                if (line.Contains("prompt eval time") && Activity.LastRead >= 3000 && Loaded.Length > 0)
                {
                    var seen = AgentPlan.Read12kFrom(Activity.LastRead, Activity.LastReadSeconds);
                    if (seen > 0)
                        Reads[Loaded] = Reads.TryGetValue(Loaded, out var before) && before > 0 ? Math.Round((before + seen) / 2, 1) : seen;
                }
            }
        }
        if (changed) Changed?.Invoke();
        if (line.Contains("spawning server instance") || line.Contains("listening on")) _ = LearnContextAsync();
    }

    // the length of a model the router just loaded, for keeping requests to it short
    private static async Task LearnContextAsync()
    {
        await Task.Delay(1500);
        var id = Loaded;
        if (id.Length == 0 || ContextFor(id) > 0) return;
        var n = await ReadContextAsync(id, autoload: false);
        if (n > 0) lock (Gate) Contexts[id] = n;
    }

    /// <summary>Null when the chosen model is loaded and answers; otherwise why it did not.</summary>
    private static async Task<string?> WaitUntilReadyAsync(Process p, string id)
    {
        var deadline = DateTime.UtcNow.AddMinutes(8);
        while (DateTime.UtcNow < deadline)
        {
            if (!ReferenceEquals(_process, p)) return "Stopped.";
            if (p.HasExited) return LastReason();
            lock (Tail)
                if (Tail.Select(l => ExitRx().Match(l)).FirstOrDefault(m => m.Success && m.Groups[1].Value == id && m.Groups[2].Value != "0") is not null)
                    return LastReason();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_upstreamPort}/models");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);
                using var r = await ModelServer.Http.SendAsync(req);
                if (r.StatusCode == HttpStatusCode.OK)
                {
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                        if (m.GetProperty("id").GetString() == id &&
                            m.TryGetProperty("status", out var st) && st.TryGetProperty("value", out var v) && v.GetString() == "loaded")
                        {
                            lock (Gate) Loaded = id;
                            return null;
                        }
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            await Task.Delay(500);
        }
        return "It did not finish loading in 8 minutes.";
    }

    /// <summary>The conversation length a model settled on (with Auto, llama.cpp chooses it); 0 when it does not say.</summary>
    private static async Task<int> ReadContextAsync(string id, bool autoload = true)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"http://127.0.0.1:{_upstreamPort}/props?model={Uri.EscapeDataString(id)}&autoload={(autoload ? "true" : "false")}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);
            using var r = await ModelServer.Http.SendAsync(req);
            if (!r.IsSuccessStatusCode) return 0;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("default_generation_settings", out var g) &&
                   g.TryGetProperty("n_ctx", out var n) && n.TryGetInt32(out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static string LastReason()
    {
        lock (Tail)
        {
            var hit = Tail.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                              l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                              l.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                                              l.Contains("out of memory", StringComparison.OrdinalIgnoreCase));
            return hit is null ? "It gave no reason." : "It said: " + ModelServer.Short(hit);
        }
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch { }
        try { p.Dispose(); } catch { }
    }

    private static bool IsFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>The port asked for when it is free, else the next free one after it (only when another program holds it).</summary>
    private static int FreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
            if (IsFree(port)) return port;
        var any = new TcpListener(IPAddress.Loopback, 0);
        any.Start();
        var free = ((IPEndPoint)any.LocalEndpoint).Port;
        any.Stop();
        return free;
    }

    /// <summary>
    /// A Windows job that ends its processes when the app ends, however it ends:
    /// without it, a killed app left llama-server holding the model in memory.
    /// </summary>
    private static class KillWithApp
    {
        private static readonly IntPtr Job = Create();

        public static void Add(Process p)
        {
            if (Job == IntPtr.Zero) return;
            try { AssignProcessToJobObject(Job, p.Handle); } catch { }
        }

        private static IntPtr Create()
        {
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
                }
                finally { Marshal.FreeHGlobal(ptr); }
                return job;
            }
            catch { return IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
