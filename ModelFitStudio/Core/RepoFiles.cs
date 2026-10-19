using System.Text.RegularExpressions;

namespace ModelFitStudio.Core;

/// <summary>
/// Which GGUF files of a repo are the model, and which only help one. A repo often
/// carries small files next to the weights: a vision projector (mmproj), a draft head
/// that speeds the model up (MTP), an importance matrix, a bare vocabulary. They load
/// in llama.cpp only beside the real model, so offering one as "the file to download"
/// gives a 0.5 GB answer for a 26B model. A full build can carry its draft layers
/// inside and say "MTP" in its name (a 17 GB Qwen3.8-27B): a draft name makes a helper
/// only when the file is too small to be the model.
///
/// zer0ne code applies the same rule; fit-cases.json holds both apps to it.
/// </summary>
public static class RepoFiles
{
    // the name says so; the same words zer0ne code looks for (engine/src/repoFiles.ts)
    private static readonly Regex Helper = new(
        @"mmproj|(^|/)imatrix[^/]*$|(^|[-_./])(mtp|draft|eagle)([-_./]|$)|(^|/)ggml-vocab[-_.]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Bytes per parameter no real version goes below, in GB per billion. The smallest
    /// quants llama.cpp makes are about 1.6 bits a weight (0.2); half of that leaves
    /// room for a count that is a little off.
    /// </summary>
    public const double FloorGbPerB = 0.1;

    /// <summary>Without a parameter count, a draft-named file this large (GB) is a build: draft heads are 0.1–1 GB.</summary>
    public const double DraftMaxGb = 3;

    private static readonly Regex Side = new(@"mmproj|(^|/)imatrix[^/]*$|(^|/)ggml-vocab[-_.]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NamedMoe = new(@"(\d+)x(\d+(?:\.\d+)?)b\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NamedCount = new(@"(?:^|[-_.])(\d+(?:\.\d+)?)([bm])(?=$|[-_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"7B", "0.6b", "8x7B" in a file name → billions of parameters (zer0ne code's paramsFromName).</summary>
    public static double? ParamsFromName(string path)
    {
        var name = path.Replace('\\', '/').Split('/')[^1];
        var moe = NamedMoe.Match(name);
        if (moe.Success) return double.Parse(moe.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * double.Parse(moe.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var m = NamedCount.Match(name);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("b", StringComparison.OrdinalIgnoreCase) ? n : n / 1000;
    }

    /// <summary>
    /// A helper, knowing the file's size: projectors, matrices and vocabularies always; a
    /// draft-named file only when it is smaller than a build of the model could be (the
    /// parameter count, or the count in the name, at <see cref="FloorGbPerB"/>;
    /// <see cref="DraftMaxGb"/> when neither is known).
    /// </summary>
    public static bool IsHelper(string? path, double sizeGb, double paramsB = 0)
    {
        if (!IsHelperName(path)) return false;
        var p = path!.Replace('\\', '/');
        if (Side.IsMatch(p)) return true;
        var b = paramsB > 0 ? paramsB : ParamsFromName(p) ?? 0;
        return sizeGb < (b > 0 ? b * FloorGbPerB : DraftMaxGb);
    }

    private static readonly Regex DraftHead = new(@"(^|[-_./])mtp([-_./]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the path names a draft head ("mtp-…"): a small network that guesses the
    /// next few tokens for the model to confirm, so the model writes faster. llama.cpp
    /// loads it beside the model with --spec-type draft-mtp --spec-draft-model.
    /// </summary>
    public static bool IsDraftHeadName(string? path) =>
        !string.IsNullOrEmpty(path) && DraftHead.IsMatch(path.Replace('\\', '/')) &&
        !path.Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the path names a helper file. Without a size this is the whole answer.</summary>
    public static bool IsHelperName(string? path) =>
        !string.IsNullOrEmpty(path) && Helper.IsMatch(path.Replace('\\', '/'));

    /// <summary>
    /// The files that are the model itself: no helper, and none far smaller than the
    /// parameter count allows. When the count is unknown, or it would leave nothing
    /// (an adapter repo, a wrong count), size does not drop plain-named files.
    /// </summary>
    public static List<T> Models<T>(IEnumerable<T> files, Func<T, string> name, Func<T, double> sizeGb, double paramsB)
    {
        var named = files.Where(f => !IsHelper(name(f), sizeGb(f), paramsB)).ToList();
        if (paramsB <= 0) return named;
        var sized = named.Where(f => sizeGb(f) >= paramsB * FloorGbPerB).ToList();
        return sized.Count > 0 ? sized : named;
    }
}
