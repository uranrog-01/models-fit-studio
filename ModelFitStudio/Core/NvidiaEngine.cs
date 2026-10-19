using System.Globalization;
using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// Which llama.cpp build an NVIDIA card should get. The built-in engine (Vulkan) runs
/// on every card; NVIDIA's own (CUDA) is faster there, comes in two versions, and each
/// version needs a driver new enough and a card it still supports. Pure decisions,
/// no I/O, so they can be tested without the hardware.
/// </summary>
public static class NvidiaEngine
{
    private static readonly Regex Card = new(@"nvidia|geforce|\bRTX\b|\bGTX\b|quadro|\btitan\b|\btesla\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // CUDA 13 dropped the Maxwell, Pascal and Volta generations: these stay on CUDA 12
    private static readonly Regex Older = new(
        @"GTX\s*(7[45]0|9\d0|10\d0)|GT\s*10\d0|MX\s*[1-3]\d0|TITAN\s*(X|Xp|V)\b|Quadro\s*[KMP]\d|Tesla\s*[KMPV]\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // the oldest Windows driver each CUDA generation runs on
    private const double Cuda13Driver = 580.88;
    private const double Cuda12Driver = 528.33;

    public static bool IsNvidia(string? gpuName) => !string.IsNullOrWhiteSpace(gpuName) && Card.IsMatch(gpuName);

    /// <summary>
    /// The driver as NVIDIA numbers it (580.88), from either form Windows gives:
    /// "580.88" from nvidia-smi, or the file version "32.0.15.8088", whose last five
    /// digits are the same number.
    /// </summary>
    public static double DriverVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Trim();
        if (Regex.IsMatch(text, @"^\d{3}\.\d{1,2}$") &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return plain;

        var digits = Regex.Replace(text, @"\D", "");
        if (text.Count(c => c == '.') < 3 || digits.Length < 5) return 0;
        var tail = digits[^5..];
        return double.Parse(tail[..3] + "." + tail[3..], CultureInfo.InvariantCulture);
    }

    /// <summary>"13.4", "12.4", or null when the card should stay on the built-in engine.</summary>
    public static string? CudaFor(string? gpuName, double driver)
    {
        if (!IsNvidia(gpuName)) return null;
        if (driver >= Cuda13Driver && !Older.IsMatch(gpuName!)) return "13.4";
        return driver >= Cuda12Driver ? "12.4" : null;
    }

    /// <summary>One plain sentence for Settings: what was found and what follows from it.</summary>
    public static string Explain(string? gpuName, double driver, bool installed)
    {
        if (!IsNvidia(gpuName)) return "";
        if (installed) return "NVIDIA card found. The NVIDIA engine is active.";
        if (CudaFor(gpuName, driver) is not null)
            return "NVIDIA card found. It runs on the built-in engine now; the NVIDIA engine is faster.";
        return driver > 0
            ? $"NVIDIA card found, but its driver ({driver.ToString("0.00", CultureInfo.InvariantCulture)}) is too old for the NVIDIA engine. Update the driver to use it."
            : "NVIDIA card found, but its driver could not be read. The built-in engine is used.";
    }
}
