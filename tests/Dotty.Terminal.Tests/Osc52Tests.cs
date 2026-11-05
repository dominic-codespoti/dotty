using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Clipboard;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public class Osc52Tests
{
    private sealed class Rig
    {
        public readonly TerminalAdapter Adapter = new(24, 80);
        public readonly BasicAnsiParser Parser = new();
        public readonly List<ClipboardWriteRequest> Writes = new();
        public readonly List<ClipboardReadRequest> Reads = new();

        public Rig()
        {
            Parser.Handler = Adapter;
            Adapter.ClipboardWriteRequested += Writes.Add;
            Adapter.ClipboardReadRequested += Reads.Add;
        }

        public void Feed(string s) => Parser.Feed(Encoding.UTF8.GetBytes(s));
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Write_BelTerminator_Clipboard()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;c;{B64("hello")}\u0007");
        Assert.Equal(new ClipboardWriteRequest(ClipboardSelection.Clipboard, "hello"), Assert.Single(r.Writes));
    }

    [Fact]
    public void Write_StTerminator_Clipboard()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;c;{B64("hello")}\u001b\\");
        Assert.Equal("hello", Assert.Single(r.Writes).Text);
    }

    [Fact]
    public void Write_EmptySelector_IsSelectAndCutBuffer0()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;;{B64("x")}\u0007");
        Assert.Equal(ClipboardSelection.Select | ClipboardSelection.CutBuffer0, Assert.Single(r.Writes).Selection);
    }
    [Theory]
    [InlineData("dGVzdGluZyAxMjM='", "testing 123")]
    [InlineData("dGVz!dGluZyAxMjM=", "testing 123")]
    [InlineData("dGVzdGluZyAxMjM", "testing 123")]
    [InlineData("YWJjZ", "abc")]
    public void Write_XtermPaste64IgnoresNoiseAndDropsIncompleteTail(string payload, string expected)
    {
        var r = new Rig();
        r.Feed($"\u001b]52;;{payload}\u001b\\");

        Assert.Equal(expected, Assert.Single(r.Writes).Text);
    }

    [Fact]
    public void Write_P_IsPrimaryOnly()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;p;{B64("x")}\u0007");
        Assert.Equal(ClipboardSelection.Primary, Assert.Single(r.Writes).Selection);
    }

    [Fact]
    public void Write_Cp_IsBoth()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;cp;{B64("x")}\u0007");
        Assert.Equal(ClipboardSelection.Clipboard | ClipboardSelection.Primary, Assert.Single(r.Writes).Selection);
    }

    [Theory]

    [InlineData("q")]
    [InlineData("xyz")]
    public void Write_UnknownSelectorOnly_Ignored(string sel)
    {
        var r = new Rig();
        r.Feed($"\u001b]52;{sel};{B64("x")}\u0007");
        Assert.Empty(r.Writes);
    }

    [Fact]
    public void Write_NoSemicolon_Ignored()
    {
        var r = new Rig();
        r.Feed("\u001b]52\u0007");
        r.Feed("\u001b]52;c\u0007");
        Assert.Empty(r.Writes);
    }

    [Fact]
    public void Write_NonAlphabetOnlyClearsSelection()
    {
        var r = new Rig();
        r.Feed("\u001b]52;c;!!!\u0007");
        Assert.Equal(new ClipboardWriteRequest(ClipboardSelection.Clipboard, ""), Assert.Single(r.Writes));
    }

    [Fact]
    public void Write_EmbeddedWhitespace_StillDecodes()
    {
        var r = new Rig();
        var b = B64("hello world");
        r.Feed($"\u001b]52;c;{b[..4]} {b[4..]}\u0007");
        Assert.Equal("hello world", Assert.Single(r.Writes).Text);
    }

    [Fact]
    public void Write_EmptyData_IsWriteOfEmptyString()
    {
        var r = new Rig();
        r.Feed("\u001b]52;c;\u0007");
        Assert.Equal(new ClipboardWriteRequest(ClipboardSelection.Clipboard, ""), Assert.Single(r.Writes));
    }

    [Fact]
    public void Write_InvalidUtf8_UsesReplacementChars()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;c;{Convert.ToBase64String(new byte[] { 0xFF, 0xFE })}\u0007");
        Assert.Contains('\uFFFD', Assert.Single(r.Writes).Text);
    }

    [Fact]
    public void Write_Multibyte_RoundTrips()
    {
        var r = new Rig();
        const string text = "héllo 日本語 🙂";
        r.Feed($"\u001b]52;c;{B64(text)}\u0007");
        Assert.Equal(text, Assert.Single(r.Writes).Text);
    }

    [Fact]
    public void Write_100Kb_DeliveredWhole()
    {
        var r = new Rig();
        var text = new string('a', 100_000);
        r.Feed($"\u001b]52;c;{B64(text)}\u0007");
        Assert.Equal(text, Assert.Single(r.Writes).Text);
    }

    [Fact]
    public void Write_OverCapWithNoise_DiscardedAndFollowingWriteRecovers()
    {
        var r = new Rig();
        r.Feed($"\u001b]52;c;!{new string('A', 1_048_581)}\u0007\u001b]52;c;{B64("ok")}\u0007");
        Assert.Equal("ok", Assert.Single(r.Writes).Text);
    }


    [Fact]
    public void Read_Primary_RaisesRequest()
    {
        var r = new Rig();
        r.Feed("\u001b]52;p;?\u0007");
        Assert.Equal(ClipboardSelection.Primary, Assert.Single(r.Reads).Selection);
    }

    [Fact]
    public void Read_EmptySelector_PreservesS0ReplySelector()
    {
        var r = new Rig();
        r.Feed("\u001b]52;;?\u0007");
        Assert.Equal(ClipboardSelection.Select | ClipboardSelection.CutBuffer0, Assert.Single(r.Reads).Selection);
        Assert.Equal("s0", Assert.Single(r.Reads).ReplySelector);
    }

    [Fact]
    public void Read_NoSubscriber_DoesNotThrow()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.ASCII.GetBytes("\u001b]52;c;?\u0007"));
    }

    [Fact]
    public void Read_QuestionFollowedByBase64Data_IsWriteNotQuery()
    {
        var r = new Rig();
        r.Feed("\u001b]52;c;?QUJD\u0007");
        Assert.Empty(r.Reads);
        Assert.Equal("ABC", Assert.Single(r.Writes).Text);
    }
}
