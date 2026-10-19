using System.Text.Json.Nodes;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>The shared "Best for agents" list: what a result holds, the page it opens, matching chips, the median.</summary>
public class AgentShareTests
{
    private static JsonObject Sample(string gpu = "Intel(R) Arc(TM) 140T GPU (16GB)", string file = "Qwen3-1.7B-Q4_K_M.gguf", int points = 71) =>
        AgentShare.Payload("3.16.0", gpu, "integrated", 31.4, "unsloth/Qwen3-1.7B-GGUF", file, 1.03, 26, 40.3, 7, 10, points, false, "b11574");

    [Fact]
    public void A_result_holds_the_machine_in_general_terms_and_the_scores_only()
    {
        var p = Sample();
        Assert.Equal(31, p["ramGb"]!.GetValue<double>());
        Assert.Equal(71, p["points"]!.GetValue<int>());
        var text = p.ToJsonString();
        Assert.DoesNotContain("Users", text);        // no paths
        Assert.DoesNotContain("sk-local", text);     // no key
    }

    [Fact]
    public void The_page_is_a_new_issue_on_the_apps_repository_with_the_json()
    {
        var url = AgentShare.IssueUrl(Sample());
        Assert.StartsWith($"https://github.com/{Updates.AppRepo}/issues/new?title=", url);
        var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=") + 6)..]);
        Assert.Contains("```json", body);
        Assert.Contains("\"points\": 71", body);
        Assert.StartsWith(AgentShare.IssuePrefix, Uri.UnescapeDataString(url[(url.IndexOf("title=") + 6)..url.IndexOf("&body=")]));
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) 140T GPU (16GB)", "arc 140t")]
    [InlineData("NVIDIA GeForce RTX 4090", "rtx 4090")]
    [InlineData("AMD Radeon(TM) 780M Graphics", "780m")]
    public void A_chip_is_matched_without_brand_noise(string name, string key)
    {
        Assert.Equal(key, AgentShare.GpuKey(name));
    }

    [Fact]
    public void Results_on_the_same_chip_are_ranked_by_their_median()
    {
        var board = new JsonArray(
            Sample(points: 71), Sample(points: 69), Sample(points: 10),                      // one odd run does not decide
            Sample(file: "Qwen3-4B-Q4_K_M.gguf", points: 80),
            Sample(gpu: "NVIDIA GeForce RTX 4090", file: "big.gguf", points: 99));           // another chip
        var rows = AgentShare.ForGpu(board, "intel arc 140t");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Qwen3-4B-Q4_K_M.gguf", rows[0].File);
        Assert.Equal(69, rows[1].Points);
        Assert.Equal(3, rows[1].Results);
        Assert.Empty(AgentShare.ForGpu(null, "x"));
    }

    [Theory]
    [InlineData("v3.17.0", "3.16.0", true)]
    [InlineData("3.16.0", "3.16.0", false)]
    [InlineData("v3.15.9", "3.16.0", false)]
    [InlineData("nonsense", "3.16.0", false)]
    public void A_newer_release_is_told_by_its_version(string tag, string current, bool newer)
    {
        Assert.Equal(newer, Updates.IsNewer(tag, current));
    }
}
