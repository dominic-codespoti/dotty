using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class SpecialColorProtocolTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        adapter.SetDefaultColors("#abc", "#123456");
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string value) => parser.Feed(Encoding.UTF8.GetBytes(value));
    private static string Osc(string value) => "\x1b]" + value + "\x1b\\";

    [Fact]
    public void SpecialColorAliasesShareStateAndRetainTheRequestedReplyForm()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, Osc("4;256;#102030;257;#405060") + Osc("5;0;?;1;?;0;#708090") + Osc("4;256;?;257;?"));
        Assert.Equal(new[]
        {
            Osc("5;0;rgb:1010/2020/3030"), Osc("5;1;rgb:4040/5050/6060"),
            Osc("4;256;rgb:7070/8080/9090"), Osc("4;257;rgb:4040/5050/6060"),
        }, replies);
    }

    [Fact]
    public void SelectedSpecialResetsDoNotResetIndexedColorsOrOtherSpecialSlots()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, Osc("4;1;#112233;256;#223344;257;#334455;260;#445566"));
        Feed(parser, Osc("105;1;bad;;4;256") + Osc("4;1;?;256;?;257;?;260;?"));
        Assert.Equal(new[]
        {
            Osc("4;1;rgb:1111/2222/3333"), Osc("4;256;rgb:2222/3333/4444"),
            Osc("4;257;rgb:aaaa/bbbb/cccc"), Osc("4;260;rgb:aaaa/bbbb/cccc"),
        }, replies);
    }

    [Fact]
    public void EmptySpecialResetAndIndexedAliasResetHaveDistinctScopes()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, Osc("4;1;#112233;256;#223344;257;#334455"));
        Feed(parser, Osc("104;256") + Osc("5;0;?;1;?") + Osc("105") + Osc("4;1;?;257;?"));
        Assert.Equal(new[]
        {
            Osc("5;0;rgb:aaaa/bbbb/cccc"), Osc("5;1;rgb:3333/4444/5555"),
            Osc("4;1;rgb:1111/2222/3333"), Osc("4;257;rgb:aaaa/bbbb/cccc"),
        }, replies);
    }

    [Theory]
    [InlineData("\x1b]104\x1b\\", false)]
    [InlineData("\x1b[!p", false)]
    [InlineData("\x1b" + "c", true)]
    public void WholePaletteResetsRestoreSpecialDefaultsWithoutDisablingTheirModes(string reset, bool clearsDynamic)
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, Osc("4;1;#102030;258;#405060") + Osc("10;#708090") + Osc("106;2;7"));
        Feed(parser, reset + Osc("4;1;?;258;?") + Osc("10;?"));
        Assert.Equal(new[]
        {
            Osc("4;1;rgb:aaaa/0000/0000"), Osc("4;258;rgb:aaaa/bbbb/cccc"),
            Osc(clearsDynamic ? "10;rgb:aaaa/bbbb/cccc" : "10;rgb:7070/8080/9090"),
        }, replies);
        Assert.Equal((byte)4, adapter.SpecialColors.Modes);
    }

    [Fact]
    public void SpecialColorDefaultsTrackTheThemeButExplicitEqualColorsRemainOverrides()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, Osc("5;0;#aabbcc"));
        adapter.SetDefaultColors("#123", "#456");
        Feed(parser, Osc("5;0;?;1;?") + Osc("105;0") + Osc("5;0;?"));
        Assert.Equal(new[]
        {
            Osc("5;0;rgb:aaaa/bbbb/cccc"), Osc("5;1;rgb:1111/2222/3333"),
            Osc("5;0;rgb:1111/2222/3333"),
        }, replies);
    }

    [Fact]
    public void TerminalHashColorsDoNotChangeCssThemeExpansion()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, Osc("10;?") + Osc("10;#abc") + Osc("10;?") + Osc("110") + Osc("10;?"));
        Assert.Equal(new[]
        {
            Osc("10;rgb:aaaa/bbbb/cccc"), Osc("10;rgb:a0a0/b0b0/c0c0"), Osc("10;rgb:aaaa/bbbb/cccc"),
        }, replies);
    }

    [Fact]
    public void DynamicFieldsAdvanceIndependentlyAndQueriesRemainOrdered()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, Osc("10;#112233;?") + Osc("10;?;#445566") + Osc("10;?;?"));
        Feed(parser, Osc("10;;#778899") + Osc("11;#123456;#ffffff;#abcdef") + Osc("10;?;?"));
        Assert.Equal(new[]
        {
            Osc("11;rgb:1212/3434/5656"), Osc("10;rgb:1111/2222/3333"),
            Osc("10;rgb:1111/2222/3333"), Osc("11;rgb:4444/5555/6666"),
            Osc("10;rgb:1111/2222/3333"), Osc("11;rgb:1212/3434/5656"),
        }, replies);
    }

    [Fact]
    public void InvalidSpecialEntriesDoNotConsumeOrCorruptFollowingValidPairs()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, Osc("5;0;#102030") + Osc("5;5;?;-1;?;0;not-a-color;0001;#405060;0;?;1;?"));
        Feed(parser, Osc("4;261;?;999999999999999999;?;256;?"));
        Assert.Equal(new[]
        {
            Osc("5;0;rgb:1010/2020/3030"), Osc("5;1;rgb:4040/5050/6060"),
            Osc("4;256;rgb:1010/2020/3030"),
        }, replies);
    }

    [Fact]
    public void SpecialModesAcceptNonzeroValuesAndIgnoreMalformedControls()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, Osc("6;0;2") + Osc("106;5;0009") + Osc("106;1;bad") + Osc("6;6;1"));
        Assert.Equal((byte)33, adapter.SpecialColors.Modes);
        Feed(parser, Osc("106;0;000") + Osc("6;5;0"));
        Assert.Equal((byte)0, adapter.SpecialColors.Modes);
    }

    [Fact]
    public void SpecialColorsAndModesAreIsolatedBetweenAdapters()
    {
        var (first, parser, _) = Setup();
        var (second, other, replies) = Setup();
        Feed(parser, Osc("5;0;#102030") + Osc("6;0;1"));
        Feed(other, Osc("5;0;?"));
        Assert.Equal(Osc("5;0;rgb:aaaa/bbbb/cccc"), Assert.Single(replies));
        Assert.Equal((byte)0, second.SpecialColors.Modes);
        Assert.Equal(0xFF102030u, first.SpecialColors.Bold);
    }
}
