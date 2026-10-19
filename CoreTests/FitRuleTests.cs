using System.Text.Json;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// One fit rule for two apps. fit-cases.json is written by zer0ne code's own fit
/// functions; this app's rule, written again in C#, must give the same answers.
/// </summary>
public class FitRuleTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(CasesPath)).RootElement;

    private static string CasesPath => Path.Combine(AppContext.BaseDirectory, "fit-cases.json");

    [Fact]
    public void The_numbers_are_the_same_in_both_apps()
    {
        var c = Cases.GetProperty("constants");
        Assert.Equal(FitRule.PerfectShare, c.GetProperty("perfect").GetDouble());
        Assert.Equal(FitRule.GoodShare, c.GetProperty("good").GetDouble());
        Assert.Equal(FitRule.TightShare, c.GetProperty("tight").GetDouble());
        Assert.Equal(FitRule.CeilingShare, c.GetProperty("ceiling").GetDouble());
        Assert.Equal(FitRule.RuntimeGb, c.GetProperty("runtimeGb").GetDouble());
        Assert.Equal(FitRule.LightKv, c.GetProperty("lightKv").GetDouble());
        Assert.Equal(FitRule.LongestContext, c.GetProperty("longestContext").GetInt32());
        Assert.Equal(RepoFiles.FloorGbPerB, c.GetProperty("floorGbPerB").GetDouble());
        Assert.Equal(RepoFiles.DraftMaxGb, c.GetProperty("draftMaxGb").GetDouble());
        Assert.Equal(FitRule.ReadingPace, c.GetProperty("readingPace").GetDouble());
        Assert.Equal(FitRule.SharedSpill, c.GetProperty("sharedSpill").GetDouble());
        Assert.Equal(SpeedEstimate.ReadingPace, FitRule.ReadingPace);
    }

    [Fact]
    public void Helper_files_are_told_by_the_same_names()
    {
        foreach (var h in Cases.GetProperty("helpers").EnumerateArray())
        {
            var path = h.GetProperty("path").GetString()!;
            Assert.True(h.GetProperty("helper").GetBoolean() == RepoFiles.IsHelperName(path), path);
        }
    }

    [Fact]
    public void The_same_files_are_left_as_the_model()
    {
        foreach (var m in Cases.GetProperty("models").EnumerateArray())
        {
            var files = m.GetProperty("files").EnumerateArray().Select(f => (Name: f[0].GetString()!, Gb: f[1].GetDouble())).ToList();
            var kept = RepoFiles.Models(files, f => f.Name, f => f.Gb, m.GetProperty("paramsB").GetDouble()).Select(f => f.Name);
            Assert.Equal(m.GetProperty("kept").EnumerateArray().Select(k => k.GetString()), kept);
        }
    }

    [Fact]
    public void Memory_gets_the_same_verdict_the_same_context_and_the_same_need()
    {
        foreach (var m in Cases.GetProperty("memory").EnumerateArray())
        {
            var name = m.GetProperty("name").GetString();
            var fit = FitRule.Rate(m.GetProperty("fileGb").GetDouble(), m.GetProperty("projectorGb").GetDouble(),
                m.GetProperty("kvBytesPerToken").GetDouble(), m.GetProperty("maxContext").GetInt32(),
                new FitRule.Limits(m.GetProperty("fastGb").GetDouble(), m.GetProperty("ceilingGb").GetDouble()));
            Assert.True(m.GetProperty("verdict").GetString() == fit.Verdict, $"{name}: {fit.Verdict}");
            Assert.True(m.GetProperty("context").GetInt32() == fit.Context, $"{name}: {fit.Context}");
            Assert.Equal(m.GetProperty("needGb").GetDouble(), fit.NeedGb, 3);
        }
    }

    [Fact]
    public void The_cache_is_sized_the_same_from_a_parameter_count()
    {
        foreach (var c in Cases.GetProperty("cache").EnumerateArray())
            Assert.Equal(c.GetProperty("bytesPerToken").GetDouble(),
                Math.Round(FitRule.KvFromParams(c.GetProperty("paramsB").GetDouble(), c.GetProperty("activeB").GetDouble())));
    }

    [Fact]
    public void The_same_version_is_recommended()
    {
        foreach (var c in Cases.GetProperty("picks").EnumerateArray())
        {
            var builds = c.GetProperty("builds").EnumerateArray()
                .Select(b => new FitRule.Build(b[0].GetString()!, b[1].GetDouble(), b[2].GetString()!, b[3].GetDouble(), b[4].GetDouble()))
                .ToList();
            var pick = FitRule.Pick(builds, c.GetProperty("pace").GetDouble());
            Assert.True(c.GetProperty("pick").GetString() == pick?.Quant, $"{c.GetProperty("name").GetString()}: {pick?.Quant}");
        }
    }

    [Fact]
    public void Both_copies_of_the_cases_are_the_same_file()
    {
        // the other copy sits in zer0ne code, when that project is next to this one
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "zer0ne_code"))) dir = dir.Parent;
        if (dir is null) return;
        var other = Path.Combine(dir.FullName, "zer0ne_code", "engine", "test", "fixtures", "fit-cases.json");
        if (!File.Exists(other)) return;
        Assert.Equal(File.ReadAllText(other).ReplaceLineEndings("\n"), File.ReadAllText(CasesPath).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(16, 33.4, 16, 26.72)]     // a laptop whose graphics chip shares memory
    [InlineData(24, 64, 24, 51.2)]        // a card with its own memory
    [InlineData(48, 32, 48, 48)]          // a card bigger than the RAM share
    [InlineData(0, 16, 7.68, 12.8)]       // no graphics memory
    public void A_PC_has_a_fast_limit_and_a_ceiling(double budget, double ram, double fast, double ceiling)
    {
        var l = FitRule.LimitsFor(budget, ram);
        Assert.Equal(fast, l.FastGb, 2);
        Assert.Equal(ceiling, l.CeilingGb, 2);
    }

    [Fact]
    public void Spilling_slows_only_what_spills()
    {
        var limits = new FitRule.Limits(16, 26.7);
        var slow = FitRule.Rate(16.948, 1.193, 245760, 32768, limits);
        Assert.Equal(FitRule.Slow, slow.Verdict);
        var f = FitRule.SpillFactor(slow, limits);
        Assert.InRange(f, 0.8, 0.9);                       // a card with its own memory: 16 of 19 GB stay fast
        // a chip that shares memory: measured, the processor runs it all
        Assert.Equal(FitRule.SharedSpill, FitRule.SpillFactor(slow, limits, 2, shared: true));
        Assert.Equal(1, FitRule.SpillFactor(FitRule.Rate(9.9, 0, 245760, 32768, limits), limits));
    }
}
