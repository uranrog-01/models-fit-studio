using System.Text.Json;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// One Suggest rule for two apps. zer0ne code picks its suggestions with the same rule
/// (engine/src/suggest.ts); suggest-cases.json is the list both must match.
/// </summary>
public class SuggestCasesTests
{
    private static string CasesPath => Path.Combine(AppContext.BaseDirectory, "suggest-cases.json");
    private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(CasesPath)).RootElement;

    [Fact]
    public void The_paces_are_the_same_in_both_apps()
    {
        var c = Cases.GetProperty("constants");
        Assert.Equal(Suggest.ComfortPace, c.GetProperty("comfortPace").GetDouble());
        Assert.Equal(SpeedEstimate.ReadingPace, c.GetProperty("readingPace").GetDouble());
    }

    [Fact]
    public void The_same_models_are_usable()
    {
        foreach (var u in Cases.GetProperty("usable").EnumerateArray())
        {
            var origin = u.GetProperty("origin").GetString()!;
            Assert.True(u.GetProperty("usable").GetBoolean() == Suggest.Usable(origin, u.GetProperty("provider").GetString()!, u.GetProperty("category").GetString()!, u.GetProperty("moe").GetBoolean()), origin);
        }
    }

    [Fact]
    public void The_same_speed_estimate_as_zer0ne_code()
    {
        var s = Cases.GetProperty("speed");
        foreach (var c in s.GetProperty("gpuClass").EnumerateArray())
            Assert.True(c.GetProperty("gpuClass").GetString() == SpeedEstimate.GpuClass(c.GetProperty("name").GetString(), c.GetProperty("hasGpu").GetBoolean(), c.GetProperty("unified").GetBoolean()), c.GetProperty("name").GetString());
        foreach (var c in s.GetProperty("range").EnumerateArray())
        {
            var (low, high) = SpeedEstimate.Range(c.GetProperty("raw").GetDouble(), c.GetProperty("gpuClass").GetString()!, c.GetProperty("calibration").GetDouble());
            Assert.Equal(c.GetProperty("range")[0].GetDouble(), low);
            Assert.Equal(c.GetProperty("range")[1].GetDouble(), high);
        }
        foreach (var c in s.GetProperty("rawTps").EnumerateArray())
            Assert.Equal(c.GetProperty("rawTps").GetDouble(), SpeedEstimate.RawTps(c.GetProperty("gbPerSec").GetDouble(), c.GetProperty("sizeGb").GetDouble()));
        foreach (var c in s.GetProperty("calibration").EnumerateArray())
            Assert.Equal(c.GetProperty("calibration").GetDouble(), SpeedEstimate.Calibration(c.GetProperty("runs").EnumerateArray().Select(r => (r[0].GetDouble(), r[1].GetDouble()))));
    }

    [Fact]
    public void Llmfits_list_gives_the_same_rows_and_picks()
    {
        var l = Cases.GetProperty("llmfit");
        var models = l.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(l.GetProperty("gbPerSec").GetDouble(), SpeedEstimate.GbPerSecFromRows(models.Take(200)));
        var rows = Suggest.Rows(models, l.GetProperty("budgetGb").GetDouble(), l.GetProperty("gbPerSec").GetDouble(), l.GetProperty("gpuClass").GetString()!, l.GetProperty("calibration").GetDouble());
        Assert.Equal(l.GetProperty("rowCount").GetInt32(), rows.Count);
        var expected = l.GetProperty("picks").EnumerateArray().Select(p => $"{p[0].GetString()}:{p[1].GetString()}:{p[2].GetDouble()}:{p[3].GetDouble()}");
        Assert.Equal(expected, Suggest.Pick(rows).Select(p => $"{p.Kind}:{p.Model.Name}:{p.Model.TpsLow}:{p.Model.TpsHigh}"));
    }

    [Fact]
    public void The_same_models_are_suggested()
    {
        foreach (var p in Cases.GetProperty("picks").EnumerateArray())
        {
            var rows = p.GetProperty("rows").EnumerateArray().Select(r =>
            {
                var origin = r[0].GetString()!;
                double paramsB = r[1].GetDouble(), tps = r[3].GetDouble();
                return new SuggestCandidate("unsloth/" + origin[(origin.LastIndexOf('/') + 1)..] + "-GGUF", origin, paramsB, r[2].GetDouble(),
                    Math.Round(paramsB * 0.606 + 0.1, 1), Math.Round(paramsB * 0.64 + 1.6, 1), tps * 0.85, tps * 1.15, r[4].GetString()!, "2026-01-01");
            }).ToList();
            var expected = p.GetProperty("picks").EnumerateArray().Select(x => $"{x[0].GetString()}:{x[1].GetString()}");
            Assert.Equal(expected, Suggest.Pick(rows).Select(s => $"{s.Kind}:{s.Model.Name}"));
        }
    }
}
