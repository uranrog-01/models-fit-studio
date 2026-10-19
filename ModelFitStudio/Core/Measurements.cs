using System.IO;
using System.Text.Json;

namespace ModelFitStudio.Core;

/// <summary>One real run of a downloaded model on this machine.</summary>
public sealed class Measurement
{
    public string Repo { get; set; } = "";
    public string File { get; set; } = "";
    public double SizeGb { get; set; }
    /// <summary>Tokens written per second, the median of the runs.</summary>
    public double GenTps { get; set; }
    /// <summary>Tokens of prompt read per second.</summary>
    public double PromptTps { get; set; }
    /// <summary>From sending a short prompt to the first token, in milliseconds.</summary>
    public double FirstTokenMs { get; set; }
    public double LoadSeconds { get; set; }
    /// <summary>What the bandwidth model said for this file before any correction; 0 when unknown.</summary>
    public double RawTps { get; set; }
    /// <summary>True when the GPU could not load the file and the CPU ran it: not a number for GPU estimates.</summary>
    public bool Fallback { get; set; }
    /// <summary>Share of the quality test answered right, 0–100; 0 when the file was not tested.</summary>
    public double QualityScore { get; set; }
    public int QualityCorrect { get; set; }
    public int QualityTotal { get; set; }
    /// <summary>Right answers per skill.</summary>
    public Dictionary<string, int>? QualityParts { get; set; }
    public string GpuName { get; set; } = "";
    public string Engine { get; set; } = "";
    public string At { get; set; } = "";
    /// <summary>"Best for agents": seconds to read a 12,000-token request, writing speed, right tool picks, points; 0 when not tested.</summary>
    public double AgentReadSeconds { get; set; }
    public double AgentWriteTps { get; set; }
    public int AgentCorrect { get; set; }
    public int AgentTotal { get; set; }
    public int AgentPoints { get; set; }
    public bool AgentThinking { get; set; }
}

/// <summary>
/// The measured speeds, kept in %LOCALAPPDATA%\ModelFitStudio\measurements.json.
/// A measured file shows its real number; every other estimate on the same GPU is
/// corrected by what the measured ones turned out to be.
/// </summary>
public static class Measurements
{
    private static readonly object Gate = new();
    private static List<Measurement>? _all;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModelFitStudio", "measurements.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<Measurement> All()
    {
        lock (Gate)
        {
            if (_all is not null) return [.. _all];
            try
            {
                _all = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<Measurement>>(File.ReadAllText(FilePath), Json) ?? []
                    : [];
            }
            catch { _all = []; }
            return [.. _all];
        }
    }

    public static Measurement? Find(string? repo, string? file) =>
        string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(file)
            ? null
            : All().FirstOrDefault(m => Same(m, repo, file));

    /// <summary>Keeps the newest run of a file: a second measurement replaces the first.</summary>
    public static void Save(Measurement m)
    {
        lock (Gate)
        {
            var list = All();
            if (list.FirstOrDefault(x => Same(x, m.Repo, m.File)) is { QualityScore: > 0 } old)
            {
                m.QualityScore = old.QualityScore;
                m.QualityCorrect = old.QualityCorrect;
                m.QualityTotal = old.QualityTotal;
                m.QualityParts = old.QualityParts;
            }
            if (list.FirstOrDefault(x => Same(x, m.Repo, m.File)) is { AgentTotal: > 0 } kept)
            {
                m.AgentReadSeconds = kept.AgentReadSeconds;
                m.AgentWriteTps = kept.AgentWriteTps;
                m.AgentCorrect = kept.AgentCorrect;
                m.AgentTotal = kept.AgentTotal;
                m.AgentPoints = kept.AgentPoints;
                m.AgentThinking = kept.AgentThinking;
            }
            list.RemoveAll(x => Same(x, m.Repo, m.File));
            list.Add(m);
            Write(list);
        }
    }

    /// <summary>Keeps an agent test next to whatever else the file already has.</summary>
    public static void SaveAgent(string repo, string file, double sizeGb, AgentTestResult r, bool thinking, string gpuName, string engine)
    {
        lock (Gate)
        {
            var list = All();
            var m = list.FirstOrDefault(x => Same(x, repo, file));
            if (m is null)
            {
                m = new Measurement { Repo = repo, File = file, SizeGb = sizeGb, GpuName = gpuName, Engine = engine };
                list.Add(m);
            }
            m.AgentReadSeconds = r.ReadSeconds;
            m.AgentWriteTps = r.WriteTps;
            m.AgentCorrect = r.Correct;
            m.AgentTotal = r.Total;
            m.AgentPoints = r.Points;
            m.AgentThinking = thinking;
            m.At = DateTime.Now.ToString("s");
            Write(list);
        }
    }

    /// <summary>Keeps a quality test next to whatever speed the file already has.</summary>
    public static void SaveQuality(string repo, string file, double sizeGb, QualityResult r, string gpuName, string engine)
    {
        lock (Gate)
        {
            var list = All();
            var m = list.FirstOrDefault(x => Same(x, repo, file));
            if (m is null)
            {
                m = new Measurement { Repo = repo, File = file, SizeGb = sizeGb, GpuName = gpuName, Engine = engine };
                list.Add(m);
            }
            m.QualityScore = r.Score;
            m.QualityCorrect = r.Correct;
            m.QualityTotal = r.Total;
            m.QualityParts = r.Parts;
            m.At = DateTime.Now.ToString("s");
            Write(list);
        }
    }

    public static bool Remove(string repo, string file)
    {
        lock (Gate)
        {
            var list = All();
            var removed = list.RemoveAll(x => Same(x, repo, file)) > 0;
            if (removed) Write(list);
            return removed;
        }
    }

    public static int Clear()
    {
        lock (Gate)
        {
            var n = All().Count;
            Write([]);
            return n;
        }
    }

    /// <summary>
    /// The correction for estimates on this GPU, from the runs made on it. Dense
    /// models of a real size only: an MoE reads a fraction of its file per token and a
    /// tiny model is all overhead, so neither says how fast the machine reads weights.
    /// </summary>
    public static double Calibration(string? gpuName) =>
        SpeedEstimate.Calibration(All()
            .Where(m => string.Equals(m.GpuName, gpuName ?? "", StringComparison.OrdinalIgnoreCase))
            .Where(m => m.GenTps > 0 && m.SizeGb >= 0.7 && !m.Fallback && !TurboFit.ParamsFromName(m.File).Moe)
            .Select(m => (m.GenTps, m.RawTps)));

    private static bool Same(Measurement m, string repo, string file) =>
        string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase);

    private static void Write(List<Measurement> list)
    {
        _all = [.. list];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
        }
        catch { }
    }
}
