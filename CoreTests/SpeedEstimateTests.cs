using CoreTests.TestOnly;
using ModelFitStudio.Core;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests;

/// <summary>
/// The speed the app shows is a range corrected for the kind of GPU. The one
/// measured case is pinned here: an Arc 140T laptop where the raw estimate said
/// about 15 tok/s and a real run gave 7–10.
/// </summary>
public class SpeedEstimateTests
{
    private readonly ITestOutputHelper _log;
    private const string T = nameof(SpeedEstimateTests);

    public SpeedEstimateTests(ITestOutputHelper log) => _log = log;

    [Theory]
    [InlineData("Intel(R) Arc(TM) 140T GPU (16GB)", SpeedEstimate.Integrated)]
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)", SpeedEstimate.Integrated)]
    [InlineData("Intel(R) Arc(TM) Graphics", SpeedEstimate.Integrated)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", SpeedEstimate.Integrated)]
    [InlineData("AMD Radeon(TM) 890M Graphics", SpeedEstimate.Integrated)]
    [InlineData("AMD Radeon(TM) Graphics", SpeedEstimate.Integrated)]
    [InlineData("AMD Radeon(TM) 8060S Graphics", SpeedEstimate.Integrated)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", SpeedEstimate.Discrete)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", SpeedEstimate.Discrete)]
    [InlineData("NVIDIA GeForce RTX 4090", SpeedEstimate.Discrete)]
    [InlineData("AMD Radeon RX 7900 XTX", SpeedEstimate.Discrete)]
    public void GpuClass_Tells_A_Shared_Memory_GPU_From_A_Card(string name, string expected)
    {
        var observed = SpeedEstimate.GpuClass(name, hasGpu: true);
        Obs.Log($"{T}.GpuClass", $"{name} -> {observed}");
        _log.WriteLine($"{name} -> {observed}");
        Assert.Equal(expected, observed);
    }

    [Fact]
    public void No_GPU_Is_Cpu_Whatever_The_Name_Says()
    {
        Assert.Equal(SpeedEstimate.Cpu, SpeedEstimate.GpuClass("No GPU detected", hasGpu: false));
    }

    [Fact]
    public void The_Measured_Laptop_Case_Lands_In_The_Measured_Range()
    {
        // the app said "about 15 tok/s"; the real run was 7–10
        var (low, high) = SpeedEstimate.Range(15, SpeedEstimate.Integrated);
        var observed = $"raw 15 -> {low}–{high} tok/s";
        Obs.Log($"{T}.MeasuredCase", observed);
        _log.WriteLine(observed);

        Assert.InRange(low, 6.5, 7.5);
        Assert.InRange(high, 9.5, 10.5);
    }

    [Theory]
    [InlineData(SpeedEstimate.Integrated)]
    [InlineData(SpeedEstimate.Discrete)]
    [InlineData(SpeedEstimate.Cpu)]
    public void A_Range_Never_Promises_More_Than_The_Raw_Estimate(string gpuClass)
    {
        var (low, high) = SpeedEstimate.Range(40, gpuClass);
        Assert.True(low > 0 && low < high, $"{gpuClass}: {low}–{high} is not a range");
        Assert.True(high <= 40, $"{gpuClass}: the top of the range beats the raw estimate");
        Assert.InRange(SpeedEstimate.Typical(low, high), low, high);
    }

    [Fact]
    public void A_Real_Run_Replaces_The_Guess_And_Narrows_The_Range()
    {
        // raw said 16, the machine did 8: every other estimate is halved, give or take 15%
        var calibration = SpeedEstimate.Calibration([(8.0, 16.0)]);
        var guess = SpeedEstimate.Range(30, SpeedEstimate.Discrete);
        var (low, high) = SpeedEstimate.Range(30, SpeedEstimate.Discrete, calibration);
        var observed = $"calibration {calibration}: raw 30 -> {low}–{high} (uncalibrated {guess.Low}–{guess.High})";
        Obs.Log($"{T}.Calibrated", observed);
        _log.WriteLine(observed);

        Assert.Equal(0.5, calibration, 3);
        Assert.InRange(15.0, low, high);
        Assert.True(high - low < guess.High - guess.Low, "a measured machine gets a narrower range than a guessed one");
    }

    [Fact]
    public void Calibration_Is_The_Median_And_Ignores_Runs_That_Cannot_Be_Right()
    {
        // one broken run (a ratio of 20) and one without a raw estimate must not bend the result
        var calibration = SpeedEstimate.Calibration([(8, 16), (9, 15), (7, 14), (400, 20), (10, 0)]);
        Assert.Equal(0.5, calibration, 3);
        Assert.Equal(0, SpeedEstimate.Calibration([]));
        Assert.Equal(0, SpeedEstimate.Calibration([(5, 0)]));
    }

    [Fact]
    public void No_Estimate_Gives_No_Range()
    {
        Assert.Equal((0, 0), SpeedEstimate.Range(0, SpeedEstimate.Discrete));
    }
}
