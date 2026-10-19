using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>
/// Which files of a repo are the model. The case that started it: a 26B model whose
/// repo also holds a 0.46 GB draft head, which was offered as the file to download.
/// </summary>
public class RepoFilesTests
{
    [Theory]
    [InlineData("MTP/mtp-gemma-4-26B-A4B-it-Q8_0.gguf", true)]
    [InlineData("mtp-gemma-4-26B-A4B-it.gguf", true)]
    [InlineData("MTP_mtp-gemma-4-26B-A4B-it-Q8_0.gguf", true)]     // as the library stores it
    [InlineData("mmproj-BF16.gguf", true)]
    [InlineData("gemma-3-4b-it-mmproj-f16.gguf", true)]
    [InlineData("imatrix.gguf", true)]
    [InlineData("ggml-vocab-llama.gguf", true)]
    [InlineData("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", false)]
    [InlineData("BF16/gemma-4-26B-A4B-it-BF16-00001-of-00002.gguf", false)]
    [InlineData("Qwen3-0.6B-Q4_K_M.gguf", false)]
    [InlineData("llama-3-8b.imatrix.Q4_K_M.gguf", false)]          // made with a matrix: still the model
    [InlineData("Smtp-Helper-7B-Q4_K_M.gguf", false)]              // "mtp" inside a word
    public void A_helper_is_known_by_its_name(string path, bool helper)
    {
        Assert.Equal(helper, RepoFiles.IsHelperName(path));
    }

    [Theory]
    [InlineData("mtp-gemma-4-E4B-it.gguf", true)]
    [InlineData("MTP/mtp-gemma-4-26B-A4B-it-Q8_0.gguf", true)]
    [InlineData("model.mtp.gguf", true)]
    [InlineData("mmproj-mtp-F16.gguf", false)]                     // a projector, whatever else its name says
    [InlineData("imatrix.gguf", false)]                            // a helper, but not a draft head
    [InlineData("Qwen3.8-27B-MTPish-Q4_K_M.gguf", false)]
    [InlineData("gemma-4-E4B-it-Q4_K_M.gguf", false)]
    public void A_draft_head_is_known_by_its_name(string path, bool head)
    {
        Assert.Equal(head, RepoFiles.IsDraftHeadName(path));
        if (head) Assert.True(RepoFiles.IsHelperName(path), "a draft head is never offered as the model");
    }

    private static readonly (string Name, double Gb)[] Gemma =
    [
        ("MTP/mtp-gemma-4-26B-A4B-it-Q8_0.gguf", 0.462),
        ("mtp-gemma-4-26B-A4B-it.gguf", 0.462),
        ("mmproj-BF16.gguf", 1.195),
        ("gemma-4-26B-A4B-it-UD-IQ2_XXS.gguf", 9.922),
        ("gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", 16.948),
        ("gemma-4-26B-A4B-it-Q8_0.gguf", 26.86),
    ];

    [Fact]
    public void Only_the_weights_are_left()
    {
        var models = RepoFiles.Models(Gemma, f => f.Name, f => f.Gb, 25.2);
        Assert.Equal(3, models.Count);
        Assert.All(models, f => Assert.True(f.Gb > 9));
    }

    [Fact]
    public void A_small_file_with_a_plain_name_is_dropped_by_its_size()
    {
        // a draft head under a name nobody thought of: far too small for 26 billion parameters
        var files = Gemma.Append(("gemma-4-26B-A4B-it-assistant-Q8_0.gguf", 0.46)).ToArray();
        var models = RepoFiles.Models(files, f => f.Item1, f => f.Item2, 25.2);
        Assert.DoesNotContain(models, f => f.Item1.Contains("assistant"));
    }

    [Theory]
    [InlineData("Qwen3.8-27B-iMatrix-NVFP4-MTP.gguf", 17.1, false)]   // a full build with its draft layers inside
    [InlineData("Qwen3.5-2B-MTP-Q4_K_M.gguf", 1.28, false)]
    [InlineData("mtp-gemma-4-26B-A4B-it.gguf", 0.46, true)]            // a head: far below a 26B build
    [InlineData("mtp-gemma-4-E4B-it.gguf", 0.1, true)]                 // no count in the name: under 3 GB
    [InlineData("mmproj-Qwen3.8-27B-F16.gguf", 0.93, true)]
    public void A_draft_name_on_a_full_build_does_not_make_it_a_helper(string path, double gb, bool helper)
    {
        Assert.Equal(helper, RepoFiles.IsHelper(path, gb));
    }

    [Fact]
    public void An_unknown_or_wrong_count_drops_nothing()
    {
        (string Name, double Gb)[] small = [("Qwen3-0.6B-Q4_K_M.gguf", 0.378), ("Qwen3-0.6B-Q8_0.gguf", 0.64)];
        Assert.Equal(2, RepoFiles.Models(small, f => f.Name, f => f.Gb, 0).Count);
        // a name that claims 70B over adapter-sized files: size would leave nothing, so it is not used
        Assert.Equal(2, RepoFiles.Models(small, f => f.Name, f => f.Gb, 70).Count);
    }
}
