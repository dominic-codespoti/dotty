using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class SgrColorTests
{
    [Theory]
    [InlineData(30, "#000000")]
    [InlineData(31, "#AA0000")]
    [InlineData(37, "#AAAAAA")]
    [InlineData(90, "#555555")]
    [InlineData(97, "#FFFFFF")]
    public void TryFromAnsiCode_MapsStandardCodes(int code, string expected)
    {
        var ok = SgrColor.TryFromAnsiCode(code, out var color);
        Assert.True(ok);
        Assert.Equal(expected, color.Hex);
    }

    [Theory]
    [InlineData(40, "#000000")]
    [InlineData(41, "#AA0000")]
    [InlineData(47, "#AAAAAA")]
    [InlineData(100, "#555555")]
    [InlineData(107, "#FFFFFF")]
    public void TryFromBackgroundCode_MapsBackgroundOffsets(int code, string expected)
    {
        var ok = SgrColor.TryFromBackgroundCode(code, out var color);
        Assert.True(ok);
        Assert.Equal(expected, color.Hex);
    }

    [Theory]
    [InlineData(0, "#000000")]
    [InlineData(5, "#AA00AA")]
    [InlineData(16, "#000000")]
    [InlineData(45, "#00D7FF")]
    [InlineData(123, "#87FFFF")]
    [InlineData(196, "#FF0000")]
    public void TryFrom256_MapsIndexedPalette(int idx, string expected)
    {
        var ok = SgrColor.TryFrom256(idx, out var color);
        Assert.True(ok);
        Assert.Equal(expected, color.Hex);
    }

    [Fact]
    public void FromRgb_BuildsHex()
    {
        var c = SgrColor.FromRgb(0x0A, 0x14, 0x1E);
        Assert.Equal("#0A141E", c.Hex);
    }
}
