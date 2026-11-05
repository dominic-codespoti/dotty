using System.Text;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// OSC/DCS/APC/PM/SOS string states: only ESC \ terminates a string, ESC or
/// any control otherwise aborts it (xterm), and C0/CAN/SUB gaps in CSI/escape
/// states.
/// </summary>
public sealed class ParserStringStateTests
{
    private static (BasicAnsiParser Parser, BoundedSequenceTests.RecordingHandler Handler) Setup()
    {
        var handler = new BoundedSequenceTests.RecordingHandler();
        return (new BasicAnsiParser { Handler = handler }, handler);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("_")]
    [InlineData("^")]
    [InlineData("X")]
    public void ApcPmSos_PayloadIsNotPrinted(string introducer)
    {
        var (parser, handler) = Setup();
        Feed(parser, $"\u001b{introducer}Gi=31,s=1,v=1,a=q;AAAA\u001b\\tail");
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
        Assert.Empty(handler.ControlCalls);
    }

    [Fact]
    public void Apc_SplitAcrossFeeds_IsStillDiscarded()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b_Gabc");
        Feed(parser, "def\u001b");
        Feed(parser, "\\tail");
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Theory]
    [InlineData((byte)0x18)]
    [InlineData((byte)0x1A)]
    public void Apc_CancelledByCanOrSub_TextAfterwardsIsPrinted(byte cancel)
    {
        var (parser, handler) = Setup();
        byte[] input = [0x1b, (byte)'_', (byte)'x', cancel, (byte)'t', (byte)'a', (byte)'i', (byte)'l'];
        parser.Feed(input);
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Fact]
    public void Apc_WithEscapeSequence_AbortsAndRunsTheSequence()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b_xx\u001b[3A");
        Assert.Equal(new[] { 3 }, handler.CursorUpCalls);
        Assert.Empty(handler.PrintCalls);
    }

    [Fact]
    public void Apc_ControlCharacterAbortsStringAndExecutes()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b_ab\ncd\u001b\\");
        Assert.Equal(new[] { "LF" }, handler.ControlCalls);
        Assert.Equal(new[] { "cd" }, handler.PrintCalls);
    }

    [Fact]
    public void Apc_0x9CIsNotATerminator()
    {
        var (parser, handler) = Setup();
        // U+201C is E2 80 9C in UTF-8.
        Feed(parser, "\u001b_a\u201Cb\u001b\\tail");
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Fact]
    public void Osc_EscapeThenNonStAbortsStringAndRunsEscape()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b]0;title\u001b[7Btail");
        Assert.Empty(handler.OscCalls);
        Assert.Equal(new[] { 7 }, handler.CursorDownCalls);
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Fact]
    public void Osc_ControlCharacterAbortsStringAndExecutes()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b]0;ti\ntle\u0007");
        Assert.Empty(handler.OscCalls);
        Assert.Equal(new[] { "LF", "BEL" }, handler.ControlCalls);
        Assert.Equal(new[] { "tle" }, handler.PrintCalls);
    }

    [Fact]
    public void Osc_DoubledEscapeStillTerminatesWithSt()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b]0;t\u001b\u001b\\x");
        Assert.Equal(new[] { (0, "t") }, handler.OscCalls);
        Assert.Equal(new[] { "x" }, handler.PrintCalls);
    }

    [Fact]
    public void Osc_EscapeBackslashTerminates()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b]0;t\u001b\\x");
        Assert.Equal(new[] { (0, "t") }, handler.OscCalls);
        Assert.Equal(new[] { "x" }, handler.PrintCalls);
    }

    [Fact]
    public void Dcs_0x9CIsNotATerminator()
    {
        var (parser, handler) = Setup();
        // U+201C is E2 80 9C in UTF-8; the DCS only ends at ESC \.
        Feed(parser, "\u001bP+q544E\u201Ctail\u001b\\after");
        Assert.Equal(new[] { "after" }, handler.PrintCalls);
    }

    [Fact]
    public void Dcs_EscapeThenNonStAbortsAndRunsEscape()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001bP+q544E\u001b[7Btail");
        Assert.Equal(new[] { 7 }, handler.CursorDownCalls);
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Fact]
    public void Dcs_OverflowThenEmbeddedEscapeDoesNotResumeCapture()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001bP+q" + new string('x', 4097) + "\u001b[0m544E\u001b\\");
        // The overflowed string is abandoned at ESC; "[0m" is a real SGR and
        // "544E" is ordinary text rather than a resumed DCS payload.
        Assert.Equal(new[] { "0" }, handler.GraphicsCalls);
        Assert.Equal(new[] { "544E" }, handler.PrintCalls);
    }

    [Theory]
    [InlineData((byte)0x18)]
    [InlineData((byte)0x1A)]
    public void Dcs_OverflowedStringIsEndedByCanOrSub(byte cancel)
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001bP+q" + new string('x', 4097));
        parser.Feed([cancel, (byte)'o', (byte)'k']);
        Assert.Equal(new[] { "ok" }, handler.PrintCalls);
    }

    [Fact]
    public void Csi_ControlInsideSequenceExecutesAndSequenceCompletes()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[1\nA");
        Assert.Equal(new[] { "LF" }, handler.ControlCalls);
        Assert.Equal(new[] { 1 }, handler.CursorUpCalls);
    }

    [Fact]
    public void Csi_DelInsideSequenceIsIgnored()
    {
        var (parser, handler) = Setup();
        parser.Feed([0x1b, (byte)'[', (byte)'2', 0x7F, (byte)'A']);
        Assert.Empty(handler.ControlCalls);
        Assert.Equal(new[] { 2 }, handler.CursorUpCalls);
    }

    [Fact]
    public void Escape_ControlExecutesThenFinalApplies()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b\nM");
        Assert.Equal(new[] { "LF", "RI" }, handler.ControlCalls);
    }

    [Fact]
    public void Ris_ResetsParserCharset()
    {
        var (parser, handler) = Setup();
        // ESC ( 0 selects DEC special graphics; RIS must return to ASCII.
        Feed(parser, "\u001b(0\u001bcq");
        Assert.Equal(new[] { "q" }, handler.PrintCalls);
    }

    [Fact]
    public void Apc_BelAbortsStringAndRings()
    {
        var (parser, handler) = Setup();
        // BEL terminates only an OSC; inside APC/PM/SOS/DCS it is a control that aborts.
        Feed(parser, "\u001b_ab\u0007cd\u001b\\");
        Assert.Equal(new[] { "BEL" }, handler.ControlCalls);
        Assert.Equal(new[] { "cd" }, handler.PrintCalls);
    }

    [Fact]
    public void Dcs_BelAbortsStringAndRings()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001bP+q544E\u0007tail");
        Assert.Equal(new[] { "BEL" }, handler.ControlCalls);
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Fact]
    public void OscOverflow_ControlAbortsDiscardAndExecutes()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b]0;" + new string('x', 1_048_577) + "\ntail");
        Assert.Empty(handler.OscCalls);
        Assert.Equal(new[] { "LF" }, handler.ControlCalls);
        Assert.Equal(new[] { "tail" }, handler.PrintCalls);
    }

    [Fact]
    public void OscAtExactCap_IsDispatched()
    {
        var (parser, handler) = Setup();
        // "0;" + data == 1 MiB payload bytes.
        string data = new string('x', 1_048_576 - 2);
        Feed(parser, "\u001b]0;" + data + "\u0007");
        Assert.Equal(new[] { (0, data) }, handler.OscCalls);
    }
}
