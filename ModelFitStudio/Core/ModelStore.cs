using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

public enum DownloadState { Queued, Downloading, Paused, Done, Failed, Cancelled }

public sealed class DownloadProgress
{
    public string Repo { get; init; } = "";
    public string File { get; init; } = "";
    public DownloadState State { get; set; }
    public long Received { get; set; }
    public long Total { get; set; }
    public string? Error { get; set; }
    /// <summary>Short human note: retry countdowns, "resuming from 12.4 GB", pause reasons.</summary>
    public string? Note { get; set; }
    /// <summary>Set by the pause button; the transfer loop honors it over a plain cancel.</summary>
    public bool PauseRequested { get; set; }
    public double Fraction => Total > 0 ? Math.Min(1, Received / (double)Total) : 0;
    public double MbPerSec { get; set; }
    public string Path { get; set; } = "";
}

/// <summary>
/// Downloads GGUF files from Hugging Face into a local folder, resuming a
/// partial file instead of starting it over.
/// </summary>
public static class ModelStore
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModelFitStudio", "store-settings.json");

    /// <summary>Token provider hook (set by host app or tests).</summary>
    public static Func<string?>? HfTokenProvider = null;

    public static string? GetHfToken()
    {
        if (HfTokenProvider != null) return HfTokenProvider();
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ModelFitStudio", "explorer-settings.json");
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("HfToken", out var tok) && tok.GetString() is { Length: > 0 } s)
                    return s;
                if (doc.RootElement.TryGetProperty("hfToken", out var tok2) && tok2.GetString() is { Length: > 0 } s2)
                    return s2;
            }
        }
        catch { }
        return Environment.GetEnvironmentVariable("HF_TOKEN");
    }

    private static string? _customRootDir;

    public static string RootDir
    {
        get
        {
            if (_customRootDir is not null) return _customRootDir;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                    if (doc.RootElement.TryGetProperty("rootDir", out var p) && p.GetString() is { Length: > 0 } custom && Directory.Exists(custom))
                        return _customRootDir = custom;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio", "Models");
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                Directory.CreateDirectory(value);
                _customRootDir = value;
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { rootDir = value }));
            }
            catch { }
        }
    }

    public static double FreeDiskSpaceGb(string? path = null)
    {
        try
        {
            var target = path ?? RootDir;
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (root is null) return 0;
            return new DriveInfo(root).AvailableFreeSpace / 1e9;
        }
        catch { return 0; }
    }

    // mirrors keep the original org in the repo name ("bartowski/microsoft_Phi-4-GGUF");
    // flattening it to one directory keeps paths short and legal on Windows
    public static string RepoDir(string repo) =>
        Path.Combine(RootDir, repo.Replace('/', '_').Replace(" ", ""));

    /// <summary>Where a file is: in this app's folder, else in zer0ne code's when it is only there.</summary>
    public static string ResolvePath(string repo, string file)
    {
        var dir = RepoDir(repo);
        var safe = string.Join("_", (file ?? "").Split(Path.GetInvalidFileNameChars()));
        var own = Path.Combine(dir, safe);
        if (File.Exists(own) || File.Exists(own + ".part")) return own;
        if (SharedPath(repo, file ?? "") is { } shared && File.Exists(shared)) return shared;
        // a model another app downloaded (LM Studio, Ollama, the Hugging Face cache…), used where it is
        return ExternalModels.PathOf(repo, file ?? "") ?? own;
    }

    // ---- one library for two apps -----------------------------------------------
    private static bool _sharedOff;

    /// <summary>
    /// zer0ne code's models folder, when that app is on this PC. Its files show in the
    /// Library and can be run, measured and tested here. They are used where they are,
    /// never copied; zer0ne code lists this app's folder the same way.
    /// MODELFIT_SHARED_DIR points somewhere else, or nowhere when it is empty.
    /// </summary>
    public static string? SharedDir
    {
        get
        {
            if (_sharedOff) return null;
            if (Environment.GetEnvironmentVariable("MODELFIT_SHARED_DIR") is { } set)
                return set.Length > 0 && Directory.Exists(set) ? set : null;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "zer0ne Code", "data", "global", "models");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PlainRepo = new(@"^[\w.\-]+/[\w.\-]+$");

    /// <summary>zer0ne code keeps a file as models\org\repo\file. Null when the names could leave that folder.</summary>
    private static string? SharedPath(string repo, string file)
    {
        if (SharedDir is not { } root || !PlainRepo.IsMatch(repo ?? "") || repo!.Contains("..")) return null;
        if (file.Length == 0 || file.Contains("..") || file.IndexOfAny(['/', '\\', ':']) >= 0) return null;
        var parts = repo.Split('/');
        return Path.Combine(root, parts[0], parts[1], file);
    }

    /// <summary>True when the file is in zer0ne code's folder, not this app's.</summary>
    public static bool IsShared(string repo, string file) =>
        SharedDir is { } root && ResolvePath(repo, file).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // a download leaves "<file>.part" until it completes, so the presence of the
    // plain file is by itself the finished signal
    public static bool HasFile(string repo, string file)
    {
        var p = ResolvePath(repo, file);
        return File.Exists(p) && !File.Exists(p + ".part");
    }

    /// <summary>Test seam: unit tests point the downloader at a local server.</summary>
    internal static Func<string, string, string>? DownloadUrlOverride = null; // (repo, file) -> url
    internal static TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    internal static TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    internal static long ChunkBytes = 32L * 1024 * 1024;
    internal static void SetTestRoot(string path)
    {
        Directory.CreateDirectory(path);
        _customRootDir = path; // tests must not overwrite the user's store-settings.json
        _sharedOff = true;     // nor see the models another app keeps on this PC
    }

    public static string DownloadUrl(string repo, string file) =>
        DownloadUrlOverride?.Invoke(repo, file) ??
        $"https://huggingface.co/{repo}/resolve/main/{EscapePath(file)}?download=true";

    // repositories keep quants in subfolders often enough ("Q4_K_M/model.gguf");
    // escaping the whole string would turn those separators into %2F
    private static string EscapePath(string file) =>
        string.Join("/", (file ?? "").Split('/').Select(Uri.EscapeDataString));

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    static ModelStore()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ModelFitStudio/1.0");
    }

    public static Task<bool> IsCompleteAsync(string repo, string file, CancellationToken ct = default) =>
        Task.FromResult(HasFile(repo, file));

    public static async Task<DownloadProgress> DownloadAsync(string repo, string file, DownloadProgress progress,
        CancellationToken ct = default)
    {
        var dest = ResolvePath(repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        progress.Path = dest;

        var part = dest + ".part";
        var meta = part + ".meta";

        const int maxAttempts = 10;
        var attempt = 0;

        // One download = many HTTP attempts. Every attempt re-stats the .part
        // file and asks for the missing tail with a Range header, so a dropped
        // connection at hour five of a 20 GB pull costs seconds, not the file.
        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();
            if (progress.PauseRequested) throw new OperationCanceledException();

            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            try
            {
                var complete = await PullOnceAsync(dest, part, meta, repo, file, progress, have, attempt, ct);
                if (!complete) { attempt = 0; continue; }
                progress.State = DownloadState.Done;
                progress.Note = null;
                try { File.Delete(meta); } catch { }
                return progress;
            }
            catch (OperationCanceledException)
            {
                progress.MbPerSec = 0;
                // the pause button wins over a plain cancel so Resume knows
                // the .part is intentionally kept
                if (progress.PauseRequested) progress.State = DownloadState.Paused;
                else progress.State = DownloadState.Cancelled;
                return progress;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("GATED_MODEL"))
            {
                progress.MbPerSec = 0;
                progress.State = DownloadState.Failed;
                progress.Error = ex.Message;
                progress.Note = null;
                return progress;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                progress.MbPerSec = 0;
                var delay = Math.Min(30, 2 * attempt);
                progress.Note = $"connection problem — retrying in {delay}s (attempt {attempt} of {maxAttempts})";
                progress.Error = ex.Message;
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException)
                {
                    progress.State = progress.PauseRequested ? DownloadState.Paused : DownloadState.Cancelled;
                    return progress;
                }
            }
            catch (Exception ex)
            {
                progress.State = DownloadState.Failed;
                progress.Error = ex.Message;
                progress.Note = null;
                return progress;
            }
        }
    }

    /// <summary>One HTTP attempt: validate any resume, then stream the tail.</summary>
    private static async Task<bool> PullOnceAsync(string dest, string part, string meta,
        string repo, string file, DownloadProgress progress, long have,
        int attempt, CancellationToken ct)
    {
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transfer.CancelAfter(HeaderTimeout);
        try
        {
        using var req = new HttpRequestMessage(HttpMethod.Get, DownloadUrl(repo, file));
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, checked(have + ChunkBytes - 1));
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        var hfTok = GetHfToken();
        if (!string.IsNullOrWhiteSpace(hfTok))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hfTok.Trim());
        if (have > 0)
        {
            // If-Range lets the server decide: same entity -> 206 with the tail,
            // changed entity -> 200 with the whole file (handled below)
            var etag = ReadMeta(meta);
            if (!string.IsNullOrEmpty(etag))
                req.Headers.TryAddWithoutValidation("If-Range", etag);
            progress.Note = $"resuming from {Fbytes(have)}";
        }
        else progress.Note = attempt > 1 ? "restarting after a failed attempt" : null;

        progress.Note ??= "connecting to Hugging Face…";
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, transfer.Token);
        transfer.CancelAfter(IdleTimeout);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"GATED_MODEL: Hugging Face blocked access to '{repo}' (HTTP {(int)resp.StatusCode}). This model is gated and requires an authorized User Access Token.");

        long total;
        long alreadyHave = have;
        var destMode = FileMode.Append;

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 &&
            resp.Content.Headers.ContentRange?.Length == have)
        {
            File.Move(part, dest, overwrite: true);
            progress.Total = progress.Received = have;
            return true;
        }
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = resp.Content.Headers.ContentRange;
            if (range?.From != have || range.To is null || range.To < have)
                throw new IOException("Server returned the wrong byte range; keeping the saved partial file.");
            total = range.Length ?? progress.Total;
        }
        else if (have > 0 && resp.StatusCode == HttpStatusCode.OK)
        {
            // No range support, or the remote file changed — the server sent
            // the whole body. Appending it to the old partial would corrupt
            // the file, so restart from zero instead.
            alreadyHave = 0;
            total = resp.Content.Headers.ContentLength ?? progress.Total;
            destMode = FileMode.Create;
            progress.Note = "remote file changed — restarting";
        }
        else if (resp.StatusCode == HttpStatusCode.OK)
        {
            total = resp.Content.Headers.ContentLength ?? progress.Total;
        }
        else
        {
            throw new InvalidOperationException($"Server returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        var newEtag = resp.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(newEtag)) WriteMeta(meta, newEtag);

        progress.Total = total > 0 ? total : progress.Total;
        progress.Received = alreadyHave;
        progress.Error = null;

        // speed is measured per attempt, so a resume shows the current rate
        // instead of an average dragged down by the hours already spent
        var sw = Stopwatch.StartNew();
        long attemptBytes = 0;

        await using (var fs = new FileStream(part, destMode, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            int read;
            await using var stream = await resp.Content.ReadAsStreamAsync(transfer.Token);
            while ((read = await stream.ReadAsync(buffer, transfer.Token)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), transfer.Token);
                transfer.CancelAfter(IdleTimeout);
                progress.Received += read;
                attemptBytes += read;
                if (sw.Elapsed.TotalSeconds > 0.5)
                {
                    progress.MbPerSec = Math.Round(attemptBytes / 1048576.0 / sw.Elapsed.TotalSeconds, 1);
                    progress.Note = null; // streaming happily - clear any resume note
                }
            }
            await fs.FlushAsync(transfer.Token);
        }

        if (resp.Content.Headers.ContentLength is { } expected && attemptBytes != expected)
            throw new InvalidOperationException(
                $"Download stopped short: received {attemptBytes} of {expected} bytes in this transfer.");

        var saved = new FileInfo(part).Length;
        if (total > 0 && saved < total && resp.StatusCode == HttpStatusCode.PartialContent) return false;
        if (total > 0 && saved != total)
            throw new IOException($"Downloaded size {saved} does not match the expected {total} bytes.");

        File.Move(part, dest, overwrite: true);
        progress.MbPerSec = 0;
        return true;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Download connection stalled; reconnecting from the saved bytes.", ex);
        }
    }

    /// <summary>Removes a partial download (and its ETag) but never a finished file.</summary>
    public static bool DiscardPartial(string repo, string file)
    {
        var part = ResolvePath(repo, file) + ".part";
        var deleted = false;
        foreach (var p in new[] { part, part + ".meta" })
        {
            try { if (File.Exists(p)) { File.Delete(p); deleted = true; } } catch { }
        }
        return deleted;
    }

    /// <summary>The .part length on disk - what a restored queue row starts from.</summary>
    public static long PartialBytes(string repo, string file)
    {
        try
        {
            var part = ResolvePath(repo, file) + ".part";
            return File.Exists(part) ? new FileInfo(part).Length : 0;
        }
        catch { return 0; }
    }

    private static string? ReadMeta(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static void WriteMeta(string path, string value)
    {
        try { File.WriteAllText(path, value); } catch { }
    }

    public static string Fbytes(long b) => b >= 1073741824 ? $"{b / 1073741824.0:0.#} GB"
        : b >= 1048576 ? $"{b / 1048576.0:0.#} MB"
        : $"{b / 1024.0:0} KB";

    // HF advertises true sizes only over the API, and the redirect already gave us
    // Content-Length; this keeps the file row from showing a blank size
    public static async Task<long> RemoteSizeAsync(string repo, string file)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, DownloadUrl(repo, file));
            using var timeout = new CancellationTokenSource(HeaderTimeout);
            using var resp = await Http.SendAsync(req, timeout.Token);
            return resp.Content.Headers.ContentLength ?? 0;
        }
        catch { return 0; }
    }

    /// <param name="Shared">The file is in zer0ne code's folder and is used from there.</param>
    /// <param name="Source">The app that downloaded a file found outside this app's folders ("LM Studio", "Ollama"…); null for this app's own.</param>
    public sealed record InstalledFile(string Repo, string File, long Bytes, DateTime Written, bool Complete, bool Shared = false, string? Source = null);

    /// <summary>True when the file belongs to another app: this app uses it but never deletes it.</summary>
    public static bool IsExternal(string repo, string file) => ExternalModels.PathOf(repo, file) is { } p &&
        string.Equals(ResolvePath(repo, file), p, StringComparison.OrdinalIgnoreCase);

    public static List<InstalledFile> ListInstalled()
    {
        var outList = new List<InstalledFile>();
        foreach (var dir in Directory.Exists(RootDir) ? Directory.EnumerateDirectories(RootDir) : [])
        {
            // only the first separator stands for "org/repo" — mirrors keep
            // underscores in the repo part ("bartowski/microsoft_Phi-4-GGUF")
            var flat = Path.GetFileName(dir);
            var cut = flat.IndexOf('_');
            var repo = cut > 0 ? flat[..cut] + "/" + flat[(cut + 1)..] : flat;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".part")) continue;
                if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(f);
                outList.Add(new InstalledFile(repo, name, info.Length, info.LastWriteTime,
                    !File.Exists(f + ".part")));
            }
        }
        // what zer0ne code downloaded: models\org\repo\file.gguf. A file both apps have is this app's.
        try
        {
            foreach (var org in SharedDir is { } root ? Directory.EnumerateDirectories(root) : [])
                foreach (var dir in Directory.EnumerateDirectories(org))
                {
                    var repo = Path.GetFileName(org) + "/" + Path.GetFileName(dir);
                    foreach (var f in Directory.EnumerateFiles(dir, "*.gguf"))
                    {
                        var name = Path.GetFileName(f);
                        // zer0ne code marks a download in progress with a note next to the file
                        if (File.Exists(Path.Combine(dir, ".pending-" + name + ".json"))) continue;
                        if (outList.Any(x => x.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) &&
                                             x.File.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        var info = new FileInfo(f);
                        outList.Add(new InstalledFile(repo, name, info.Length, info.LastWriteTime, true, Shared: true));
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // what other apps downloaded: a file this app (or zer0ne code) already has is listed once
        foreach (var m in ExternalModels.List())
        {
            if (outList.Any(x => (x.Repo.Equals(m.Repo, StringComparison.OrdinalIgnoreCase) && x.File.Equals(m.File, StringComparison.OrdinalIgnoreCase)) ||
                                 (x.File.Equals(m.File, StringComparison.OrdinalIgnoreCase) && x.Bytes == m.Bytes))) continue;
            outList.Add(new InstalledFile(m.Repo, m.File, m.Bytes, m.Written, true, Source: m.Source));
        }
        return [.. outList.OrderByDescending(f => f.Written)];
    }

    public static bool Delete(string repo, string file)
    {
        if (IsExternal(repo, file)) return false;   // another app's file: that app deletes it
        var p = ResolvePath(repo, file);
        var deleted = false;
        foreach (var candidate in new[] { p, p + ".part" })
        {
            try { if (File.Exists(candidate)) { File.Delete(candidate); deleted = true; } } catch { }
        }
        return deleted;
    }

    public static void OpenFolder(string? sub = null)
    {
        var dir = sub is null ? RootDir : Path.Combine(RootDir, sub);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }
    public sealed record StorageBreakdown(
        double TotalModelGb,
        double IncompletePartGb,
        int CompletedFileCount,
        int IncompleteFileCount,
        double FreeDiskGb);

    public static StorageBreakdown GetStorageBreakdown()
    {
        long completeBytes = 0;
        long partBytes = 0;
        int completeCount = 0;
        int partCount = 0;

        if (Directory.Exists(RootDir))
        {
            try
            {
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(RootDir, "*", opt))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        {
                            partBytes += fi.Length;
                            partCount++;
                        }
                        else if (f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        {
                            completeBytes += fi.Length;
                            completeCount++;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return new StorageBreakdown(
            completeBytes / 1e9,
            partBytes / 1e9,
            completeCount,
            partCount,
            FreeDiskSpaceGb()
        );
    }

    public static (int DeletedCount, double ReclaimedGb) PurgeIncompleteDownloads()
    {
        int count = 0;
        long reclaimed = 0;

        if (Directory.Exists(RootDir))
        {
            try
            {
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(RootDir, "*", opt))
                {
                    if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            reclaimed += fi.Length;
                            File.Delete(f);
                            count++;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        return (count, reclaimed / 1e9);
    }
}
