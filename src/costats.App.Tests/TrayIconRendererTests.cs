using costats.App.Services;
using Xunit;

namespace costats.App.Tests;

public class TrayIconRendererTests
{
    // --- CalcPct ---

    [Fact]
    public void CalcPct_NullLimit_ReturnsZero()
    {
        var result = TrayHost.CalcPct(used: 500, limit: null);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalcPct_ZeroLimit_ReturnsZero()
    {
        var result = TrayHost.CalcPct(used: 500, limit: 0);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalcPct_NullUsed_ReturnsZero()
    {
        var result = TrayHost.CalcPct(used: null, limit: 1000);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void CalcPct_HalfUsed_Returns50()
    {
        var result = TrayHost.CalcPct(used: 500, limit: 1000);
        Assert.Equal(50.0, result, precision: 5);
    }

    [Fact]
    public void CalcPct_FullyUsed_Returns100()
    {
        var result = TrayHost.CalcPct(used: 1000, limit: 1000);
        Assert.Equal(100.0, result, precision: 5);
    }

    [Fact]
    public void CalcPct_OverQuota_ClampedTo100()
    {
        var result = TrayHost.CalcPct(used: 1500, limit: 1000);
        Assert.Equal(100.0, result, precision: 5);
    }

    [Fact]
    public void CalcPct_PercentageMode_Limit100()
    {
        // When limit == 100, used is already a percentage (CLI probe style)
        var result = TrayHost.CalcPct(used: 73, limit: 100);
        Assert.Equal(73.0, result, precision: 5);
    }
}
