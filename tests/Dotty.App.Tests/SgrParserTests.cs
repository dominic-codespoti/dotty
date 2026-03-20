using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class SgrParserTests
{
    [Fact]
    public void EmptyParameters_ResetToDefault()
    {
        var current = new CellAttributes { Bold = true, Foreground = new SgrColor("#FFFFFF") };
        var updated = SgrParser.Apply(ReadOnlySpan<char>.Empty, current);
        Assert.Equal(CellAttributes.Default, updated);
    }

    [Fact]
    public void ExtendedAttributes_SetAndClear()
    {
        var updated = SgrParser.Apply("5;8;9;21;53".AsSpan(), CellAttributes.Default);
        Assert.True(updated.SlowBlink);
        Assert.True(updated.Invisible);
        Assert.True(updated.Strikethrough);
        Assert.True(updated.DoubleUnderline);
        Assert.True(updated.Overline);

        var cleared = SgrParser.Apply("24;25;28;29;55".AsSpan(), updated);
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
        var updated = SgrParser.Apply("1;3;4;7".AsSpan(), CellAttributes.Default);
        Assert.True(updated.Bold);
        Assert.True(updated.Italic);
        Assert.True(updated.Underline);
        Assert.True(updated.Inverse);

        var cleared = SgrParser.Apply("22;23;24;27".AsSpan(), updated);
        Assert.False(cleared.Bold);
        Assert.False(cleared.Italic);
        Assert.False(cleared.Underline);
        Assert.False(cleared.Inverse);
    }

    [Fact]
    public void StandardColors_Applied()
    {
        var updated = SgrParser.Apply("31;44".AsSpan(), CellAttributes.Default);
        Assert.Equal("#AA0000", updated.Foreground?.Hex);
        Assert.Equal("#0000AA", updated.Background?.Hex); // 44 -> 34 -> blue background
    }

    [Fact]
    public void ExtendedTrueColor_Applied()
    {
        var updated = SgrParser.Apply("38;2;10;20;30;48;2;1;2;3".AsSpan(), CellAttributes.Default);
        Assert.Equal("#0A141E", updated.Foreground?.Hex);
        Assert.Equal("#010203", updated.Background?.Hex);
    }

    [Fact]
    public void Extended256Color_Applied()
    {
        var updated = SgrParser.Apply("38;5;196;48;5;123;58;5;45".AsSpan(), CellAttributes.Default);
        Assert.Equal("#FF0000", updated.Foreground?.Hex); // idx 196
        Assert.Equal("#87FFFF", updated.Background?.Hex); // idx 123
        Assert.Equal("#00D7FF", updated.UnderlineColor?.Hex); // idx 45
    }

    [Fact]
    public void ResetCodes_ClearColors()
    {
        var current = new CellAttributes
        {
            Foreground = new SgrColor("#111111"),
            Background = new SgrColor("#222222"),
            UnderlineColor = new SgrColor("#333333"),
        };

        var updated = SgrParser.Apply("39;49;59".AsSpan(), current);
        Assert.Null(updated.Foreground);
        Assert.Null(updated.Background);
        Assert.Null(updated.UnderlineColor);
    }
}
