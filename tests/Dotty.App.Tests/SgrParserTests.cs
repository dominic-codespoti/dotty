using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class SgrParserTests
{
    [Fact]
    public void EmptyParameters_ResetToDefault()
    {
        var current = new CellAttributes { Bold = true, Foreground = SgrColorArgb.FromAnsiCode(37) };
        var updated = SgrParserArgb.Apply(ReadOnlySpan<char>.Empty, current);
        Assert.Equal(CellAttributes.Default, updated);
    }

    [Fact]
    public void ExtendedAttributes_SetAndClear()
    {
        var updated = SgrParserArgb.Apply("5;8;9;21;53".AsSpan(), CellAttributes.Default);
        Assert.True(updated.SlowBlink);
        Assert.True(updated.Invisible);
        Assert.True(updated.Strikethrough);
        Assert.True(updated.DoubleUnderline);
        Assert.True(updated.Overline);

        var cleared = SgrParserArgb.Apply("24;25;28;29;55".AsSpan(), updated);
        Assert.False(cleared.Underline); // 24 clears Underline and DoubleUnderline
        Assert.False(cleared.DoubleUnderline);
        Assert.False(cleared.SlowBlink);
        Assert.False(cleared.Invisible);
        Assert.False(cleared.Strikethrough);
        Assert.False(cleared.Overline);
    }

    [Fact]
    public void BasicAttributes_SetAndClear()
    {
        var updated = SgrParserArgb.Apply("1;3;4;7".AsSpan(), CellAttributes.Default);
        Assert.True(updated.Bold);
        Assert.True(updated.Italic);
        Assert.True(updated.Underline);
        Assert.True(updated.Inverse);

        var cleared = SgrParserArgb.Apply("22;23;24;27".AsSpan(), updated);
        Assert.False(cleared.Bold);
        Assert.False(cleared.Italic);
        Assert.False(cleared.Underline);
        Assert.False(cleared.Inverse);
    }

    [Fact]
    public void StandardColors_Applied()
    {
        var updated = SgrParserArgb.Apply("31;44".AsSpan(), CellAttributes.Default);
        Assert.Equal(0xFFAA0000u, updated.Foreground.Argb); // Red
        Assert.Equal(0xFF0000AAu, updated.Background.Argb); // 44 -> 34 -> blue background
    }

    [Fact]
    public void ExtendedTrueColor_Applied()
    {
        var updated = SgrParserArgb.Apply("38;2;10;20;30;48;2;1;2;3".AsSpan(), CellAttributes.Default);
        Assert.Equal(0xFF0A141Eu, updated.Foreground.Argb);
        Assert.Equal(0xFF010203u, updated.Background.Argb);
    }

    [Fact]
    public void Extended256Color_Applied()
    {
        var updated = SgrParserArgb.Apply("38;5;196;48;5;123;58;5;45".AsSpan(), CellAttributes.Default);
        Assert.Equal(0xFFFF0000u, updated.Foreground.Argb); // idx 196
        Assert.Equal(0xFF87FFFFu, updated.Background.Argb); // idx 123
        Assert.Equal(0xFF00D7FFu, updated.UnderlineColor.Argb); // idx 45
    }

    [Fact]
    public void ResetCodes_ClearColors()
    {
        var current = new CellAttributes
        {
            Foreground = SgrColorArgb.FromRgb(0x11, 0x11, 0x11),
            Background = SgrColorArgb.FromRgb(0x22, 0x22, 0x22),
            UnderlineColor = SgrColorArgb.FromRgb(0x33, 0x33, 0x33),
        };

        var updated = SgrParserArgb.Apply("39;49;59".AsSpan(), current);
        Assert.True(updated.Foreground.IsEmpty);
        Assert.True(updated.Background.IsEmpty);
        Assert.True(updated.UnderlineColor.IsEmpty);
    }
}
