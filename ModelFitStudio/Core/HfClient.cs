using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ModelFitStudio.Core;

public sealed class HfFile(string name, long bytes)
{
    public string Name { get; } = name;
    public long Bytes { get; } = bytes;
    public string SizeGbText => $"{Bytes / 1e9:0.0} GB";
    public double SizeGb => Bytes / 1e9;
    public string? Quant { get; init; }
    public int? Shards { get; init; }
    public bool IsGguf => Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
    public bool IsMmproj => Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
}

public sealed class HfRepo
{
    public string RepoId { get; set; } = "";
    public string? BaseModel { get; set; }
    public string? PipelineTag { get; set; }
    public string? License { get; set; }
    public long Downloads { get; set; }
    public int Likes { get; set; }
    public string? Architecture { get; set; }
    public List<HfFile> Files { get; } = [];
    public List<string> Tags { get; } = [];
    /// <summary>The weights. Helper files (a vision projector, a draft head) are not models: see <see cref="RepoFiles"/>.</summary>
    public IEnumerable<HfFile> GgufFiles => Files.Where(f => f.IsGguf && !RepoFiles.IsHelper(f.Name, f.Bytes / 1e9));
    // Multi-part GGUFs are one download: siblings arrive as
    // "<quant>/<name>-00001-of-00006.gguf", so collapse each shard set to a
    // single file whose size is the whole model.
    public IEnumerable<HfFile> ModelFiles =>
        GgufFiles
            .GroupBy(ShardGroupKey)
            .OrderBy(g => QuantParser.Tier(g.First().Quant))
            .Select(g => new HfFile(g.First().Name, g.Sum(f => f.Bytes))
            {
                Quant = g.First().Quant,
                Shards = g.Count() > 1 ? g.Count() : null,
            });
    public IEnumerable<HfFile> MmprojFiles => Files.Where(f => f.IsMmproj);
    /// <summary>Draft heads, the smallest first: the one a model is run with.</summary>
    public IEnumerable<HfFile> DraftFiles => Files.Where(f => f.IsGguf && RepoFiles.IsDraftHeadName(f.Name) && RepoFiles.IsHelper(f.Name, f.Bytes / 1e9)).OrderBy(f => f.Bytes).ThenBy(f => f.Name.Length);

    private static string ShardGroupKey(HfFile f)
    {
        // only shard sets share a key; a plain single-file quant is its own group
        var m = Regex.Match(f.Name, @"-(\d{5})-of-(\d{5})(\.gguf)$", RegexOptions.IgnoreCase);
        return m.Success ? f.Name[..m.Index] : f.Name;
    }

    // Architecture fields resolved from config.json (own repo, or a base-model repo)
    public Dictionary<string, double> Arch { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int? NumLayers => GetInt("num_hidden_layers");
    public int? HiddenSize => GetInt("hidden_size");
    public int? NumExperts => GetInt("n_routed_experts") ?? GetInt("num_experts") ?? GetInt("num_local_experts");
    public int? ActiveExperts => GetInt("num_experts_per_tok");
    public int? ContextLength => GetInt("max_position_embeddings");
    public double? ParamsFromConfig { get; set; }
    /// <summary>The parameter count and context the GGUF header itself states, when Hugging Face has read it.</summary>
    public double? GgufParams { get; set; }
    public int? GgufContext { get; set; }
    public bool MoE => (NumExperts ?? 0) > 0;

    private int? GetInt(string k) => Arch.TryGetValue(k, out var v) ? (int)v : (int?)null;

    public string Role
    {
        get
        {
            var hay = string.Join(' ', Tags.Append(PipelineTag ?? "").Append(RepoId)).ToLowerInvariant();
            if (hay.Contains("embedding") || hay.Contains("feature-extraction") || hay.Contains("sentence-similarity")) return "Embedding";
            if (hay.Contains("mmproj") || hay.Contains("-vl") || hay.Contains("vision") || hay.Contains("image-text")) return "Vision";
            if (hay.Contains("text-to-speech") || hay.Contains("audio") || hay.Contains("tts") ||
                hay.Contains("speech") || hay.Contains("-asr") || hay.Contains("whisper")) return "Audio / TTS";
            if (hay.Contains("coder") || hay.Contains("code")) return "Coding";
            if (hay.Contains("reasoning") || hay.Contains("thinking") || hay.Contains("r1-") || hay.Contains("-distill")) return "Reasoning";
            return "Chat / general";
        }
    }
}

public static class HfClient
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://huggingface.co/"),
        Timeout = TimeSpan.FromSeconds(30),
    };
    static HfClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ModelFitStudio/1.0");
        // optional: an HF access token raises the anonymous per-IP limit
        var token = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) Http.DefaultRequestHeaders.Authorization = new("Bearer", token.Trim());
    }

    // ---------- network discipline ----------
    // Hugging Face hands out one anonymous request allowance per IP. A single
    // scan costs several API calls, and anything else on this machine talking
    // to HF (a browser tab, another tool) shares the same allowance — once it
    // is spent every request answers 429 for a while. So: retry politely, and
    // cache what we already fetched so re-scans cost the network nothing.

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModelFitStudio", "cache");

    private static string CacheFile(string key)
    {
        var safe = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDir, safe + ".json");
    }

    private static string? ReadCache(string key, TimeSpan maxAge)
    {
        try
        {
            var p = CacheFile(key);
            if (!File.Exists(p)) return null;
            if (DateTime.UtcNow - new FileInfo(p).LastWriteTimeUtc > maxAge) return null;
            return File.ReadAllText(p);
        }
        catch { return null; }
    }

    private static string? ReadCacheAnyAge(string key)
    {
        try { var p = CacheFile(key); return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch { return null; }
    }

    private static void WriteCache(string key, string json)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CacheFile(key), json);
        }
        catch { }
    }

    /// <summary>GETs a document, backing off when HF answers 429/503.</summary>
    private static async Task<(string? Body, int Status)> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var token = AppSettings.Get().HfToken ?? Environment.GetEnvironmentVariable("HF_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            using var resp = await Http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadAsStringAsync(ct), status);
            if (status is 401 or 403)
                return (null, status);
            if (status is 429 or 503 && attempt < 3)
            {
                var wait = resp.Headers.RetryAfter?.Delta
                           ?? TimeSpan.FromSeconds(2 + attempt * 4);   // 2s, 6s, 10s
                await Task.Delay(wait, ct);
                continue;
            }
            return (null, status);
        }
    }

    /// <summary>Cached GET for callers outside HfClient — the catalog search uses it.
    /// <paramref name="bypassCache"/> forces a live fetch and rewrites the cache.</summary>
    public static async Task<(string? Body, int Status)> FetchCachedJsonAsync(string key, string url, TimeSpan ttl,
        CancellationToken ct = default, bool bypassCache = false)
    {
        if (!bypassCache)
        {
            var cached = ReadCache(key, ttl);
            if (cached is not null) return (cached, 200);
        }
        var (fetched, status) = await GetJsonAsync(url, ct);
        if (fetched is null) return (ReadCacheAnyAge(key), status);
        WriteCache(key, fetched);
        return (fetched, status);
    }

    /// <summary>Says what a failed HF status actually means, instead of blaming the repo.</summary>
    private static string FetchError(string repoId, int status) => status switch
    {
        // HF answers 401 for both private repos and repos that do not exist —
        // gated models still serve their metadata publicly
        401 => $"GATED_MODEL: Hugging Face reports '{repoId}' requires authorization (HTTP 401). If this is a gated model (like Llama 3 or Gemma), enter your Hugging Face User Access Token.",
        403 => $"GATED_MODEL: '{repoId}' is blocked for anonymous access (HTTP 403). Enter your authorized Hugging Face User Access Token.",
        404 => $"Repository '{repoId}' not found on Hugging Face.",
        429 => "Hugging Face is rate-limiting this machine right now (HTTP 429). Anonymous API calls share one " +
               "allowance per IP, and this scan plus anything else talking to HF uses it up. " +
               "Wait a minute and scan again — models you scanned recently are cached and come straight from disk.",
        _ => $"Hugging Face answered HTTP {status} for '{repoId}'.",
    };

    /// <summary>Accepts a full URL, org/repo, or a bare name. Returns null when the input is a plain model name.</summary>
    public static (string? RepoId, string? FileHint) ParseRepo(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return (null, null);

        var url = s.Replace('\\', '/');
        var m = Regex.Match(url, @"^(?:https?://)?(?:www\.)?huggingface\.co/([^/?#]+)/([^/?#]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var repo = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
            var fm = Regex.Match(url, @"(?:/blob/|/resolve/)[^/]+/(.+)$", RegexOptions.IgnoreCase);
            return (repo, fm.Success ? fm.Groups[1].Value.Trim() : null);
        }

        // org/repo
        var parts = url.Split('/');
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]) &&
            !url.Contains(' ') && Regex.IsMatch(url, @"^[\w.\-]+/[\w.\-]+$"))
        {
            return (url, null);
        }
        return (null, null);
    }

    /// <summary>The model part of a repo id, with quantiser and mirror prefixes stripped: "bartowski/microsoft_Phi-4-GGUF" → "Phi-4".</summary>
    public static string BaseModelName(string repoId)
    {
        var last = (repoId ?? "").Split('/')[^1];
        var stripped = Regex.Replace(last, @"[._-](?:GGUF|AWQ|GPTQ|AutoRound|MLX|EXL2|bnb-4bit|quantized)$", "", RegexOptions.IgnoreCase);
        // mirrors prefix the original org: "bartowski/microsoft_Phi-4-GGUF"
        stripped = Regex.Replace(stripped, @"^[A-Za-z0-9]+_", "");
        return stripped.Length > 0 ? stripped : last;
    }

    /// <summary>Finds GGUF mirrors of a model on Hugging Face, most downloaded first.</summary>
    public static async Task<List<string>> SearchGgufMirrorsAsync(string modelName, CancellationToken ct = default)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(modelName)) return found;
        var want = Normalize(modelName);
        foreach (var probe in new[] { modelName + " GGUF", modelName })
        {
            try
            {
                var url = $"api/models?search={Uri.EscapeDataString(probe)}&limit=40&sort=downloads&direction=-1";
                var (body, _) = await GetJsonAsync(url, ct);
                if (body is null) continue;
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    var id = m.Str("id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!id.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase)) continue;
                    // full-text search is fuzzy: the mirror must actually be of
                    // this model, judged on the name with separators removed
                    if (!Normalize(BaseModelName(id)).Contains(want)) continue;
                    if (!found.Contains(id)) found.Add(id);
                }
            }
            catch { }
            if (found.Count >= 8) break;
        }

        // known mirror orgs first, and at most two from each so the list shows
        // a mix of mirrors rather than four quantisations of one repo
        var ranked = new List<string>();
        foreach (var rank in Enumerable.Range(0, 5))
        {
            var perOrg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in found.Where(id => MirrorRank(id) == rank))
            {
                var org = id.Split('/')[0];
                if (perOrg.GetValueOrDefault(org) >= 2) continue;
                perOrg[org] = perOrg.GetValueOrDefault(org) + 1;
                ranked.Add(id);
            }
        }
        return [.. ranked.Take(4)];
    }

    private static int MirrorRank(string repoId)
    {
        var org = repoId.Split('/')[0].ToLowerInvariant();
        return org switch
        {
            "unsloth" => 0,
            "bartowski" => 1,
            "mradermacher" => 2,
            "lmstudio-community" => 3,
            _ => 4,
        };
    }

    private static string Normalize(string s) =>
        Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToLowerInvariant();

    public static async Task<(HfRepo? Repo, string? Error)> GetRepoAsync(string repoId, CancellationToken ct = default)
    {
        var cacheKey = $"repo {repoId}";
        try
        {
            var url = $"api/models/{repoId}?blobs=true";
            var body = ReadCache(cacheKey, TimeSpan.FromHours(12));
            if (body is null)
            {
                var (fetched, status) = await GetJsonAsync(url, ct);
                if (fetched is null)
                {
                    // a stale copy beats an error screen while HF is throttling
                    body = ReadCacheAnyAge(cacheKey);
                    if (body is null) return (null, FetchError(repoId, status));
                }
                else
                {
                    WriteCache(cacheKey, fetched);
                    body = fetched;
                }
            }

            using var doc = JsonDocument.Parse(body!);
            var root = doc.RootElement;
            var repo = new HfRepo { RepoId = repoId };
            repo.PipelineTag = root.Str("pipeline_tag");
            repo.Downloads = (long)(root.Num("downloads") ?? 0);
            repo.Likes = (int)(root.Num("likes") ?? 0);
            foreach (var t in root.Arr("tags")) repo.Tags.Add(t);
            if (root.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
            {
                repo.GgufParams = gguf.Num("total");
                repo.GgufContext = gguf.Int("context_length");
            }

            if (root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object)
            {
                repo.License = card.Str("license");
                if (card.TryGetProperty("base_model", out var bm) && bm.ValueKind == JsonValueKind.Array && bm.GetArrayLength() > 0)
                    repo.BaseModel = bm[0].ValueKind == JsonValueKind.String ? bm[0].GetString() : null;
                else if (card.TryGetProperty("base_model", out var bs) && bs.ValueKind == JsonValueKind.String)
                    repo.BaseModel = bs.GetString();
                repo.Architecture ??= card.Str("base_model_relation");
                var mt = card.TryGetProperty("model_index", out var mi) ? mi.GetString() : null;
                if (!string.IsNullOrWhiteSpace(mt)) repo.Architecture ??= mt;
            }
            foreach (var t in repo.Tags)
            {
                if (t.StartsWith("license:") && repo.License is null) repo.License = t["license:".Length..];
                if (t.StartsWith("base_model:") && repo.BaseModel is null) repo.BaseModel = t["base_model:".Length..];
            }

            if (root.TryGetProperty("siblings", out var sib) && sib.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in sib.EnumerateArray())
                {
                    var name = f.Str("rfilename");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var size = f.Num("size") ?? 0;
                    repo.Files.Add(new HfFile(name, (long)size) { Quant = QuantParser.FromFileName(name) });
                }
            }
            return (repo, null);
        }
        catch (Exception ex)
        {
            return (null, $"Hugging Face request failed: {ex.Message}");
        }
    }

    /// <summary>Resolves architecture fields, walking repo → cardData base_model → same-org base → search.</summary>
    public static async Task<Dictionary<string, double>> GetArchAsync(HfRepo repo, CancellationToken ct = default)
    {
        var arch = await FetchConfigAsync(repo.RepoId, ct);
        if (arch.Count > 0) return arch;

        var chain = new List<string>();
        if (!string.IsNullOrWhiteSpace(repo.BaseModel)) chain.Add(repo.BaseModel);
        var stripped = Regex.Replace(repo.RepoId, @"-(?:GGUF|AWQ|GPTQ|AutoRound|MLX|EXL2|bf16|fp8|fp16|quantized)$", "", RegexOptions.IgnoreCase);
        if (stripped != repo.RepoId) chain.Add(stripped);
        if (repo.BaseModel is null)
        {
            var org = repo.RepoId.Split('/')[0];
            var last = repo.RepoId.Split('/')[1];
            chain.Add(Regex.Replace(last, @"-(?:GGUF|AWQ|GPTQ|AutoRound|MLX|EXL2).*$", "", RegexOptions.IgnoreCase) is { Length: > 0 } n ? $"{org}/{n}" : repo.RepoId);
        }

        foreach (var cand in chain.Distinct())
        {
            var cfg = await FetchConfigAsync(cand, ct);
            if (cfg.Count > 0) return cfg;
        }
        return [];
    }

    private static async Task<Dictionary<string, double>> FetchConfigAsync(string repoId, CancellationToken ct)
    {
        var cacheKey = $"config {repoId}";
        try
        {
            var body = ReadCache(cacheKey, TimeSpan.FromHours(12));
            if (body is null)
            {
                foreach (var branch in new[] { "main", "master" })
                {
                    var (fetched, _) = await GetJsonAsync($"{repoId}/raw/{branch}/config.json", ct);
                    if (fetched is null) continue;
                    body = fetched;
                    WriteCache(cacheKey, fetched);
                    break;
                }
                // a stale config is still better than scoring without one
                body ??= ReadCacheAnyAge(cacheKey);
                if (body is null) return [];
            }

            using var doc = JsonDocument.Parse(body);
            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Flatten(doc.RootElement, dict);
            return dict;
        }
        catch { return []; }
    }

    private static void Flatten(JsonElement el, Dictionary<string, double> dict, int depth = 0)
    {
        if (depth > 2 || el.ValueKind != JsonValueKind.Object) return;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
                dict[p.Name] = d;
            else if (p.Value.ValueKind == JsonValueKind.Object && p.Name is "text_config" or "vision_config" or "llm_config")
                Flatten(p.Value, dict, depth + 1);
        }
    }

    /// <summary>Rough parameter estimate used when config.json is unavailable.</summary>
    public static double? EstimateParams(HfRepo repo)
    {
        // Rough heuristics: layers × hidden × (4h + attention) + experts
        var l = repo.Arch.GetValueOrDefault("num_hidden_layers");
        var h = repo.Arch.GetValueOrDefault("hidden_size");
        if (l <= 0 || h <= 0) return null;
        var attn = repo.Arch.GetValueOrDefault("num_attention_heads") > 0 ? repo.Arch["num_attention_heads"] * 128 : h;
        var kv = repo.Arch.GetValueOrDefault("num_key_value_heads") > 0 ? repo.Arch["num_key_value_heads"] * 128 : attn;
        var perLayer = 4 * h * h + 2 * h * attn + 2 * h * kv;
        var interm = repo.Arch.GetValueOrDefault("intermediate_size");
        if (interm > 0) perLayer += 3 * h * interm;
        var moe = repo.Arch.GetValueOrDefault("moe_intermediate_size");
        var experts = repo.Arch.GetValueOrDefault("n_routed_experts");
        if (moe > 0) perLayer += experts > 0 ? experts * 3 * h * moe : 3 * h * moe;
        var vocab = repo.Arch.GetValueOrDefault("vocab_size") > 0 ? repo.Arch["vocab_size"] : 150000;
        return (perLayer * l + vocab * h) / 2;
    }

    /// <summary>Tries the Ollama registry for a matching library name (registry.ollama.ai).</summary>
    public static async Task<string?> OllamaNameAsync(HfRepo repo, CancellationToken ct)
    {
        var names = new List<string>();
        var last = repo.RepoId.Split('/')[^1].ToLowerInvariant();
        names.Add(Regex.Replace(last, @"-(gguf|instruct|it|chat|awq|gptq)$", ""));
        if (!string.IsNullOrWhiteSpace(repo.BaseModel))
            names.Add(repo.BaseModel.Split('/')[^1].ToLowerInvariant());
        foreach (var n in names.Distinct())
        {
            try
            {
                using var resp = await Http.GetAsync($"https://registry.ollama.ai/library/{n}/tags", ct);
                if (resp.IsSuccessStatusCode) return n;
            }
            catch { return null; }
        }
        return null;
    }
}

public static class QuantParser
{
    /// <summary>The usual versions, the best trade first: one list, kept with the fit rule.</summary>
    public static readonly string[] PreferredOrder = FitRule.UsualOrder;

    public static string? FromFileName(string name)
    {
        var m = Regex.Match(name, @"(UD-)?(IQ\d+_[A-Z]+|TQ\d+_\d+|Q\d+_K_[A-Z]+|Q\d+_K|Q\d+_\d+|MXFP4_MOE|BF16|FP16|FP32|F16|F32)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        // "fp16" is the same kind of file as "f16": some authors write the p
        var core = m.Groups[2].Value.ToUpperInvariant().Replace("FP", "F");
        return m.Groups[1].Success ? "UD-" + core : core;
    }

    public static int Tier(string? quant)
    {
        if (string.IsNullOrWhiteSpace(quant)) return 99;
        var i = Array.FindIndex(PreferredOrder, q => q.Equals(quant, StringComparison.OrdinalIgnoreCase));
        // "UD-Q4_K_M" is a Q4_K_M with the publisher's own mix: the same place in the order
        if (i < 0 && quant.StartsWith("UD-", StringComparison.OrdinalIgnoreCase))
            i = Array.FindIndex(PreferredOrder, q => q.Equals(quant[3..], StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 98 - (quant.Length % 7) : i;
    }
}
