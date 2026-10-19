using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

public sealed record EngineInfo(string ServerPath, string Source, string Build)
{
    /// <summary>True for NVIDIA's own build (CUDA); the others run on any card.</summary>
    public bool Nvidia { get; init; }
}

/// <summary>One file of a llama.cpp release: what would be downloaded, with its real size.</summary>
public sealed record EngineAsset(string Name, string Url, long Bytes, string? Sha256);

/// <summary>
/// llama.cpp, the engine that runs a model. One build ships inside the app, so
/// measuring and running work with no download and no network: it draws on any
/// graphics card (Vulkan) and on the processor alone. On a PC with an NVIDIA card
/// the app switches by itself to NVIDIA's own build (CUDA) when that build is on
/// the PC: shipped next to the app, or downloaded once from the official release.
/// </summary>
public static class Engine
{
    // the newest few: "latest" can be a release without binaries, the builds are the bNNNNN ones
    private const string ReleaseApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15";
    private const string Repo = "https://github.com/ggml-org/llama.cpp/";

    private static string AppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio");

    /// <summary>The build that ships with the app.</summary>
    public static string BundledDir => Path.Combine(AppContext.BaseDirectory, "engine");

    /// <summary>A build the user downloaded (a version without a built-in engine, or a newer one).</summary>
    public static string Dir => Path.Combine(AppData, "engine");

    /// <summary>NVIDIA's build when it ships next to the app (the NVIDIA edition).</summary>
    public static string BundledNvidiaDir => Path.Combine(AppContext.BaseDirectory, "engine-nvidia");

    /// <summary>NVIDIA's build when the app downloaded it.</summary>
    public static string NvidiaDir => Path.Combine(AppData, "engine-nvidia");

    /// <summary>Set by the hardware scan: this PC has an NVIDIA card, so its engine is used when present.</summary>
    public static bool PreferNvidia { get; set; }

    // its own client: the Hugging Face token must never travel to another host
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ModelFitStudio");
        return c;
    }

    /// <summary>The llama-server to use on this PC, or null when there is none at all.</summary>
    public static EngineInfo? Find() => (PreferNvidia ? FindNvidia() : null) ?? Plain();

    /// <summary>NVIDIA's build, wherever it is.</summary>
    public static EngineInfo? FindNvidia()
    {
        foreach (var dir in new[] { BundledNvidiaDir, NvidiaDir })
        {
            var exe = Path.Combine(dir, "llama-server.exe");
            if (IsRealExe(exe)) return new EngineInfo(exe, "NVIDIA engine", ReadStamp(dir)) { Nvidia = true };
        }
        return null;
    }

    /// <summary>The number of a llama.cpp build ("b11574" is 11574); 0 when unknown.</summary>
    public static int BuildNumber(string? build) =>
        build is { Length: > 1 } && (build[0] is 'b' or 'B') && int.TryParse(build[1..], out var n) ? n : 0;

    /// <summary>The build for any card: a newer one the app downloaded (an engine update), the built-in one, then one the PC already has.</summary>
    public static EngineInfo? Plain()
    {
        var bundled = Path.Combine(BundledDir, "llama-server.exe");
        var own = Path.Combine(Dir, "llama-server.exe");
        var ownReal = IsRealExe(own);
        if (IsRealExe(bundled))
        {
            if (ownReal && BuildNumber(ReadStamp(Dir)) > BuildNumber(ReadStamp(BundledDir)))
                return new EngineInfo(own, "updated by Models Fit Studio", ReadStamp(Dir));
            return new EngineInfo(bundled, "built in", ReadStamp(BundledDir));
        }

        if (ownReal) return new EngineInfo(own, "downloaded by Models Fit Studio", ReadStamp(Dir));

        var env = Environment.GetEnvironmentVariable("LLAMA_CPP_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var p = Path.Combine(env.Trim(), "llama-server.exe");
            if (IsRealExe(p)) return new EngineInfo(p, "LLAMA_CPP_PATH", "");
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var p = Path.Combine(dir.Trim(), "llama-server.exe");
                if (IsRealExe(p)) return new EngineInfo(p, "PATH", "");
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// What to try when this server could not load a model: NVIDIA's build fails on a
    /// card or driver it does not support, and the built-in one still runs there.
    /// </summary>
    public static string? FallbackFor(string serverExe)
    {
        var nvidia = FindNvidia();
        if (nvidia is null || !string.Equals(nvidia.ServerPath, serverExe, StringComparison.OrdinalIgnoreCase)) return null;
        return Plain()?.ServerPath;
    }

    // Some tools leave placeholders of a few bytes named llama-server.exe. The real one
    // is small too (a launcher of a few KB; the code is in a DLL beside it), so only
    // an empty shell is refused.
    private static bool IsRealExe(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 1024; }
        catch { return false; }
    }

    private static string ReadStamp(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "engine.json")));
            return doc.RootElement.Str("build") ?? "";
        }
        catch { return ""; }
    }

    // ---- the NVIDIA driver ---------------------------------------------------
    /// <summary>The installed NVIDIA driver as NVIDIA numbers it (580.88), or 0 when it cannot be read.</summary>
    public static double NvidiaDriver()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        try
        {
            var smi = Path.Combine(system, "nvidia-smi.exe");
            if (File.Exists(smi))
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = smi,
                    Arguments = "--query-gpu=driver_version --format=csv,noheader",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is not null)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } }
                    var v = NvidiaEngine.DriverVersion(line);
                    if (v > 0) return v;
                }
            }
        }
        catch { }
        try
        {
            var dll = Path.Combine(system, "nvcuda.dll");
            if (File.Exists(dll)) return NvidiaEngine.DriverVersion(FileVersionInfo.GetVersionInfo(dll).FileVersion);
        }
        catch { }
        return 0;
    }

    // ---- downloads -----------------------------------------------------------
    private static async Task<JsonDocument> ReleasesAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(ReleaseApi, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub answered HTTP {(int)resp.StatusCode} for the llama.cpp release list.");
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static string Arch => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                                  System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    private static EngineAsset? Asset(JsonElement release, Func<string, bool> wanted)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.Str("name") ?? "";
            if (!wanted(name)) continue;
            var digest = a.Str("digest");
            return new EngineAsset(name, a.Str("browser_download_url") ?? "", (long)(a.Num("size") ?? 0),
                digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null);
        }
        return null;
    }

    /// <summary>The build that would be downloaded: its name and size, so the user is asked with real numbers.</summary>
    public static async Task<(string Build, string Asset, string Url, long Bytes, string? Sha256)> LatestAsync(bool hasGpu,
        CancellationToken ct = default)
    {
        using var doc = await ReleasesAsync(ct);
        // The build for this device: Vulkan runs on Intel, AMD and NVIDIA alike and carries
        // the CPU code too; without a GPU the CPU build is smaller. ARM PCs get their own.
        var wanted = $"-bin-win-{(hasGpu ? "vulkan" : "cpu")}-{Arch}.zip";
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                // llama.cpp publishes its rolling builds as pre-releases: they are the builds
                if (release.B("draft") == true) continue;
                var a = Asset(release, n => n.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
                                            n.EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
                if (a is not null) return (release.Str("tag_name") ?? "", a.Name, a.Url, a.Bytes, a.Sha256);
            }
        throw new InvalidOperationException($"No recent llama.cpp release has a Windows build named *{wanted}.");
    }

    /// <summary>
    /// NVIDIA's build for one CUDA version: the engine and the CUDA runtime it needs,
    /// two files of the same release.
    /// </summary>
    public static async Task<(string Build, EngineAsset[] Assets)> NvidiaLatestAsync(string cuda, CancellationToken ct = default)
    {
        using var doc = await ReleasesAsync(ct);
        var tail = $"-bin-win-cuda-{cuda}-{Arch}.zip";
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.B("draft") == true) continue;
                var engine = Asset(release, n => n.StartsWith("llama-b", StringComparison.OrdinalIgnoreCase) &&
                                                 n.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
                var runtime = Asset(release, n => n.StartsWith("cudart-llama", StringComparison.OrdinalIgnoreCase) &&
                                                  n.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
                if (engine is not null && runtime is not null) return (release.Str("tag_name") ?? "", [engine, runtime]);
            }
        throw new InvalidOperationException($"No recent llama.cpp release has an NVIDIA build for CUDA {cuda}.");
    }

    private static async Task DownloadAsync(EngineAsset asset, string zip, Action<long>? got, CancellationToken ct)
    {
        if (!asset.Url.StartsWith(Repo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release points outside the llama.cpp repository; nothing was downloaded.");

        using (var resp = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got?.Invoke(n);
            }
        }
        if (asset.Sha256 is null) return;
        await using var f = File.OpenRead(zip);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
        if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The download does not match the hash GitHub publishes for it; it was discarded.");
    }

    /// <summary>Downloads the files, checks each against its published hash, and puts them together in <paramref name="target"/>.</summary>
    private static async Task InstallIntoAsync(string target, string build, EngineAsset[] assets,
        Action<long, long>? progress, CancellationToken ct)
    {
        var staging = target + ".new";
        var zips = new List<string>();
        try
        {
            var total = assets.Sum(a => a.Bytes);
            long done = 0;
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            foreach (var asset in assets)
            {
                var zip = Path.Combine(Path.GetTempPath(), $"modelfit-engine-{Guid.NewGuid():N}.zip");
                zips.Add(zip);
                await DownloadAsync(asset, zip, n => progress?.Invoke(done += n, total), ct);
                ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
                try { File.Delete(zip); } catch { }
            }

            // releases have shipped both flat and with one top folder
            var server = Directory.EnumerateFiles(staging, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException($"{assets[0].Name} contains no llama-server.exe.");
            var root = Path.GetDirectoryName(server)!;
            // the runtime's files belong beside the server, wherever its archive put them
            foreach (var dll in Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories).ToList())
            {
                var beside = Path.Combine(root, Path.GetFileName(dll));
                if (!File.Exists(beside)) File.Move(dll, beside);
            }
            File.WriteAllText(Path.Combine(root, "engine.json"),
                JsonSerializer.Serialize(new { build, asset = assets[0].Name, installed = DateTime.Now.ToString("s") }));

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(root, target);
        }
        finally
        {
            foreach (var zip in zips) { try { File.Delete(zip); } catch { } }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>Downloads the official build into the app's own folder and checks it against the published hash.</summary>
    public static async Task<EngineInfo> InstallAsync(bool hasGpu, Action<long, long>? progress = null,
        CancellationToken ct = default)
    {
        var (build, asset, url, bytes, sha) = await LatestAsync(hasGpu, ct);
        await InstallIntoAsync(Dir, build, [new EngineAsset(asset, url, bytes, sha)], progress, ct);
        return Find() ?? throw new InvalidOperationException("llama.cpp was unpacked but cannot be found.");
    }

    /// <summary>Downloads NVIDIA's build for one CUDA version. It is used from the next model on.</summary>
    public static async Task<EngineInfo> InstallNvidiaAsync(string cuda, Action<long, long>? progress = null,
        CancellationToken ct = default)
    {
        var (build, assets) = await NvidiaLatestAsync(cuda, ct);
        await InstallIntoAsync(NvidiaDir, build, assets, progress, ct);
        return FindNvidia() ?? throw new InvalidOperationException("The NVIDIA engine was unpacked but cannot be found.");
    }

    /// <summary>Deletes a downloaded build. The built-in one, and one found through PATH or LLAMA_CPP_PATH, are not the app's to remove.</summary>
    public static bool Remove() => Delete(Dir);

    public static bool RemoveNvidia() => Delete(NvidiaDir);

    private static bool Delete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, true);
            return true;
        }
        catch { return false; }
    }
}
