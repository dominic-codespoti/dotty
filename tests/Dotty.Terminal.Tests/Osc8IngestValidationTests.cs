using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class Osc8IngestValidationTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Create()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) =>
        parser.Feed(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x00)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x202E)]
    [InlineData(0x200B)]
    [InlineData(0x85)]
    [InlineData(0x7F)]
    [InlineData(0x2028)]
    public void InvalidUri_LeavesTextWithoutHyperlink(int code)
    {
        var (adapter, parser) = Create();
        string uri = "https://evil.test/a" + (char)code + "b";

        Feed(parser, $"\u001b]8;;{uri}\u001b\\text\u001b]8;;\u001b\\");

        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 0).HyperlinkId);
        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 3).HyperlinkId);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x0A)]
    [InlineData(0x1B)]
    [InlineData(0x7F)]
    [InlineData(0x9F)]
    public void OscDispatch_InvalidUriWithControl_ClosesActiveLink(int code)
    {
        // Bypasses the parser (which aborts C0 itself) to test the adapter gate.
        var adapter = new TerminalAdapter(rows: 4, columns: 20);
        adapter.OnOperatingSystemCommand(8, (";https://good.test/").AsSpan());
        adapter.OnPrint("A".AsSpan());
        adapter.OnOperatingSystemCommand(8, (";https://evil.test/a" + (char)code + "b").AsSpan());
        adapter.OnPrint("B".AsSpan());

        Assert.NotEqual(0, adapter.Buffer.GetColdCell(0, 0).HyperlinkId);
        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 1).HyperlinkId);
    }

    [Fact]
    public void InvalidUri_ClosesPreviouslyActiveLink()
    {
        var (adapter, parser) = Create();

        // U+200B (format char): C0 controls would abort the OSC in the parser before the adapter sees it.
        Feed(parser, "\u001b]8;;https://good.test/\u001b\\A\u001b]8;;https://evil.test/a\u200Bb\u001b\\B");

        Assert.NotEqual(0, adapter.Buffer.GetColdCell(0, 0).HyperlinkId);
        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 1).HyperlinkId);
    }

    [Fact]
    public void OverlongUri_IsIgnored_WhileLongValidUrisAreStored()
    {
        var (adapter, parser) = Create();
        string max = "https://e.test/" + new string('a', HyperlinkUri.MaxLength - "https://e.test/".Length);
        string tooLong = max + "a";

        Feed(parser, $"\u001b]8;;{max}\u001b\\A\u001b]8;;{tooLong}\u001b\\B");

        Assert.Equal(max, adapter.Buffer.GetHyperlinkUrl(adapter.Buffer.GetColdCell(0, 0).HyperlinkId));
        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 1).HyperlinkId);
    }

    [Fact]
    public void UriContainingSemicolonsAfterParams_IsStoredWhole()
    {
        var (adapter, parser) = Create();

        Feed(parser, "\u001b]8;id=a;https://e.test/x;y;z\u001b\\T\u001b]8;;\u001b\\");

        Assert.Equal("https://e.test/x;y;z", adapter.Buffer.GetHyperlinkUrl(adapter.Buffer.GetColdCell(0, 0).HyperlinkId));
    }

    [Fact]
    public void InvalidParams_CloseTheLink()
    {
        var (adapter, parser) = Create();
        string longId = new string('a', HyperlinkUri.MaxIdLength + 1);

        Feed(parser, $"\u001b]8;id={longId};https://e.test/\u001b\\T");

        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 0).HyperlinkId);
    }

    [Fact]
    public void Osc8Disabled_StoresNothingAndClosesActiveLink()
    {
        var (adapter, parser) = Create();
        Feed(parser, "\u001b]8;;https://e.test/one\u001b\\A");
        Assert.NotEqual(0, adapter.Buffer.GetColdCell(0, 0).HyperlinkId);

        adapter.Osc8HyperlinksEnabled = false;
        Feed(parser, "\u001b]8;;https://e.test/two\u001b\\B");

        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 1).HyperlinkId);
        Assert.Null(adapter.Buffer.GetHyperlinkUrl(2));
    }

    [Fact]
    public void SchemeIsNotFilteredAtIngest()
    {
        var (adapter, parser) = Create();

        Feed(parser, "\u001b]8;;javascript:alert(1)\u001b\\J");

        Assert.Equal("javascript:alert(1)", adapter.Buffer.GetHyperlinkUrl(adapter.Buffer.GetColdCell(0, 0).HyperlinkId));
    }

    [Fact]
    public void OnHyperlink_RoutesThroughSameValidation()
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 20);

        adapter.OnHyperlink("https://e.test/a\u0000b");
        adapter.OnPrint("x".AsSpan());

        Assert.Equal(0, adapter.Buffer.GetColdCell(0, 0).HyperlinkId);
    }
}
