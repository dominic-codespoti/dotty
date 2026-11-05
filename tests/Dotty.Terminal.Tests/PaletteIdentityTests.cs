using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PaletteIdentityTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) =>
        parser.Feed(Encoding.UTF8.GetBytes(text));

    private static CellAttributes StyleAt(TerminalAdapter adapter, int cell)
    {
        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        return snapshot.GetStyle(snapshot.Cells[cell].StyleId);
    }

    [Fact]
    public void PrintedIndexedColorsFollowPaletteMutationButEqualRgbDoesNot()
    {
        var (adapter, parser) = Setup();
        uint palette200 = new TerminalPalette()[200];
        string rgb = $"{(palette200 >> 16) & 255};{(palette200 >> 8) & 255};{palette200 & 255}";
        Feed(parser, $"\u001b[38;5;200;48;5;200;58;5;200mA\u001b[38;2;{rgb};48;2;{rgb};58;2;{rgb}mB");

        Feed(parser, "\u001b]4;200;#123456\u0007");

        CellAttributes indexed = StyleAt(adapter, 0);
        CellAttributes explicitRgb = StyleAt(adapter, 1);
        Assert.Equal(200, indexed.ForegroundIndex);
        Assert.Equal(200, indexed.BackgroundIndex);
        Assert.Equal(200, indexed.UnderlineColorIndex);
        Assert.Equal(0xFF123456u, indexed.Foreground.Argb);
        Assert.Equal(0xFF123456u, indexed.Background.Argb);
        Assert.Equal(0xFF123456u, indexed.UnderlineColor.Argb);
        Assert.Equal(palette200, explicitRgb.Foreground.Argb);
        Assert.Equal(palette200, explicitRgb.Background.Argb);
        Assert.Equal(palette200, explicitRgb.UnderlineColor.Argb);
        Assert.Equal(ColorKind.Rgb, explicitRgb.ForegroundKind);
        Assert.Equal(ColorKind.Rgb, explicitRgb.BackgroundKind);
        Assert.Equal(ColorKind.Rgb, explicitRgb.UnderlineColorKind);
    }

    [Fact]
    public void EqualPaletteValuesAtDifferentIndicesRemainIndependent()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "\u001b]4;1;#010203;2;#010203\u0007\u001b[38;2;1;2;3;48;5;1mA\u001b[38;2;1;2;3;48;5;2mB");

        Feed(parser, "\u001b]4;1;#a0b0c0\u0007");

        Assert.Equal(0xFF010203u, StyleAt(adapter, 0).Foreground.Argb);
        Assert.Equal(StyleAt(adapter, 0).Foreground, StyleAt(adapter, 1).Foreground);
        Assert.Equal(0xFFA0B0C0u, StyleAt(adapter, 0).Background.Argb);
        Assert.Equal(1, StyleAt(adapter, 0).BackgroundIndex);
        Assert.Equal(0xFF010203u, StyleAt(adapter, 1).Background.Argb);
        Assert.Equal(2, StyleAt(adapter, 1).BackgroundIndex);
    }

    [Fact]
    public void ActiveAndSavedIndexedRenditionsRemainPaletteRelativeAcrossMutationAndReset()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "\u001b[38;5;200m\u001b[sA\u001b]4;200;#102030\u0007B");

        Assert.Equal(0xFF102030u, StyleAt(adapter, 0).Foreground.Argb);
        Assert.Equal(0xFF102030u, StyleAt(adapter, 1).Foreground.Argb);
        Feed(parser, "\u001b[0m\u001b]104;200\u0007\u001b[u\u001b[3GC");
        uint resetColor = new TerminalPalette()[200];
        Assert.Equal(resetColor, StyleAt(adapter, 0).Foreground.Argb);
        Assert.Equal(resetColor, StyleAt(adapter, 1).Foreground.Argb);
        Assert.Equal(resetColor, StyleAt(adapter, 2).Foreground.Argb);
        Assert.Equal(200, StyleAt(adapter, 2).ForegroundIndex);
    }
}
