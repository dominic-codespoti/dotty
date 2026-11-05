using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>Terminal identity replies: DA2 version, XTVERSION and the XTGETTCAP <c>TN</c> name.</summary>
public sealed class TerminalIdentityTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("0.3.0", "\u001b[>1;300;0c")]
    [InlineData("0.3.0-dev+abc1234", "\u001b[>1;300;0c")]
    [InlineData("0.4.0-beta.1", "\u001b[>1;400;0c")]
    [InlineData("1.2.3", "\u001b[>1;10203;0c")]
    [InlineData("12.34.56", "\u001b[>1;123456;0c")]
    [InlineData("2", "\u001b[>1;20000;0c")]
    [InlineData("1.250.7", "\u001b[>1;19907;0c")]
    [InlineData("dev", "\u001b[>1;0;0c")]
    public void Da2_ReportsVersionNumberFromIdentity(string version, string expected)
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetTerminalIdentity(version, "xterm-256color");
        Feed(parser, "\u001b[>c");
        Feed(parser, "\u001b[>0c");
        Assert.Equal(new[] { expected, expected }, replies);
    }


    [Theory]
    [InlineData("\u001b[>q")]
    [InlineData("\u001b[>0q")]
    public void XtVersion_RepliesWithNameAndVersion(string query)
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetTerminalIdentity("0.3.0-dev+abc1234", "xterm-256color");
        Feed(parser, query);
        Assert.Equal(new[] { "\u001bP>|Dotty 0.3.0-dev+abc1234\u001b\\" }, replies);
    }

    [Fact]
    public void XtVersion_WithoutIdentityRepliesWithNameOnly()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[>q");
        Assert.Equal(new[] { "\u001bP>|Dotty\u001b\\" }, replies);
    }

    [Theory]
    [InlineData("\u001b[>q")]
    [InlineData("\u001b[>0q")]
    [InlineData("\u001b[>1q")]
    [InlineData("\u001b[>0;1q")]
    public void XtVersion_IsNeverACursorShapeChange(string query)
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b[6 q");
        Feed(parser, query);
        Assert.Equal(6, adapter.CursorShape);
    }

    [Theory]
    [InlineData("\u001b[>1q")]
    [InlineData("\u001b[>0;1q")]
    public void XtVersion_UndefinedParametersGetNoReply(string query)
    {
        var (_, parser, replies) = Setup();
        Feed(parser, query);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("xterm-256color", "787465726D2D323536636F6C6F72")]
    [InlineData("xterm-dotty", "787465726D2D646F747479")]
    public void Xtgettcap_TnReportsTheTermGivenToTheChild(string term, string hexValue)
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetTerminalIdentity("0.3.0", term);
        Feed(parser, "\u001bP+q544E\u001b\\");
        Assert.Equal(new[] { "\u001bP1+r544E=" + hexValue + "\u001b\\" }, replies);
    }

    [Fact]
    public void SetTerminalIdentity_BlankArgumentsKeepPreviousValues()
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetTerminalIdentity("1.2.3", "xterm-dotty");
        adapter.SetTerminalIdentity(" ", "");
        Feed(parser, "\u001b[>q");
        Feed(parser, "\u001b[>c");
        Assert.Equal(new[] { "\u001bP>|Dotty 1.2.3\u001b\\", "\u001b[>1;10203;0c" }, replies);
        Assert.Equal("xterm-dotty", adapter.TermName);
    }
}
