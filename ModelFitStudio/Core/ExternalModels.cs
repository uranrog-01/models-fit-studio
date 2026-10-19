using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace ModelFitStudio.Core;

/// <summary>A model file another app already downloaded: used where it is, never copied or deleted by this app.</summary>
public sealed record ExternalModel(string Repo, string File, string Path, long Bytes, DateTime Written, string Source);

/// <summary>
/// GGUF models that LM Studio, Ollama, Jan, the Hugging Face cache (which llama.cpp also uses) or
/// GPT4All already have on this PC. They show in the Library and can be measured, tested and served
/// to agents without a second download. Each folder is read in the background with a time limit:
/// a slow or offline folder never holds up the app.
/// </summary>
public static class ExternalModels
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly object Gate = new();
    private static List<ExternalModel> _last = [];
    private static DateTime _at = DateTime.MinValue;
    private static Task? _scan;
    private static readonly ConcurrentDictionary<string, string> Index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MODELFIT_NO_EXTERNAL=1 turns the scan off (tests, or a user who wants only this app's files).</summary>
    private static bool Off => Environment.GetEnvironmentVariable("MODELFIT_NO_EXTERNAL") == "1";

    /// <summary>The last scan; a new one starts in the background when it is older than a minute. The first call waits for it, up to 6 seconds.</summary>
    public static List<ExternalModel> List()
    {
        if (Off) return [];
        Task? wait = null;
        lock (Gate)
        {
            if ((DateTime.UtcNow - _at).TotalSeconds > 60 && _scan is null)
                _scan = Task.Run(Scan);
            if (_at == DateTime.MinValue) wait = _scan;
        }
        try { wait?.Wait(TimeSpan.FromSeconds(6)); } catch { }
        lock (Gate) return [.. _last];
    }

    /// <summary>The file behind a repo and name this scan found; null when it is not another app's.</summary>
    public static string? PathOf(string repo, string file) =>
        Index.TryGetValue(repo + "/" + file, out var p) && System.IO.File.Exists(p) ? p : null;

    private static void Scan()
    {
        var found = new List<ExternalModel>();
        // each source alone, with its own time limit
        var sources = new (string name, Func<IEnumerable<ExternalModel>> read)[]
        {
            ("LM Studio", LmStudio),
            ("Ollama", Ollama),
            ("Hugging Face", HfCache),
            ("Jan", Jan),
            ("GPT4All", Gpt4All),
        };
        foreach (var (_, read) in sources)
        {
            var task = Task.Run(() => read().ToList());
            try { if (task.Wait(TimeSpan.FromSeconds(4))) found.AddRange(task.Result); }
            catch { }
        }
        // one file reached twice (a link, or two apps sharing a folder) is listed once
        var unique = found.GroupBy(m => (m.File.ToLowerInvariant(), m.Bytes)).Select(g => g.First()).ToList();
        Index.Clear();
        foreach (var m in unique) Index[m.Repo + "/" + m.File] = m.Path;
        lock (Gate)
        {
            _last = unique;
            _at = DateTime.UtcNow;
            _scan = null;
        }
    }

    private static readonly EnumerationOptions Deep = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 5,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    private static ExternalModel? Make(string repo, string file, string path, string source)
    {
        try
        {
            var fi = new FileInfo(path);
            // a link (Hugging Face's cache, Ollama's studio links) counts as the file it points to
            if (fi.LinkTarget is not null && fi.ResolveLinkTarget(true) is FileInfo target) fi = target;
            if (!fi.Exists || fi.Length < 50_000_000) return null;   // not a model: too small
            return new ExternalModel(repo, file, fi.FullName, fi.Length, fi.LastWriteTime, source);
        }
        catch { return null; }
    }

    // LM Studio: models\publisher\repo\file.gguf
    private static IEnumerable<ExternalModel> LmStudio()
    {
        foreach (var root in new[] { Path.Combine(Home, ".lmstudio", "models"), Path.Combine(Home, ".cache", "lm-studio", "models") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.gguf", Deep))
            {
                var rel = Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar);
                var repo = rel.Length >= 3 ? rel[0] + "/" + rel[1] : "lmstudio/" + Path.GetFileNameWithoutExtension(f);
                if (Make(repo, Path.GetFileName(f), f, "LM Studio") is { } m) yield return m;
            }
        }
    }

    // Ollama: manifests\registry\namespace\model\tag (JSON) names the blob that holds the weights
    private static IEnumerable<ExternalModel> Ollama()
    {
        var root = Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } env ? env : Path.Combine(Home, ".ollama", "models");
        var manifests = Path.Combine(root, "manifests");
        if (!Directory.Exists(manifests)) yield break;
        foreach (var man in Directory.EnumerateFiles(manifests, "*", Deep))
        {
            string? digest = null;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(man));
                foreach (var layer in doc.RootElement.GetProperty("layers").EnumerateArray())
                    if (layer.GetProperty("mediaType").GetString() == "application/vnd.ollama.image.model")
                        digest = layer.GetProperty("digest").GetString();
            }
            catch { continue; }
            if (digest is null) continue;
            var blob = Path.Combine(root, "blobs", digest.Replace(':', '-'));
            var parts = Path.GetRelativePath(manifests, man).Split(Path.DirectorySeparatorChar);
            if (parts.Length < 4) continue;
            var (ns, model, tag) = (parts[^3], parts[^2], parts[^1]);
            var repo = "ollama/" + (ns == "library" ? model : ns + "-" + model);
            if (Make(repo, $"{model}-{tag}.gguf", blob, "Ollama") is { } m) yield return m;
        }
    }

    // Hugging Face's cache (llama.cpp's -hf downloads go there too): models--org--name\snapshots\rev\file.gguf
    private static IEnumerable<ExternalModel> HfCache()
    {
        var hub = Environment.GetEnvironmentVariable("HF_HUB_CACHE") is { Length: > 0 } h ? h
            : Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } home ? Path.Combine(home, "hub")
            : Path.Combine(Home, ".cache", "huggingface", "hub");
        if (!Directory.Exists(hub)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(hub, "models--*"))
        {
            var bits = Path.GetFileName(dir).Split("--");
            if (bits.Length < 3) continue;
            var repo = bits[1] + "/" + string.Join("--", bits[2..]);
            var snaps = Path.Combine(dir, "snapshots");
            if (!Directory.Exists(snaps)) continue;
            foreach (var f in Directory.EnumerateFiles(snaps, "*.gguf", Deep))
            {
                var rel = Path.GetRelativePath(snaps, f).Split(Path.DirectorySeparatorChar);
                var name = string.Join("/", rel[1..]);   // without the revision folder
                if (Make(repo, name.Replace('/', '_'), f, "Hugging Face cache") is { } m) yield return m;
            }
        }
    }

    private static IEnumerable<ExternalModel> Jan()
    {
        foreach (var root in new[] { Path.Combine(Roaming, "Jan", "data", "models"), Path.Combine(Home, "jan", "models") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.gguf", Deep))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(f)) ?? "models";
                if (Make("jan/" + folder, Path.GetFileName(f), f, "Jan") is { } m) yield return m;
            }
        }
    }

    private static IEnumerable<ExternalModel> Gpt4All()
    {
        var root = Path.Combine(Local, "nomic.ai", "GPT4All");
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.gguf", Deep))
            if (Make("gpt4all/models", Path.GetFileName(f), f, "GPT4All") is { } m) yield return m;
    }
}
