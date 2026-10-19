using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace ModelFitStudio.Core;

public sealed class HardwareInfo
{
    public string CpuName { get; set; } = "Unknown CPU";
    public int CpuCores { get; set; }
    public double TotalRamGb { get; set; }
    public double FreeRamGb { get; set; }
    public string GpuName { get; set; } = "No GPU detected";
    public bool HasGpu { get; set; }
    public int GpuCount { get; set; }
    public bool UnifiedMemory { get; set; }
    public string Backend { get; set; } = "unknown";
    /// <summary>cpu | integrated | discrete — decides how the speed estimate is corrected.</summary>
    public string GpuClass => SpeedEstimate.GpuClass(GpuName, HasGpu, UnifiedMemory);
    /// <summary>True for an NVIDIA card: it can use NVIDIA's own, faster engine.</summary>
    public bool IsNvidia => HasGpu && NvidiaEngine.IsNvidia(GpuName);
    /// <summary>The NVIDIA driver as NVIDIA numbers it (580.88); 0 when there is none or it cannot be read.</summary>
    public double NvidiaDriver { get; set; }
    /// <summary>VRAM budget used for fit analysis (GB).</summary>
    public double BudgetGb { get; set; }
    /// <summary>What llmfit's default detection reported, before correction (GB).</summary>
    public double DetectedVramGb { get; set; }
    /// <summary>Total shared/graphics pool reported by the Vulkan driver (GB), when available.</summary>
    public double? PoolGb { get; set; }
    public double? PoolFreeGb { get; set; }
    public bool VramCorrected { get; set; }
    public List<string> Notes { get; } = [];
    public string? LlamaServerPath { get; set; }
    public string CorrectedFrom { get; set; } = "";
}

public static class HardwareScanner
{
    public static async Task<(HardwareInfo? Info, string? Error)> ScanAsync(CancellationToken ct = default)
    {
        if (!LlmfitRunner.Available)
            return (null, "Hardware profiling tool not found. Place required tools in the tools\\ folder next to the app, or set LLMFIT_EXE.");

        var root = await LlmfitRunner.RunJsonAsync("system --json", timeoutMs: 45_000, ct: ct);
        if (root is not JsonElement doc || doc.ValueKind != JsonValueKind.Object)
            return (null, "Could not read hardware (system scan produced no data).");

        JsonElement sys;
        if (doc.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.Object)
            sys = s;
        else
            sys = doc; // tolerate the plain-shape output

        var hw = new HardwareInfo
        {
            CpuName = sys.Str("cpu_name") ?? "Unknown CPU",
            CpuCores = sys.Int("cpu_cores") ?? 0,
            TotalRamGb = sys.Num("total_ram_gb") ?? 0,
            FreeRamGb = sys.Num("available_ram_gb") ?? 0,
            GpuName = sys.Str("gpu_name") ?? "No GPU detected",
            HasGpu = sys.B("has_gpu") ?? false,
            GpuCount = sys.Int("gpu_count") ?? 0,
            UnifiedMemory = sys.B("unified_memory") ?? false,
            Backend = (sys.Str("backend") ?? "unknown").ToUpperInvariant(),
            DetectedVramGb = sys.Num("gpu_vram_gb") ?? 0,
        };

        var gpus = sys.TryGetProperty("gpus", out var g) ? g : default;
        if (gpus.ValueKind == JsonValueKind.Array && gpus.GetArrayLength() > 0)
        {
            var first = gpus[0];
            hw.GpuName = first.Str("name") ?? hw.GpuName;
            hw.UnifiedMemory = first.B("unified_memory") ?? hw.UnifiedMemory;
            hw.DetectedVramGb = first.Num("vram_gb") ?? hw.DetectedVramGb;
            hw.Backend = (first.Str("backend") ?? hw.Backend).ToUpperInvariant();
        }

        // a llama.cpp the machine already has, as llmfit found it: only used below to ask the driver for its real pool
        if (doc.TryGetProperty("providers", out var prov) && prov.TryGetProperty("llama.cpp", out var lc))
        {
            hw.LlamaServerPath = lc.Str("llama_server_path");
        }
        // the app's own engine answers the same question on a PC that has no other
        if (string.IsNullOrWhiteSpace(hw.LlamaServerPath) || !File.Exists(hw.LlamaServerPath))
            hw.LlamaServerPath = Engine.Plain()?.ServerPath;

        // for trying the NVIDIA paths on a PC without such a card
        if (Environment.GetEnvironmentVariable("MODELFIT_GPU_NAME") is { Length: > 0 } pretend)
        {
            hw.GpuName = pretend;
            hw.HasGpu = true;
        }

        // --- VRAM correction -------------------------------------------------
        // Windows reports graphics memory through a 32-bit field, so pools larger
        // than 4 GB wrap around (a 16 GB pool shows up as ~2 GB). llmfit then
        // scores models against the wrapped value and refuses everything above
        // it. Recover the real pool from the driver name and llama.cpp.
        var nameClaimGb = NameClaimedVram(hw.GpuName);
        var (poolTotal, poolFree) = await VulkanPoolAsync(hw, ct);

        var candidates = new List<(string Source, double Gb)>();
        if (nameClaimGb is > 0) candidates.Add(("driver name", nameClaimGb.Value));
        if (poolTotal is > 0) candidates.Add(("Vulkan device memory", poolTotal.Value));
        if (hw.DetectedVramGb > 0) candidates.Add(("registry", hw.DetectedVramGb));

        var best = candidates.Count > 0 ? candidates.OrderByDescending(c => c.Gb).First() : ("registry", 0);
        hw.PoolGb = poolTotal;
        hw.PoolFreeGb = poolFree;
        hw.VramCorrected = best.Gb > hw.DetectedVramGb + 0.25;
        hw.CorrectedFrom = best.Source;

        // Cap by what system RAM can actually back: a graphics pool cannot exceed
        // RAM minus a working reserve for the OS and apps.
        var ramCap = hw.TotalRamGb > 6 ? hw.TotalRamGb - 6 : hw.TotalRamGb * 0.7;
        hw.BudgetGb = best.Gb > 0 ? Math.Min(best.Gb, ramCap) : 0;

        if (hw.VramCorrected)
            hw.Notes.Add($"VRAM detected as {hw.DetectedVramGb:0.#} GB (Windows wraps pools over 4 GB) — corrected to {hw.BudgetGb:0.#} GB from {best.Source}.");

        if (hw.HasGpu && hw.BudgetGb <= 0)
            hw.Notes.Add("GPU detected but its memory size could not be determined; fit will use system RAM only.");
        else if (!hw.HasGpu)
            hw.Notes.Add("No GPU detected — models will be scored for CPU inference.");

        if (hw.IsNvidia)
            hw.NvidiaDriver = NvidiaEngine.DriverVersion(Environment.GetEnvironmentVariable("MODELFIT_NVIDIA_DRIVER")) is > 0 and var d
                ? d
                : await Task.Run(Engine.NvidiaDriver, ct);
        Engine.PreferNvidia = hw.IsNvidia;

        return (hw, null);
    }

    /// <summary>Parses "(16GB)" / "(16 GB)" style claims in Intel/AMD driver names.</summary>
    private static double? NameClaimedVram(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = Regex.Match(name, @"\((\d+(?:\.\d+)?)\s*Gi?B\)", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Asks llama.cpp (Vulkan) for the real device pool: "Vulkan0: Name (18309 MiB, 17541 MiB free)".</summary>
    private static async Task<(double? Total, double? Free)> VulkanPoolAsync(HardwareInfo hw, CancellationToken ct)
    {
        var server = hw.LlamaServerPath;
        if (string.IsNullOrWhiteSpace(server) || !File.Exists(server)) return (null, null);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = server,
                Arguments = "--list-devices",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (null, null);
            var outp = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) outp.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) outp.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(8), ct);

            var text = outp.ToString();
            var m = Regex.Match(text, @"Vulkan\d+:\s*([^(]+)\((\d+)\s*MiB,\s*(\d+)\s*MiB\s*free\)", RegexOptions.IgnoreCase);
            if (!m.Success) return (null, null);
            double gib(int mib) => Math.Round(mib / 1024.0, 2);
            return (gib(int.Parse(m.Groups[2].Value)), gib(int.Parse(m.Groups[3].Value)));
        }
        catch
        {
            return (null, null);
        }
    }
}
