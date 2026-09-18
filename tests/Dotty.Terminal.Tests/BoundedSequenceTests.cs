using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class BoundedSequenceTests
{
    [Fact]
    public void CsiParameterOverflow_IsDiscarded_AndFollowingSequenceAndTextRecover()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        var input = $"\u001b[{new string('1', 257)}A\u001b[7Btail";
        parser.Feed(Encoding.ASCII.GetBytes(input));

        Assert.Empty(handler.CursorUpCalls);
        Assert.Single(handler.CursorDownCalls);
        Assert.Equal(7, handler.CursorDownCalls[0]);
        Assert.Contains("tail", handler.PrintCalls);
    }

    [Fact]
    public void OscPayloadOverflow_IsDiscarded_AndFollowingSequenceAndTextRecover()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        var oversizedPayload = new string('x', 65_537);
        var input = $"\u001b]8;;{oversizedPayload}\u0007\u001b]8;;ok\u0007tail";
        parser.Feed(Encoding.UTF8.GetBytes(input));

        Assert.Single(handler.OscCalls);
        Assert.Equal((8, ";ok"), handler.OscCalls[0]);
        Assert.Contains("tail", handler.PrintCalls);
    }

    [Fact]
    public void CsiSplitAcrossFeeds_DispatchesOnceWithAllParameters()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.ASCII.GetBytes("\u001b[12;34"));
        Assert.Empty(handler.CursorMoves);

        parser.Feed(Encoding.ASCII.GetBytes("H"));

        Assert.Single(handler.CursorMoves);
        Assert.Equal((12, 34), handler.CursorMoves[0]);
    }

    [Fact]
    public void Osc8SplitAcrossFeeds_DispatchesOnceWithCompletePayload()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b]8;;https://example"));
        Assert.Empty(handler.OscCalls);

        parser.Feed(Encoding.UTF8.GetBytes(".com\u0007after"));

        Assert.Single(handler.OscCalls);
        Assert.Equal((8, ";https://example.com"), handler.OscCalls[0]);
        Assert.Contains("after", handler.PrintCalls);
    }

    [Fact]
    public void CsiMoreThanEightParameters_AppliesOnlyFirstEight()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.ASCII.GetBytes("\u001b[?25;6;7;2004;1004;1000;1002;1003;1049h"));

        Assert.Equal(
            new[] { 25, 6, 7, 2004, 1004, 1000, 1002, 1003 },
            handler.PrivateModeCalls.ConvertAll(call => call.mode));
        Assert.DoesNotContain(handler.PrivateModeCalls, call => call.mode == 1049);
    }

    [Fact]
    public void UnterminatedCsi_EscStartsFreshSequence_AndFollowingTextSurvives()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.ASCII.GetBytes("\u001b[31\u001b[0mtext"));

        Assert.Single(handler.GraphicsCalls);
        Assert.Equal("0", handler.GraphicsCalls[0]);
        Assert.Contains("text", handler.PrintCalls);
    }

    [Theory]
    [InlineData((byte)0x18)]
    [InlineData((byte)0x1A)]
    public void UnterminatedCsi_CancelControl_AndFollowingTextSurvives(byte cancel)
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        var input = new byte[] { 0x1b, (byte)'[', (byte)'3', (byte)'1', cancel, (byte)'t', (byte)'e', (byte)'x', (byte)'t' };
        parser.Feed(input);

        Assert.Empty(handler.GraphicsCalls);
        Assert.Contains("text", handler.PrintCalls);
    }

    [Fact]
    public void UnterminatedOsc_BelTerminates_AndFollowingTextSurvives()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b]8;;https://example.com\u0007text"));

        Assert.Single(handler.OscCalls);
        Assert.Equal((8, ";https://example.com"), handler.OscCalls[0]);
        Assert.Contains("text", handler.PrintCalls);
    }

    [Theory]
    [InlineData((byte)0x18)]
    [InlineData((byte)0x1A)]
    public void UnterminatedOsc_CancelControl_AndFollowingTextSurvives(byte cancel)
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        var prefix = Encoding.UTF8.GetBytes("\u001b]8;;https://example.com");
        var input = new byte[prefix.Length + 5];
        prefix.CopyTo(input, 0);
        input[prefix.Length] = cancel;
        Encoding.ASCII.GetBytes("text").CopyTo(input, prefix.Length + 1);
        parser.Feed(input);

        Assert.Empty(handler.OscCalls);
        Assert.Contains("text", handler.PrintCalls);
    }

    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public List<string> GraphicsCalls { get; } = new();
        public List<(int row, int col)> CursorMoves { get; } = new();
        public List<int> CursorUpCalls { get; } = new();
        public List<int> CursorDownCalls { get; } = new();
        public List<(int code, string payload)> OscCalls { get; } = new();
        public List<(int mode, bool enabled)> PrivateModeCalls { get; } = new();

        object? ITerminalHandler.Buffer => null;
        event Action<string>? ITerminalHandler.RenderRequested { add { } remove { } }
        event Action<string>? ITerminalHandler.ClipboardWriteRequested { add { } remove { } }
        event Action<string>? ITerminalHandler.TitleChanged { add { } remove { } }

        void ITerminalHandler.OnHyperlink(string uri) { }
        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintCalls.Add(text.ToString());
        void ITerminalHandler.OnEraseDisplay(int mode) { }
        void ITerminalHandler.OnClearScrollback() { }
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> parameters) => GraphicsCalls.Add(parameters.ToString());
        void ITerminalHandler.OnBell() { }

        void ITerminalHandler.OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload) => OscCalls.Add((code, payload.ToString()));

        void ITerminalHandler.OnMoveCursor(int row, int col) => CursorMoves.Add((row, col));
        void ITerminalHandler.OnCursorUp(int n) => CursorUpCalls.Add(n);
        void ITerminalHandler.OnCursorDown(int n) => CursorDownCalls.Add(n);
        void ITerminalHandler.OnCursorForward(int n) { }
        void ITerminalHandler.OnCursorBack(int n) { }
        void ITerminalHandler.OnEraseLine(int mode) { }
        void ITerminalHandler.OnCarriageReturn() { }
        void ITerminalHandler.OnLineFeed() { }
        void ITerminalHandler.OnSetScrollRegion(int top1Based, int bottom1Based) { }
        void ITerminalHandler.OnSetOriginMode(bool enabled) => PrivateModeCalls.Add((6, enabled));
        void ITerminalHandler.OnSetAlternateScreen(bool enabled) => PrivateModeCalls.Add((1049, enabled));
        void ITerminalHandler.OnSetCursorVisibility(bool visible) => PrivateModeCalls.Add((25, visible));
        void ITerminalHandler.OnSaveCursor() { }
        void ITerminalHandler.OnRestoreCursor() { }
        void ITerminalHandler.OnInsertChars(int n) { }
        void ITerminalHandler.OnDeleteChars(int n) { }
        void ITerminalHandler.OnEraseCharacters(int n) { }
        void ITerminalHandler.OnInsertLines(int n) { }
        void ITerminalHandler.OnDeleteLines(int n) { }
        void ITerminalHandler.OnSetAutoWrap(bool enabled) => PrivateModeCalls.Add((7, enabled));
        void ITerminalHandler.OnSetTabStop() { }
        void ITerminalHandler.OnClearTabStop() { }
        void ITerminalHandler.OnClearAllTabStops() { }
        void ITerminalHandler.OnReverseIndex() { }
        void ITerminalHandler.OnSetBracketedPasteMode(bool enabled) => PrivateModeCalls.Add((2004, enabled));
        void ITerminalHandler.OnDeviceStatusReport(int code) { }
        void ITerminalHandler.OnCursorPositionReport() { }
        void ITerminalHandler.OnCursorHorizontalAbsolute(int col) { }
        void ITerminalHandler.OnCursorVerticalAbsolute(int row) { }
        void ITerminalHandler.OnCursorNextLine(int n) { }
        void ITerminalHandler.OnCursorPreviousLine(int n) { }
        void ITerminalHandler.OnScrollUp(int n) { }
        void ITerminalHandler.OnScrollDown(int n) { }
        void ITerminalHandler.OnFullReset() { }
        void ITerminalHandler.OnRepeatCharacter(int n) { }
        void ITerminalHandler.OnTab() { }
        void ITerminalHandler.OnBackTab(int n) { }
        void ITerminalHandler.OnSetKeypadApplicationMode(bool enabled) { }
        void ITerminalHandler.OnSetCursorShape(int shape) { }
        void ITerminalHandler.OnSetApplicationCursorKeys(bool enabled) { }
        void ITerminalHandler.OnSendDeviceAttributes(int daType) { }
        void ITerminalHandler.OnMouseEvent(int button, int col, int row, bool isPress) { }
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) => PrivateModeCalls.Add((mode, enabled));
        void ITerminalHandler.OnSetSynchronizedUpdate(bool enabled) { }
        void ITerminalHandler.OnSetKittyKeyboardMode(int mode) { }
        void ITerminalHandler.OnQueryKittyKeyboard() { }
        void ITerminalHandler.FlushRender() { }
        void ITerminalHandler.OnSetFocusReporting(bool enabled) => PrivateModeCalls.Add((1004, enabled));
        void ITerminalHandler.OnWindowReport(int command) { }
    }
}
