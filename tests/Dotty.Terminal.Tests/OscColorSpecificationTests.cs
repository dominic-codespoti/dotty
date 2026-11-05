using System.Globalization;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class OscColorSpecificationTests
{
    [Theory]
    [InlineData("#fff", 0xfff0f0f0u)]
    [InlineData("#888", 0xff808080u)]
    [InlineData("#f000f000f000", 0xfff0f0f0u)]
    [InlineData("#800080008000", 0xff808080u)]
    [InlineData("#123456789", 0xff124578u)]
    [InlineData("rgb:1/8/ff", 0xff1188ffu)]
    [InlineData("rgb:01/80/ffff", 0xff0180ffu)]
    [InlineData("rgb:ffff/0000/8080", 0xffff0080u)]
    public void ParsesX11HexadecimalForms(string input, uint expected)
    {
        Assert.True(OscColorParser.TryParse(input, out uint actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("rgbi:0.5/0.5/0.5", 0xffc1bbbbu)]
    [InlineData("CIELab:1/1/1", 0xff6c6767u)]
    [InlineData("CIELab:25/-40/60", 0xff004a68u)]
    [InlineData("CIELab:50/1/1", 0xffef1352u)]
    [InlineData("CIELuv:1/1/1", 0xff16140eu)]
    [InlineData("CIEXYZ:1/1/1", 0xffffffffu)]
    [InlineData("CIEXYZ:0.5/0.5/0.5", 0xffddb5a0u)]
    [InlineData("CIExyY:1/1/1", 0xffffffffu)]
    [InlineData("CIExyY:0.5/0.5/0.5", 0xfff7b30eu)]
    [InlineData("CIEuvY:1/1/1", 0xffffffffu)]
    [InlineData("CIEuvY:0.5/0.5/0.5", 0xffffa3aeu)]
    [InlineData("TekHVC:1/1/1", 0xff1a130fu)]
    [InlineData("TekHVC:0/50/100", 0xffec1368u)]
    [InlineData("TekHVC:360/1/1", 0xff1a130fu)]
    public void ParsesColorimetricForms(string input, uint expected)
    {
        Assert.True(OscColorParser.TryParse(input, out uint actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#ggg")]
    [InlineData("#12345")]
    [InlineData("rgb:/1/1")]
    [InlineData("rgb:1/2/3/4")]
    [InlineData("rgb:12345/0/0")]
    [InlineData("rgbi:NaN/0/0")]
    [InlineData("rgbi:Infinity/0/0")]
    [InlineData("rgbi:-0.1/0/0")]
    [InlineData("rgbi:0/0/1.1")]
    [InlineData("CIEXYZ:0/1.1/0")]
    [InlineData("CIELab:101/0/0")]
    [InlineData("CIExyY:1.1/0.5/0.5")]
    [InlineData("CIEuvY:0/0/2")]
    [InlineData("TekHVC:120/101/10")]
    [InlineData("TekHVC:20/50/-1")]
    public void RejectsMalformedAndOutOfDomainForms(string input)
    {
        Assert.False(OscColorParser.TryParse(input, out uint color));
        Assert.Equal(0u, color);
    }

    [Fact]
    public void ParsesFloatingPointIndependentOfCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(OscColorParser.TryParse("CIELab:1.0/1.0/1.0", out uint color));
            Assert.Equal(0xff6c6767u, color);
            Assert.False(OscColorParser.TryParse("CIELab:1,0/1,0/1,0", out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
