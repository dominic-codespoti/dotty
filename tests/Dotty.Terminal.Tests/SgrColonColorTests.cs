using System;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Extended colours in their colon (ITU T.416 / ISO 8613-6) sub-parameter forms: <c>38:5:n</c>, <c>38:2::r:g:b</c>,
/// <c>38:2:r:g:b</c> and <c>38:2:cs:r:g:b</c>, for foreground, background and underline colour. They must behave
/// exactly like the semicolon forms, colour-kind tags included, and a malformed one must set nothing and consume only
/// its own parameter.
/// </summary>
public sealed class SgrColonColorTests
{
    private static readonly TerminalPalette Palette = new();

    private static CellAttributes Sgr(string parameters, CellAttributes? start = null) =>
        SgrParserArgb.Apply(parameters.AsSpan(), start ?? CellAttributes.Default, Palette);

    [Theory]
    [InlineData("38:5:42", "38;5;42")]
    [InlineData("38:5:0", "38;5;0")]
    [InlineData("38:5:255", "38;5;255")]
    [InlineData("38:2::1:2:3", "38;2;1;2;3")]
    [InlineData("38:2:1:2:3", "38;2;1;2;3")]
    [InlineData("38:2:0:1:2:3", "38;2;1;2;3")]
    [InlineData("38:2::0:0:0", "38;2;0;0;0")]
    [InlineData("48:5:9", "48;5;9")]
    [InlineData("48:2::10:20:30", "48;2;10;20;30")]
    [InlineData("48:2:10:20:30", "48;2;10;20;30")]
    [InlineData("58:5:7", "58;5;7")]
    [InlineData("58:2::4:5:6", "58;2;4;5;6")]
    [InlineData("58:2:4:5:6", "58;2;4;5;6")]
    public void ColonFormsEqualTheSemicolonForms(string colon, string semicolon)
    {
        Assert.Equal(Sgr(semicolon), Sgr(colon));
    }

    [Fact]
    public void ColonPaletteForegroundIsIndexedWithItsIndex()
    {
        var a = Sgr("38:5:42");

        Assert.Equal(ColorKind.Indexed, a.ForegroundKind);
        Assert.Equal(42, a.ForegroundIndex);
        Assert.Equal(Palette[42], a.Foreground.Argb);
    }

    [Theory]
    [InlineData("38:2::1:2:3", 1, 2, 3)]
    [InlineData("38:2:200:100:50", 200, 100, 50)]
    [InlineData("38:2:7:255:0:128", 255, 0, 128)]     // colour-space slot filled in
    public void ColonTruecolorForegroundIsRgb(string sgr, int r, int g, int b)
    {
        var a = Sgr(sgr);

        Assert.Equal(ColorKind.Rgb, a.ForegroundKind);
        Assert.Equal(-1, a.ForegroundIndex);
        Assert.Equal(SgrColorArgb.FromRgb((byte)r, (byte)g, (byte)b), a.Foreground);
    }

    [Fact]
    public void ColonUnderlineColourCarriesItsKindAndIndex()
    {
        var indexed = Sgr("58:5:7");
        var rgb = Sgr("58:2::4:5:6");

        Assert.Equal((ColorKind.Indexed, 7), (indexed.UnderlineColorKind, indexed.UnderlineColorIndex));
        Assert.Equal(Palette[7], indexed.UnderlineColor.Argb);
        Assert.Equal((ColorKind.Rgb, -1), (rgb.UnderlineColorKind, rgb.UnderlineColorIndex));
        Assert.Equal(SgrColorArgb.FromRgb(4, 5, 6), rgb.UnderlineColor);
        Assert.Equal(ColorKind.Default, indexed.ForegroundKind);
    }

    [Fact]
    public void ColonBackgroundSetsOnlyTheBackground()
    {
        var a = Sgr("48:2::10:20:30");

        Assert.Equal(SgrColorArgb.FromRgb(10, 20, 30), a.Background);
        Assert.Equal(ColorKind.Default, a.ForegroundKind);
        Assert.Equal(ColorKind.Default, a.UnderlineColorKind);
    }

    [Fact]
    public void ARgbColonColourReplacesAnIndexedTag()
    {
        var a = Sgr("38:2::1:2:3", Sgr("38:5:42"));

        Assert.Equal((ColorKind.Rgb, -1), (a.ForegroundKind, a.ForegroundIndex));
        var back = Sgr("38:5:9", a);
        Assert.Equal((ColorKind.Indexed, 9), (back.ForegroundKind, back.ForegroundIndex));
    }

    [Fact]
    public void ParametersAroundAColonColourAreStillApplied()
    {
        var a = Sgr("1;38:5:42;4;48:2::1:2:3;58:5:7;3");

        Assert.True(a.Bold);
        Assert.True(a.Italic);
        Assert.Equal(UnderlineStyle.Single, a.UnderlineStyle);
        Assert.Equal(42, a.ForegroundIndex);
        Assert.Equal(SgrColorArgb.FromRgb(1, 2, 3), a.Background);
        Assert.Equal(7, a.UnderlineColorIndex);
    }

    [Fact]
    public void TheUnderlineStyleSubParameterStillWorksNextToColonColours()
    {
        var a = Sgr("4:3;38:5:1");

        Assert.Equal(UnderlineStyle.Curl, a.UnderlineStyle);
        Assert.Equal(1, a.ForegroundIndex);
    }

    [Theory]
    [InlineData("38:5")]             // no index
    [InlineData("38:5:256")]         // out of range
    [InlineData("38:5:300")]
    [InlineData("38:5:x")]           // not a number
    [InlineData("38:5:1:2")]         // too many for the palette form
    [InlineData("38:2:1:2")]         // too few
    [InlineData("38:2:1:2:300")]     // component out of range
    [InlineData("38:2:0:1:2:3:4")]   // too many
    [InlineData("38:9:1:2:3")]       // unknown colour mode
    [InlineData("38:")]
    [InlineData("38:2")]
    public void MalformedColonColoursSetNothingAndKeepTheRestOfTheList(string malformed)
    {
        var alone = Sgr(malformed);
        Assert.Equal(ColorKind.Default, alone.ForegroundKind);
        Assert.False(alone.Bold);

        var followed = Sgr(malformed + ";1");
        Assert.Equal(ColorKind.Default, followed.ForegroundKind);
        Assert.True(followed.Bold);

        var kept = Sgr(malformed, Sgr("38:5:3"));
        Assert.Equal((ColorKind.Indexed, 3), (kept.ForegroundKind, kept.ForegroundIndex));
    }

    [Fact]
    public void AnEmptyComponentReadsAsZero()
    {
        // As the empty colour-space slot of "38:2::r:g:b" does.
        Assert.Equal(0, Sgr("38:5:").ForegroundIndex);
        Assert.Equal(SgrColorArgb.FromRgb(0, 2, 3), Sgr("38:2::2:3").Foreground);
    }

    [Fact]
    public void AResetClearsColonColoursLikeAnyOther()
    {
        var a = Sgr("38:5:42;58:2::1:2:3");

        var reset = Sgr("39;59", a);
        Assert.Equal(ColorKind.Default, reset.ForegroundKind);
        Assert.Equal(ColorKind.Default, reset.UnderlineColorKind);
        Assert.Equal(CellAttributes.Default, Sgr("0", a));
    }

    [Fact]
    public void SemicolonFormsAreUnchanged()
    {
        var a = Sgr("38;5;42;48;2;1;2;3;58;5;7");

        Assert.Equal((ColorKind.Indexed, 42), (a.ForegroundKind, a.ForegroundIndex));
        Assert.Equal(SgrColorArgb.FromRgb(1, 2, 3), a.Background);
        Assert.Equal((ColorKind.Indexed, 7), (a.UnderlineColorKind, a.UnderlineColorIndex));
    }

    [Theory]
    [InlineData("4", UnderlineStyle.Single)]
    [InlineData("4:", UnderlineStyle.Single)]
    [InlineData("4:0", UnderlineStyle.None)]
    [InlineData("4:1", UnderlineStyle.Single)]
    [InlineData("4:2", UnderlineStyle.Double)]
    [InlineData("4:3", UnderlineStyle.Curl)]
    [InlineData("4:4", UnderlineStyle.Dotted)]
    [InlineData("4:5", UnderlineStyle.Dashed)]
    [InlineData("4:9", UnderlineStyle.Single)]
    public void TheUnderlineStyleSubParameterSelectsTheStyle(string sgr, UnderlineStyle style)
    {
        Assert.Equal(style, Sgr(sgr).UnderlineStyle);
    }

    [Fact]
    public void UnderlineStyleZeroTurnsAnActiveUnderlineOff()
    {
        var on = Sgr("4:3;58:5:7");

        var off = Sgr("4:0", on);

        Assert.Equal(UnderlineStyle.None, off.UnderlineStyle);
        Assert.False(off.Underline);
        Assert.Equal(7, off.UnderlineColorIndex);          // the colour is a separate attribute
    }

    [Fact]
    public void ColonStyleAndColonColoursMixWithSemicolonParameters()
    {
        var a = Sgr("1;4:3;58:2::255:0:0;38:5:9;48;5;2;3");

        Assert.True(a.Bold);
        Assert.True(a.Italic);
        Assert.Equal(UnderlineStyle.Curl, a.UnderlineStyle);
        Assert.Equal((ColorKind.Rgb, -1), (a.UnderlineColorKind, a.UnderlineColorIndex));
        Assert.Equal(SgrColorArgb.FromRgb(255, 0, 0), a.UnderlineColor);
        Assert.Equal((ColorKind.Indexed, 9), (a.ForegroundKind, a.ForegroundIndex));
        Assert.Equal(Sgr("48;5;2").Background, a.Background);
    }

    [Fact]
    public void ASemicolonColourBeforeAColonColourConsumesOnlyItsOwnParameters()
    {
        var a = Sgr("38;5;1;48:5:2;1");

        Assert.Equal((ColorKind.Indexed, 1), (a.ForegroundKind, a.ForegroundIndex));
        Assert.Equal(Sgr("48;5;2").Background, a.Background);
        Assert.True(a.Bold);
    }

    [Fact]
    public void AMalformedColonColourDoesNotSwallowAFollowingColonColour()
    {
        var a = Sgr("38:5:300;48:2::1:2:3;4:2");

        Assert.Equal(ColorKind.Default, a.ForegroundKind);
        Assert.Equal(SgrColorArgb.FromRgb(1, 2, 3), a.Background);
        Assert.Equal(UnderlineStyle.Double, a.UnderlineStyle);
    }
}
