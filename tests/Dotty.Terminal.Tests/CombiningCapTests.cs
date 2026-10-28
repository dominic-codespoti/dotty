using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>A cell keeps its base code point plus at most 64 combining code points.</summary>
public sealed class CombiningCapTests
{
    private const int MaxScalars = 65;
    private const string Acute = "\u0301";
    private const string MusicalStem = "\U0001D165"; // astral combining mark (surrogate pair)

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    private static string? CellText(TerminalAdapter adapter, int row, int col)
    {
        CellHot cell = adapter.Buffer.GetCell(row, col);
        ColdCell cold = adapter.Buffer.GetColdCell(row, col);
        return GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
    }

    private static int ScalarCount(string text)
    {
        int count = 0;
        foreach (Rune _ in text.EnumerateRunes()) count++;
        return count;
    }

    private static string Repeat(string text, int count)
    {
        var sb = new StringBuilder(text.Length * count);
        for (int i = 0; i < count; i++) sb.Append(text);
        return sb.ToString();
    }

    private static void AssertWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "high surrogate without low");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]), "lone low surrogate");
            }
        }
    }

    [Fact]
    public void AsciiBase_MarksInOneFeed_AreCappedAndNextCharLandsInNextCell()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "a" + Repeat(Acute, 200) + "b");

        string? first = CellText(adapter, 0, 0);
        Assert.Equal(MaxScalars, ScalarCount(first!));
        Assert.Equal("a" + Repeat(Acute, 64), first);
        Assert.Equal("b", CellText(adapter, 0, 1));
        Assert.Equal(2, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void AsciiBase_MarksInSeparateFeeds_AreCapped()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "a");
        for (int i = 0; i < 200; i++) Feed(parser, Acute);
        Feed(parser, "b");

        Assert.Equal("a" + Repeat(Acute, 64), CellText(adapter, 0, 0));
        Assert.Equal("b", CellText(adapter, 0, 1));
    }

    [Fact]
    public void NonAsciiBase_ClusterWritePathIsCapped()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "\u00F1" + Repeat(Acute, 200) + "b"); // n-tilde: the whole cluster reaches the grapheme writer

        Assert.Equal("\u00F1" + Repeat(Acute, 64), CellText(adapter, 0, 0));
        Assert.Equal("b", CellText(adapter, 0, 1));
    }

    [Fact]
    public void ClusterOfExactlyTheLimit_IsKeptWhole()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "\u00F1" + Repeat(Acute, 64) + "b");

        Assert.Equal("\u00F1" + Repeat(Acute, 64), CellText(adapter, 0, 0));
        Assert.Equal("b", CellText(adapter, 0, 1));
    }

    [Fact]
    public void AstralBase_With64Marks_AcceptsNoMore()
    {
        var (adapter, parser) = Setup();
        const string astralBase = "\U0001D11E"; // musical G clef, one cell wide
        Feed(parser, astralBase + Repeat(Acute, 64));
        Feed(parser, Acute);
        Feed(parser, Acute);
        Feed(parser, "b");

        string? text = CellText(adapter, 0, 0);
        Assert.Equal(astralBase + Repeat(Acute, 64), text);
        Assert.Equal(MaxScalars, ScalarCount(text!));
        Assert.Equal("b", CellText(adapter, 0, 1));
    }

    [Fact]
    public void MarksAfterWideCharacter_AreCapped()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "界" + Repeat(Acute, 200) + "b");

        Assert.Equal("界" + Repeat(Acute, 64), CellText(adapter, 0, 0));
        Assert.Equal("b", CellText(adapter, 0, 2));
        Assert.Equal(3, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void SurrogatePairMarks_AreNeverSplitAtTheLimit()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "a" + Repeat(MusicalStem, 70) + "b");

        string? text = CellText(adapter, 0, 0);
        Assert.Equal("a" + Repeat(MusicalStem, 64), text);
        Assert.Equal(1 + 64 * 2, text!.Length);
        AssertWellFormed(text);
        Assert.Equal("b", CellText(adapter, 0, 1));
    }

    [Fact]
    public void SurrogatePairMarks_OneAtATime_StayWellFormed()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "a");
        for (int i = 0; i < 70; i++) Feed(parser, MusicalStem);

        string? text = CellText(adapter, 0, 0);
        Assert.Equal("a" + Repeat(MusicalStem, 64), text);
        AssertWellFormed(text!);
    }

    [Fact]
    public void MixedBmpAndAstralMarks_CapByScalarCount()
    {
        var (adapter, parser) = Setup();
        // 1 base + 63 BMP marks + 1 astral mark = 65 scalars; the trailing astral mark must be dropped whole.
        Feed(parser, "a" + Repeat(Acute, 63) + MusicalStem + MusicalStem);

        string? text = CellText(adapter, 0, 0);
        Assert.Equal("a" + Repeat(Acute, 63) + MusicalStem, text);
        AssertWellFormed(text!);
    }

    [Fact]
    public void RepeatedCappedWrites_ReuseTheStoredCluster()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "a" + Repeat(Acute, 200));
        short first = adapter.Buffer.GetColdCell(0, 0).GraphemeIndex;
        Feed(parser, "\u001b[H" + "a" + Repeat(Acute, 200));
        short second = adapter.Buffer.GetColdCell(0, 0).GraphemeIndex;

        Assert.True(first > 0);
        Assert.Equal(first, second);
    }
}
