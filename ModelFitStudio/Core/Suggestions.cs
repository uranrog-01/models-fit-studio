using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

/// <summary>
/// Reads every model that fits this PC from the model database, sizes and times each
/// the way the model list does, and hands them to <see cref="Suggest"/>. The answer is
/// kept until the memory budget or the measured speed changes.
/// </summary>
public static class Suggestions
{
    private static List<Suggestion>? _cached;
    private static string _cacheKey = "";

    public static async Task<List<Suggestion>> ForAsync(HardwareInfo hw, CancellationToken ct = default)
    {
        var calibration = Measurements.Calibration(hw.GpuName);
        var key = $"{hw.BudgetGb:0.#}|{hw.GpuClass}|{calibration:0.00}";
        if (_cached is not null && _cacheKey == key) return _cached;
        if (!LlmfitRunner.Available) return [];

        var speed = await MachineSpeed.GbPerSecAsync(hw, ct);
        var budgetArg = hw.BudgetGb > 0 ? $"--memory={hw.BudgetGb:0.#}G " : "";
        var root = await LlmfitRunner.RunJsonAsync($"{budgetArg}fit --json -n 6000", timeoutMs: 90_000, ct: ct);
        if (root is not JsonElement doc || !doc.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return [];

        // the same rows zer0ne code builds (Suggest.Rows), from the same llmfit list
        _cached = Suggest.Pick(Suggest.Rows(models.EnumerateArray(), hw.BudgetGb, speed, hw.GpuClass, calibration));
        _cacheKey = key;
        return _cached;
    }
}
