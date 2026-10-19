using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ModelFitStudio.Core;

public sealed class QuantOption
{
    public required string File { get; init; }
    public double SizeGb { get; init; }
    public long SizeBytes { get; init; }
    public string? Quant { get; init; }
    public string FitLevel { get; set; } = "Unknown";   // Perfect | Good | Marginal | Slower | Too tight
    public double UtilPct { get; set; }
    /// <summary>The middle of the range below: one number for the places that need one.</summary>
    public double EstTps { get; set; }
    /// <summary>The speed shown to the user: a range, corrected for the kind of GPU.</summary>
    public double TpsLow { get; set; }
    public double TpsHigh { get; set; }
    /// <summary>True when the speed is a real run of this file on this machine, not an estimate.</summary>
    public bool Measured { get; set; }
    /// <summary>Time to the first token of a short prompt, in milliseconds. Measured only; 0 when unknown.</summary>
    public double FirstTokenMs { get; set; }
    /// <summary>The quality test of this file on this machine, 0–100; 0 when it was not tested.</summary>
    public double QualityTested { get; set; }
    public double MemoryRequiredGb { get; set; }
    public double Score { get; set; }
    public string RunMode { get; set; } = "";
    public double UsableContext { get; set; }
    public string Verdict { get; set; } = "unknown";     // runs | tight | slow (past fast memory, partly from system memory) | no
    public List<string> Reasons { get; } = [];
    public bool Recommended { get; set; }
}

public sealed class AnalysisResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string Input { get; set; } = "";
    public string Kind { get; set; } = "hf";             // hf | catalog
    public string? RepoId { get; set; }
    public string? HfUrl { get; set; }
    public string? BaseModel { get; set; }
    public string? ModelName { get; set; }
    public string? Provider { get; set; }
    public string Role { get; set; } = "Chat / general";
    public string? Architecture { get; set; }
    public string? ParamsText { get; set; }
    public double? ParamsTotal { get; set; }
    public double? ParamsActive { get; set; }
    public bool IsMoe { get; set; }
    public int? ContextMax { get; set; }
    public string? License { get; set; }
    public long Downloads { get; set; }
    public int Likes { get; set; }
    public List<QuantOption> Quants { get; } = [];
    public QuantOption? Primary { get; set; }
    public string? OllamaName { get; set; }
    public List<string> Notes { get; } = [];
    public List<string> Suggestions { get; } = [];
    public string? ServeCommand { get; set; }
    public string? DownloadCommand { get; set; }
    public string? LlmfitEstimateBasis { get; set; }
    public bool EstimateCalibrated { get; set; }
    /// <summary>True when the speed ranges are corrected by a real run on this machine.</summary>
    public bool SpeedCalibrated { get; set; }
    /// <summary>llmfit's quality score, 0–100: model size, family and task fit. An estimate, not a benchmark. 0 when unknown.</summary>
    public double Quality { get; set; }
    /// <summary>Why this version is the recommended one, in a sentence.</summary>
    public string? PickReason { get; set; }
    /// <summary>How the recommended quant runs on this machine, before it is downloaded.</summary>
    public TurboFit.TurboInfo? Turbo { get; set; }
    public List<string> MmprojFiles { get; } = [];
    /// <summary>Draft heads in the repository, the smallest first (empty for most models).</summary>
    public List<string> DraftFiles { get; } = [];
}

public static class Analyzer
{

    public static async Task<AnalysisResult> AnalyzeAsync(string input, HardwareInfo hw, CancellationToken ct = default)
    {
        var res = new AnalysisResult { Input = input };
        if (string.IsNullOrWhiteSpace(input))
        {
            res.Error = "Paste a Hugging Face link or model name first.";
            return res;
        }

        var (repoId, fileHint) = HfClient.ParseRepo(input);
        if (repoId is null)
            return await AnalyzeByNameAsync(input, hw, res, ct);

        // ---- Hugging Face repository flow -----------------------------------
        res.Kind = "hf";
        var (repo, err) = await HfClient.GetRepoAsync(repoId, ct);
        if (repo is null)
        {
            res.Error = err;
            return res;
        }

        res.RepoId = repo.RepoId;
        res.HfUrl = $"https://huggingface.co/{repo.RepoId}";
        res.BaseModel = repo.BaseModel;
        res.Role = repo.Role;
        res.License = repo.License;
        res.Downloads = repo.Downloads;
        res.Likes = repo.Likes;
        res.OllamaName = await HfClient.OllamaNameAsync(repo, ct);
        res.DownloadCommand = $"huggingface-cli download {repo.RepoId} --include \"*.gguf\"";

        var arch = await HfClient.GetArchAsync(repo, ct);
        repo.Arch.Clear();
        foreach (var kv in arch) repo.Arch[kv.Key] = kv.Value;
        res.Architecture = repo.Arch.Count > 0 ? GuessArch(repo) : null;
        res.IsMoe = repo.MoE;
        res.ContextMax = repo.ContextLength ?? repo.GgufContext;
        (res.ParamsTotal, res.ParamsActive) = ParamCounts(repo);
        // The count worked out from config.json is a rough formula, and a config can
        // describe only part of a model (a vision wrapper, a nested text config). When
        // it is off, llmfit drops the entry as implausible and nothing is scored. The
        // GGUF header and the size in the name are the model's own word, so they win.
        var trustedB = HfCatalog.Params(repo.RepoId, repo.GgufParams / 1e9);
        if (trustedB is > 0) res.ParamsTotal = trustedB * 1e9;

        // A repo also carries files that only help the model (a vision projector, a draft
        // head). Offered as "the file", a 0.5 GB helper made a 26B model look tiny.
        var ggufs = RepoFiles.Models(repo.ModelFiles, f => f.Name, f => f.SizeGb, trustedB ?? 0)
            .Where(f => f.SizeGb > 0.35)
            .OrderBy(f => QuantParser.Tier(f.Quant))
            .ToList();
        foreach (var f in repo.MmprojFiles) res.MmprojFiles.Add(f.Name);
        if (res.MmprojFiles.Count > 0)
            res.Notes.Add($"Vision files detected ({string.Join(", ", res.MmprojFiles)}) — add --mmproj <file> to the llama-server command.");
        foreach (var f in repo.DraftFiles) res.DraftFiles.Add(f.Name);
        if (res.DraftFiles.Count > 0)
            res.Notes.Add($"Draft head detected ({res.DraftFiles[0]}) — add --spec-type draft-mtp --spec-draft-model <file> and the model writes faster.");

        if (ggufs.Count == 0)
        {
            var others = repo.Files.Where(f => f.Name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).Count();
            var isMlx = repo.RepoId.Contains("mlx", StringComparison.OrdinalIgnoreCase) || repo.Tags.Any(t => t.Contains("mlx", StringComparison.OrdinalIgnoreCase));
            // A dead end is useless. Go and look for the GGUF mirror of the same
            // model so the user has somewhere to click instead.
            var baseName = HfClient.BaseModelName(repo.RepoId);
            var mirrors = await HfClient.SearchGgufMirrorsAsync(baseName, ct);
            res.Suggestions.Clear();
            res.Suggestions.AddRange(mirrors);
            if (isMlx)
            {
                res.Error = $"'{repo.RepoId}' is an Apple Silicon MLX model (macOS Metal only). Windows and PCs run GGUF — select a matching GGUF mirror below with the same weights to run it here.";
            }
            else
            {
                res.Error = others > 0
                    ? $"'{repo.RepoId}' ships safetensors, not GGUF ({others} files). Pick a GGUF mirror below — they are the same weights in llama.cpp format."
                    : $"'{repo.RepoId}' contains no model weight files.";
            }
            if (mirrors.Count == 0)
                res.Error += " No GGUF mirror of it turned up on Hugging Face either.";
            return res;
        }

        // Every file is rated by the shared rule (FitRule): its size, the projector a vision
        // model loads with it, the cache for the conversation and the engine's buffers,
        // against this PC's fast memory and its ceiling.
        var limits = FitRule.LimitsFor(hw.BudgetGb, hw.TotalRamGb);
        var budget = limits.FastGb;
        var projectorGb = repo.MmprojFiles.Where(f => f.IsGguf && f.Bytes > 0).Select(f => f.SizeGb).DefaultIfEmpty(0).Min();
        var kvPerToken = KvBytesPerToken(repo, res.ParamsTotal, res.ParamsActive);
        var maxContext = res.ContextMax is > 0 ? res.ContextMax.Value : 8192;
        FitRule.Fit FitOf(HfFile f) => FitRule.Rate(f.SizeGb, projectorGb, kvPerToken, maxContext, limits);
        double Need(HfFile f) => FitOf(f).NeedGb;

        // A ladder of up to nine versions. The ones the verdict hangs on come first: the
        // largest that runs with room to spare, the largest that runs inside fast memory,
        // one that runs past it, the first that does not run, and the smallest. Then the
        // usual versions, those that fit before those that do not.
        var bySize = ggufs.OrderBy(f => f.SizeGb).ToList();
        var fits = bySize.Where(f => FitOf(f).Verdict != FitRule.No).ToList();
        var picked = new List<HfFile>();
        void Add(HfFile? f)
        {
            if (f is not null && picked.All(p => p.Name != f.Name)) picked.Add(f);
        }

        // a publisher's own mix ("UD-Q4_K_M") stands in for the plain version it replaces
        var usual = QuantParser.PreferredOrder
            .Select(tier => fits.FirstOrDefault(f => string.Equals(f.Quant, tier, StringComparison.OrdinalIgnoreCase))
                            ?? fits.FirstOrDefault(f => string.Equals(f.Quant, "UD-" + tier, StringComparison.OrdinalIgnoreCase)))
            .OfType<HfFile>()
            .ToList();
        Add(bySize.LastOrDefault(f => FitOf(f).Runs && !FitRule.IsUnquantised(f.Quant)));
        Add(bySize.LastOrDefault(f => FitOf(f).Runs));
        Add(bySize.LastOrDefault(f => FitOf(f).Verdict == FitRule.Tight));
        // past fast memory the usual version is the one people ask about; else the smallest, which loses the least speed
        Add(usual.FirstOrDefault(f => FitOf(f).Verdict == FitRule.Slow) ?? bySize.FirstOrDefault(f => FitOf(f).Verdict == FitRule.Slow));
        Add(bySize.FirstOrDefault(f => FitOf(f).Verdict == FitRule.No));
        foreach (var f in fits.Take(2)) Add(f);
        foreach (var f in usual.Where(f => FitOf(f).Verdict is not (FitRule.Slow or FitRule.No))) Add(f);
        foreach (var f in usual.Where(f => FitOf(f).Verdict is FitRule.Slow or FitRule.No)) Add(f);

        var candidates = picked
            .Take(9)
            .OrderBy(f => f.SizeGb)
            .ToList();
        if (candidates.Count == 0) candidates = [.. bySize.Take(3)];

        // ---- score every candidate with llmfit's own engine -------------------
        var customPath = Path.Combine(Path.GetTempPath(), $"modelfit-{Guid.NewGuid():N}.json");
        var entries = new List<Dictionary<string, object?>>();
        var label = ScoringName(repo.RepoId);
        var (_, namedActiveB, namedMoe) = TurboFit.ParamsFromName(repo.RepoId.Split('/')[^1]);
        foreach (var f in candidates)
        {
            var required = Need(f);
            var e = new Dictionary<string, object?>
            {
                ["name"] = $"{label} [{f.Quant ?? "file"}]",
                ["provider"] = repo.RepoId.Split('/')[0],
                ["parameter_count"] = ParamText(res.ParamsTotal),
                ["min_ram_gb"] = Math.Round(required * 1.05, 2),
                ["recommended_ram_gb"] = Math.Round(required * 1.2, 2),
                ["min_vram_gb"] = Math.Round(required, 2),
                ["quantization"] = f.Quant ?? "Q4_K_M",
                ["format"] = "gguf",
                ["context_length"] = res.ContextMax is > 0 ? (int)Math.Min(res.ContextMax.Value, 1_000_000) : 8192,
                ["use_case"] = "General chat",
                ["license"] = repo.License ?? "unknown",
            };
            if (repo.MoE)
            {
                e["is_moe"] = true;
                if (repo.NumExperts is > 0) e["num_experts"] = repo.NumExperts;
                if (repo.ActiveExperts is > 0) e["active_experts"] = repo.ActiveExperts;
                // llmfit's MoE speed model needs active params; without them it
                // falls back to the full parameter count and under-reports tok/s.
                if (res.ParamsActive is > 0) e["active_parameters"] = (long)res.ParamsActive.Value;
            }
            else if (namedMoe && namedActiveB > 0)
            {
                // the name was the only place that said so, and the label no longer carries it
                e["is_moe"] = true;
                e["active_parameters"] = (long)(namedActiveB * 1e9);
            }
            var keys = new (string Key, string Field)[]
            {
                ("num_hidden_layers", "num_hidden_layers"),
                ("num_attention_heads", "num_attention_heads"),
                ("num_key_value_heads", "num_key_value_heads"),
                ("head_dim", "head_dim"),
                ("hidden_size", "hidden_size"),
                ("vocab_size", "vocab_size"),
                ("moe_intermediate_size", "moe_intermediate_size"),
            };
            foreach (var (k, fld) in keys)
                if (repo.Arch.TryGetValue(k, out var v) && v > 0) e[fld] = (int)v;
            entries.Add(e);
        }

        File.WriteAllText(customPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            var budgetArg = hw.BudgetGb > 0 ? $"--memory={hw.BudgetGb:0.#}G " : "";
            var fit = await LlmfitRunner.RunJsonAsync($"{budgetArg}fit --json", customPath, timeoutMs: 120_000, ct: ct);
            if (fit is null || fit.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("llmfit fit --json returned no data.");

            var rows = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var catalogGbPerSec = 0.0;
            if (fit.Value.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    var n = m.Str("name");
                    if (!string.IsNullOrEmpty(n) && n.StartsWith(label + " [", StringComparison.OrdinalIgnoreCase))
                        rows[n] = m;
                }
                catalogGbPerSec = MachineSpeed.FromRows(models);
            }

            DebugDiag($"fit rows={rows.Count} of {models.GetArrayLength()} for {repo.RepoId}");
            foreach (var (k, v) in rows)
                DebugDiag($"  row {k}: best_quant={v.Str("best_quant")} disk={v.Num("disk_size_gb")} " +
                          $"mem={v.Num("memory_required_gb")} util={v.Num("utilization_pct")} tps={v.Num("estimated_tps")} " +
                          $"fit={v.Str("fit_level")} run={v.Str("run_mode")} usable={v.Num("usable_context")} score={v.Num("score")} " +
                          $"parts={(v.TryGetProperty("score_components", out var sc) ? sc.GetRawText() : "none")}");
            if (rows.Count == 0)
            {
                DebugDiag($"  custom file: {File.Exists(customPath)} bytes={new FileInfo(customPath).Length} path={customPath}");
                var probe = JsonDocument.Parse(File.ReadAllText(customPath));
                DebugDiag("  custom entries: " + string.Join(", ",
                    probe.RootElement.EnumerateArray().Select(e => e.Str("name"))));
                try { File.Copy(customPath, Path.Combine(Path.GetTempPath(), "modelfit-last-custom.json"), true); }
                catch { }
            }

            // reference tps: pick the Q4_K_M-class row, else the first
            JsonElement? refRow = rows.Values.FirstOrDefault(r => r.Str("best_quant") == "Q4_K_M");
            if (refRow is null || refRow.Value.ValueKind != JsonValueKind.Object)
                refRow = rows.Values.Count > 0 ? rows.Values.First() : null;
            double refTps = refRow.HasValue ? refRow.Value.Num("estimated_tps") ?? 0 : 0;
            double refDisk = refRow.HasValue ? refRow.Value.Num("disk_size_gb") ?? 0 : 0;
            var usedBandwidthModel = false;
            string basis = refRow.HasValue &&
                           refRow.Value.TryGetProperty("estimate_basis", out var eb) &&
                           eb.ValueKind == JsonValueKind.Object
                ? eb.Str("method") ?? "unknown"
                : "unknown";
            res.LlmfitEstimateBasis = basis;
            res.EstimateCalibrated = (refRow.HasValue ? refRow.Value.Str("estimate_confidence") : null) switch
            {
                "measured" or "calibrated" => true,
                _ => false,
            };
            if (refRow.HasValue && refRow.Value.TryGetProperty("score_components", out var parts))
                res.Quality = parts.Num("quality") ?? 0;
            var calibration = Measurements.Calibration(hw.GpuName);
            res.SpeedCalibrated = calibration > 0;
            // what a model past the budget keeps of its speed: measured for a chip that shares
            // system memory (FitRule.SharedSpill); on a card with its own memory the part
            // outside it loses the card's bandwidth advantage, at least a factor of two (an estimate)
            var shared = hw.GpuClass == SpeedEstimate.Integrated;
            var slowerBy = hw.GpuClass == SpeedEstimate.Discrete
                ? Math.Max(2, TurboFit.GpuBandwidthGbs(hw.GpuName) / Math.Max(1, TurboFit.RamBandwidthGbs()))
                : 2;

            foreach (var f in candidates)
            {
                var q = new QuantOption
                {
                    File = f.Name,
                    SizeGb = Math.Round(f.SizeGb, 2),
                    SizeBytes = f.Bytes,
                    Quant = f.Quant ?? "GGUF",
                };
                res.Quants.Add(q);

                // Memory and fit are this file's own. llmfit sizes a model at the quant
                // it would pick itself, so its row gives every file of a repo the same
                // memory — right for one of them, wrong for the rest.
                var own = FitOf(f);
                q.MemoryRequiredGb = Math.Round(own.NeedGb, 2);
                q.UtilPct = Math.Round(own.Util * 100, 1);
                q.FitLevel = own.Level;

                var key = $"{label} [{f.Quant ?? "file"}]";
                if (rows.TryGetValue(key, out var row))
                {
                    q.Score = row.Num("score") ?? 0;
                    // tps: llmfit scores the family once, at its own reference
                    // size (its Q4_K_M-class formula). Decode moves with the
                    // weight bytes read per token, so scale by that ratio. llmfit's
                    // reference is often its Q8, so a Q2 of the same model is a third
                    // of the size: the clamp has to leave room for that, and only
                    // bounds the extrapolation beyond it.
                    q.EstTps = refTps > 0 && refDisk > 0 && f.SizeGb > 0
                        ? Math.Round(refTps * Math.Min(3.5, Math.Max(0.3, refDisk / f.SizeGb)), 1)
                        : 0;
                    // a small file is mostly per-token overhead, not weight reading: the same
                    // damping the model list uses, so a card and its check agree
                    if (!res.IsMoe && !namedMoe) q.EstTps = Math.Round(q.EstTps * f.SizeGb / (f.SizeGb + 0.4), 1);
                    q.RunMode = row.Str("run_mode") ?? "";
                }
                else
                {
                    // llmfit has no row for this repo at all, so borrow the
                    // machine's own bytes-per-second instead of showing nothing
                    q.EstTps = MachineSpeed.RawTps(catalogGbPerSec, f.SizeGb);
                    if (q.EstTps > 0) usedBandwidthModel = true;
                    q.RunMode = "";
                }
                // the conversation length the memory above was worked out for
                q.UsableContext = own.Context;
                // past fast memory, the part in system memory is read slower for every token
                if (own.Verdict is FitRule.Slow or FitRule.No)
                    q.EstTps = Math.Round(q.EstTps * FitRule.SpillFactor(own, limits, slowerBy, shared), 1);
                // a file that was run here shows what it did; the rest show the raw number
                // corrected into a range — by a real run on this machine when there is one
                var kept = Measurements.Find(repo.RepoId, f.Name);
                if (kept is { QualityScore: > 0 }) q.QualityTested = kept.QualityScore;
                if (kept is { GenTps: > 0 } run)
                {
                    (q.TpsLow, q.TpsHigh, q.EstTps) = (run.GenTps, run.GenTps, run.GenTps);
                    q.Measured = true;
                    q.FirstTokenMs = run.FirstTokenMs;
                }
                else
                {
                    (q.TpsLow, q.TpsHigh) = SpeedEstimate.Range(q.EstTps, hw.GpuClass, calibration);
                    q.EstTps = SpeedEstimate.Typical(q.TpsLow, q.TpsHigh);
                }

                q.Verdict = own.Verdict switch
                {
                    FitRule.Perfect or FitRule.Good => "runs",
                    FitRule.Tight => "tight",
                    FitRule.Slow => "slow",
                    _ => "no",
                };
                if (q.Verdict == "runs")
                    q.Reasons.Add($"Uses {q.UtilPct:0.#}% of the {budget:0.#} GB memory budget.");
                else if (q.Verdict == "tight")
                    q.Reasons.Add($"Needs {q.MemoryRequiredGb:0.#} GB against a {budget:0.#} GB budget — close other apps, or pick a smaller quant.");
                else if (q.Verdict == "slow")
                    q.Reasons.Add($"Needs {q.MemoryRequiredGb:0.#} GB, more than the {budget:0.#} GB budget: the rest runs from system memory, slower.");
                else
                    q.Reasons.Add($"Needs {q.MemoryRequiredGb:0.#} GB, more than this PC can hold ({limits.CeilingGb * FitRule.CeilingShare:0.#} GB).");

                if (hw.BudgetGb > 0 && f.SizeGb > hw.FreeRamGb)
                    q.Reasons.Add($"Only {hw.FreeRamGb:0.#} GB of system RAM is free right now — quit apps before loading.");
                if (f.Shards is > 1)
                    q.Reasons.Add($"Downloaded as {f.Shards} parts — {q.SizeGb:0.##} GB is the whole model, not one shard.");
            }

            // A version that was run here is the best estimator for its siblings: same
            // model, only the bytes read per token differ. It beats any general formula.
            var anchor = res.Quants.Where(x => x.Measured && x.SizeGb > 0).OrderByDescending(x => x.SizeGb).FirstOrDefault();
            if (anchor is not null)
                foreach (var q in res.Quants.Where(x => !x.Measured && x.SizeGb > 0))
                {
                    var mid = anchor.EstTps * Math.Clamp((anchor.SizeGb + 0.4) / (q.SizeGb + 0.4), 0.45, 1.9);
                    (q.TpsLow, q.TpsHigh, q.EstTps) = (Math.Round(mid * 0.88, 1), Math.Round(mid * 1.12, 1), Math.Round(mid, 1));
                }

            // The recommendation: the largest version that fits and still writes at
            // reading pace. A bigger file that fits but crawls is not the better pick, and
            // an unquantised one (F16) buys nothing a Q8 does not already give.
            // The rule itself is FitRule.Pick, shared with zer0ne code.
            var runs = res.Quants.Where(q => q.Verdict == "runs").ToList();
            var builds = res.Quants.Select(q => (Option: q, Build: new FitRule.Build(q.Quant ?? "", q.SizeGb, q.Verdict switch
            {
                // perfect or good: the pick only asks whether there is room to spare
                "runs" => FitRule.Good,
                "tight" => FitRule.Tight,
                "slow" => FitRule.Slow,
                _ => FitRule.No,
            }, q.EstTps, q.MemoryRequiredGb))).ToList();
            var chosen = FitRule.Pick([.. builds.Select(b => b.Build)]);
            // nothing runs at all: the smallest is still shown, as what comes closest
            res.Primary = chosen is null
                ? res.Quants.OrderBy(q => q.MemoryRequiredGb).FirstOrDefault()
                : builds.First(b => ReferenceEquals(b.Build, chosen)).Option;
            if (res.Primary is { Verdict: "runs" } best)
                res.PickReason = best.EstTps < FitRule.ReadingPace || FitRule.IsUnquantised(best.Quant)
                    ? "No version reaches reading pace on this PC. Recommended: the usual best trade between quality and speed."
                    : runs.Any(q => q.SizeGb > best.SizeGb)
                        ? "Recommended: the largest version that still writes at reading pace here."
                        : "Recommended: the largest version that fits, and it writes at reading pace.";
            if (res.Primary != null) res.Primary.Recommended = true;

            // What the recommended quant does on this machine — the number the user
            // decides on, so it is worth showing in the Check tab before the download.
            if (res.Primary is { } pick && hw.BudgetGb > 0)
            {
                var (nameTotal, nameActive, nameMoe) = TurboFit.ParamsFromName(pick.File);
                // ParamText's rule: callers mix raw counts and billions
                double ToB(double? v) => v is null or <= 0 ? 0 : v > 1e6 ? v.Value / 1e9 : v.Value;
                var totalB = nameTotal > 0 ? nameTotal : ToB(res.ParamsTotal);
                var activeB = nameActive > 0 ? nameActive : (res.IsMoe && totalB > 0 ? Math.Round(totalB * 0.1, 2) : ToB(res.ParamsActive));
                res.Turbo = TurboFit.Info(TurboFit.Plan(new TurboInput
                {
                    FileName = pick.File,
                    // the projector loads with the model and takes the same memory
                    FileBytes = (long)((pick.SizeGb + projectorGb) * 1e9),
                    ParamsTotalB = totalB,
                    ParamsActiveB = activeB,
                    IsMoe = nameMoe || res.IsMoe,
                    VramGb = hw.BudgetGb,
                    RamGb = hw.TotalRamGb,
                    FreeDiskGb = FreeDiskGb(),
                    WantedCtx = res.ContextMax ?? 8192,
                    GpuName = hw.GpuName,
                    Shared = hw.GpuClass == SpeedEstimate.Integrated,
                }));
            }

            // llmfit knows nothing about this repo, so the speeds above came from
            // the machine's own bandwidth — say so instead of implying a catalog hit
            if (usedBandwidthModel) res.LlmfitEstimateBasis = "bandwidth_model";

            res.ModelName = repo.RepoId.Split('/')[1];
            res.Provider = repo.RepoId.Split('/')[0];
            if (rows.TryGetValue($"{label} [{res.Primary?.Quant ?? "file"}]", out var primRow))
            {
                res.ParamsTotal = primRow.Num("params_b") is double pb && pb > 0 ? pb * 1e9 : res.ParamsTotal;
            }
            if (!res.IsMoe && res.ParamsTotal is > 0 && res.ParamsActive is null)
                res.ParamsActive = null;
            // A config that describes only part of a model (a vision wrapper, a nested text
            // config) gives a count that is far off, and the page then says "~1B" for a 9B
            // model. The size in the name is the author's own. Display only: the scoring
            // above already ran with the count it has always used.
            var named = TurboFit.ParamsFromName(repo.RepoId.Split('/')[^1]);
            if (named is { TotalB: > 0, Moe: false } && !repo.MoE &&
                (res.ParamsTotal is not > 0 || res.ParamsTotal < named.TotalB * 0.5e9 || res.ParamsTotal > named.TotalB * 2e9))
                res.ParamsTotal = named.TotalB * 1e9;

            var ctx = res.ContextMax is > 0 ? (int)Math.Min(res.ContextMax.Value, 16384) : 8192;
            var mmprojSwitch = "";
            if (res.MmprojFiles.Count > 0)
                mmprojSwitch = $" --mmproj \"{res.MmprojFiles[0]}\"";
            var draftSwitch = res.DraftFiles.Count > 0 ? $" --spec-type draft-mtp --spec-draft-model \"{res.DraftFiles[0]}\"" : "";
            res.ServeCommand =
                $"llama-server -m \"{res.Primary?.File}\" -ngl 99 -c {ctx} --jinja --cache-type-k q8_0 --cache-type-v q8_0{mmprojSwitch}{draftSwitch} --port 8080";
            res.Ok = true;
        }
        catch (Exception ex)
        {
            res.Error = $"Scoring failed: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(customPath)) File.Delete(customPath); } catch { }
        }
        return res;
    }

    private static async Task<AnalysisResult> AnalyzeByNameAsync(string name, HardwareInfo hw, AnalysisResult res, CancellationToken ct)
    {
        res.Kind = "catalog";
        var budgetArg = hw.BudgetGb > 0 ? $"--memory={hw.BudgetGb:0.#}G " : "";
        var root = await LlmfitRunner.RunJsonAsync($"{budgetArg}info \"{name.Replace("\"", "")}\" --json", timeoutMs: 60_000, ct: ct);
        if (root is null || root.Value.ValueKind != JsonValueKind.Object)
        {
            res.Error = $"Could not analyse '{name}'. If it is a Hugging Face repo, paste its full link (huggingface.co/org/repo).";
            return res;
        }

        if (root.Value.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            var kind = e.Str("kind") ?? "error";
            var msg = e.Str("message") ?? "unknown error";
            if (kind.Contains("selector", StringComparison.OrdinalIgnoreCase))
            {
                res.Error = $"More than one model matches '{name}'. Pick one:";
                foreach (var line in msg.Split('\n'))
                {
                    var t = line.Trim().TrimStart('-').Trim();
                    if (t.Contains('/') && !t.Contains(' ')) res.Suggestions.Add(t);
                }
                return res;
            }
            res.Error = msg;
            return res;
        }

        if (!root.Value.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array || models.GetArrayLength() == 0)
        {
            res.Error = $"'{name}' is not in the model catalog. Paste its Hugging Face link instead and it will be profiled from the repo.";
            return res;
        }

        var m = models[0];
        res.Ok = true;
        res.ModelName = m.Str("name");
        res.Provider = m.Str("provider");
        res.Role = m.Str("category") ?? m.Str("use_case") ?? "Chat / general";
        res.ContextMax = m.Int("context_length");
        res.ParamsText = m.Str("parameter_count");
        res.ParamsTotal = m.Num("params_b");
        res.IsMoe = m.B("is_moe") ?? false;
        res.License = m.Str("license");
        res.BaseModel = m.Str("name");
        res.OllamaName = m.Str("ollama_name");
        res.RepoId = m.Str("name");

        var basis = m.TryGetProperty("estimate_basis", out var eb) ? eb.Str("method") ?? "unknown" : "unknown";
        res.LlmfitEstimateBasis = basis;
        res.EstimateCalibrated = m.Str("estimate_confidence") switch { "measured" or "calibrated" => true, _ => false };

        var q = new QuantOption
        {
            File = m.Str("best_quant") is { Length: > 0 } bq ? $"{bq} (estimated — repo not scanned)" : "GGUF",
            SizeGb = m.Num("disk_size_gb") ?? 0,
            Quant = m.Str("best_quant"),
            FitLevel = m.Str("fit_level") ?? "Unknown",
            UtilPct = m.Num("utilization_pct") ?? 0,
            EstTps = m.Num("measured_tps") ?? m.Num("estimated_tps") ?? 0,
            MemoryRequiredGb = m.Num("memory_required_gb") ?? 0,
            Score = m.Num("score") ?? 0,
            RunMode = m.Str("run_mode") ?? "",
            UsableContext = m.Num("usable_context") ?? 0,
            Verdict = (m.Str("fit_level")) switch
            {
                "Perfect" or "Good" => "runs",
                "Marginal" => "tight",
                _ => "no",
            },
        };
        // a speed llmfit measured on this machine is shown as it is; an estimate becomes a range
        if (m.Num("measured_tps") is > 0)
            (q.TpsLow, q.TpsHigh) = (q.EstTps, q.EstTps);
        else
        {
            var calibration = Measurements.Calibration(hw.GpuName);
            res.SpeedCalibrated = calibration > 0;
            (q.TpsLow, q.TpsHigh) = SpeedEstimate.Range(q.EstTps, hw.GpuClass, calibration);
            q.EstTps = SpeedEstimate.Typical(q.TpsLow, q.TpsHigh);
        }
        if (m.TryGetProperty("score_components", out var parts)) res.Quality = parts.Num("quality") ?? 0;
        q.Reasons.Add(q.Verdict switch
        {
            "runs" => $"Uses {q.UtilPct:0.#}% of the {hw.BudgetGb:0.#} GB memory budget.",
            "tight" => $"Needs {q.MemoryRequiredGb:0.#} GB against a {hw.BudgetGb:0.#} GB budget.",
            _ => "Does not fit — needs more memory than this machine offers.",
        });
        q.Recommended = true;
        res.Quants.Add(q);
        res.Primary = q;

        if (res.OllamaName is { Length: > 0 })
            res.DownloadCommand = $"ollama pull {res.OllamaName}";
        if (q.Verdict != "no")
            res.ServeCommand = "llama-server -m <downloaded-file>.gguf -ngl 99 -c 16384 --jinja --cache-type-k q8_0 --cache-type-v q8_0 --port 8080";
        return res;
    }

    /// <summary>
    /// The name an entry is scored under: the repo without its size. llmfit reads a
    /// size out of the name and drops an entry whose parameter count disagrees with
    /// it — and it misreads decimals ("0.6B" as 6B), so a correct entry was dropped.
    /// The count is passed explicitly; the family name is what llmfit still needs.
    /// </summary>
    internal static string ScoringName(string repoId) =>
        Regex.Replace(repoId, @"[-_ ]?(?:\d+x)?A?\d+(?:\.\d+)?[BbMm](?=[-_. ]|$)", "");

    /// <summary>Cache bytes per token at 16-bit: from the attention shape in config.json, else from the size.</summary>
    private static double KvBytesPerToken(HfRepo repo, double? paramsTotal, double? paramsActive)
    {
        var l = repo.NumLayers ?? 0;
        var kvHeads = repo.Arch.GetValueOrDefault("num_key_value_heads") > 0 ? repo.Arch["num_key_value_heads"] : repo.Arch.GetValueOrDefault("num_attention_heads");
        var headDim = repo.Arch.GetValueOrDefault("head_dim") > 0 ? repo.Arch["head_dim"] : (repo.HiddenSize ?? 0) / Math.Max(1, repo.Arch.GetValueOrDefault("num_attention_heads"));
        if (l > 0 && kvHeads > 0 && headDim > 0) return FitRule.KvFromShape(l, kvHeads, headDim);
        // callers mix raw counts and billions (ParamText's rule)
        static double B(double? v) => v is null or <= 0 ? 0 : v > 1e6 ? v.Value / 1e9 : v.Value;
        return FitRule.KvFromParams(Math.Max(0.5, B(paramsTotal)), B(paramsActive));
    }

    // MoE decode speed scales with the parameters touched per token, and HF
    // configs rarely publish that number, so derive it: the repo name often
    // carries it outright (…-23B-A3B-GGUF), otherwise the expert topology does.
    private static (double? Total, double? Active) ParamCounts(HfRepo repo)
    {
        var name = repo.RepoId;
        var m = Regex.Match(name, @"(\d+(?:\.\d+)?)\s*b\s*-\s*a\s*(\d+(?:\.\d+)?)\s*b", RegexOptions.IgnoreCase);
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nTotal) &&
            double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nActive) &&
            nTotal > 0 && nActive > 0 && nActive <= nTotal)
            return (nTotal * 1e9, nActive * 1e9);

        var total = HfClient.EstimateParams(repo);
        if (!repo.MoE || repo.ActiveExperts is not > 0) return (total, null);

        var h = repo.HiddenSize ?? 0;
        var l = repo.Arch.GetValueOrDefault("num_hidden_layers");
        var experts = repo.NumExperts ?? 0;
        var active = repo.ActiveExperts ?? 0;
        var moe = repo.Arch.GetValueOrDefault("moe_intermediate_size");
        if (h <= 0 || l <= 0 || experts <= 0 || active > experts || moe <= 0) return (total, null);

        var vocab = repo.Arch.GetValueOrDefault("vocab_size") is > 0 and var v ? v : 150000;
        var attnHeads = repo.Arch.GetValueOrDefault("num_attention_heads") > 0 ? repo.Arch["num_attention_heads"] * 128 : h;
        var kvHeads = repo.Arch.GetValueOrDefault("num_key_value_heads") > 0 ? repo.Arch["num_key_value_heads"] * 128 : attnHeads;
        var perLayer = 4.0 * h * h + 2.0 * h * attnHeads + 2.0 * h * kvHeads;
        perLayer += active * 3.0 * h * moe;                      // experts touched per token
        var dense = repo.Arch.GetValueOrDefault("intermediate_size");
        if (dense > 0) perLayer += 3.0 * h * dense;               // shared expert / dense FFN
        return (total, (perLayer * l + 2.0 * vocab * h) / 2.0);
    }

    private static string ParamText(double? raw)
    {
        if (raw is null or <= 0) return "1B";
        // callers mix raw param counts and billions — normalise to billions
        var b = raw > 1e6 ? raw / 1e9 : raw;
        if (b < 1) return $"{b * 1000:0}M";
        return $"{b:0.#}B";
    }

    private static string GuessArch(HfRepo repo)
    {
        if (repo.Arch.TryGetValue("architectures", out _)) return "from config.json";
        if (repo.MoE) return "MoE";
        return "dense";
    }

    /// <summary>Free space on the drive the app runs from, for the "streams from disk" answer.</summary>
    private static double FreeDiskGb()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace / 1e9;
        }
        catch { return 0; }
    }

    internal static void DebugDiag(string line)
    {
        if (Environment.GetEnvironmentVariable("MODELFIT_DIAG") != "1") return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "modelfit-diag.log"),
                $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
