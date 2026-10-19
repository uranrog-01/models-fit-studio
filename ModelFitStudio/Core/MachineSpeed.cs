using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ModelFitStudio.Core;

/// <summary>
/// This machine's raw decode rate in gigabytes of weights per second. Decoding a
/// token reads every active weight once, so tok/s × file size is roughly constant
/// for a machine; llmfit's own rows give that constant. It is the raw figure:
/// <see cref="SpeedEstimate"/> turns it into the range the app shows.
/// </summary>
public static class MachineSpeed
{
    private static double _gbPerSec;
    private static double _forBudget = -1;

    /// <summary>The constant for this machine, asked from llmfit once per memory budget. Zero when it cannot be read.</summary>
    public static async Task<double> GbPerSecAsync(HardwareInfo hw, CancellationToken ct = default)
    {
        if (_gbPerSec > 0 && Math.Abs(_forBudget - hw.BudgetGb) < 0.5) return _gbPerSec;
        if (!LlmfitRunner.Available) return 0;

        // a short run is enough: the median of a few hundred rows is the same as of ten thousand
        var budgetArg = hw.BudgetGb > 0 ? $"--memory={hw.BudgetGb:0.#}G " : "";
        var root = await LlmfitRunner.RunJsonAsync($"{budgetArg}fit --json -n 200", timeoutMs: 60_000, ct: ct);
        if (root is not JsonElement doc || !doc.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return 0;

        var found = FromRows(models);
        if (found > 0)
        {
            _gbPerSec = found;
            _forBudget = hw.BudgetGb;
        }
        return found;
    }

    /// <summary>The median of tok/s × size over llmfit rows (<see cref="SpeedEstimate.GbPerSecFromRows"/>).</summary>
    public static double FromRows(JsonElement models) => SpeedEstimate.GbPerSecFromRows(models.EnumerateArray());

    /// <summary>Raw speed for a file of this size, from the machine's constant (<see cref="SpeedEstimate.RawTps"/>).</summary>
    public static double RawTps(double gbPerSec, double sizeGb) => SpeedEstimate.RawTps(gbPerSec, sizeGb);
}
