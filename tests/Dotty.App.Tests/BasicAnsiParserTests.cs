using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public class BasicAnsiParserTests
{
    [Fact]
    public void DecGraphicsTranslateToUnicode()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b(0lqk\u001b(BABC"));

        Assert.Equal("┌─┐", handler.PrintCalls[0]);
        Assert.Equal("ABC", handler.PrintCalls[1]);
    }

    [Fact]
    public void CharsetSettingPersistsBetweenChunks()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b(0q"));
        parser.Feed(Encoding.UTF8.GetBytes("x"));
        parser.Feed(Encoding.UTF8.GetBytes("x\u001b(B"));

        Assert.Equal("─", handler.PrintCalls[0]);
        Assert.Equal("│", handler.PrintCalls[1]);
        Assert.Equal("│", handler.PrintCalls[2]);
        Assert.Equal(3, handler.PrintCalls.Count);
    }

    [Theory]
    [InlineData("\u001b[J", 0)]
    [InlineData("\u001b[0J", 0)]
    [InlineData("\u001b[1J", 1)]
    [InlineData("\u001b[2J", 2)]
    public void EraseDisplayModes_ZeroOneTwo_AreForwarded(string sequence, int expectedMode)
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes(sequence));

        Assert.Single(handler.EraseDisplayCalls);
        Assert.Equal(expectedMode, handler.EraseDisplayCalls[0]);
    }

    [Fact]
    public void EraseDisplayModeThree_ClearsScrollbackOnly()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[3J"));

        Assert.Empty(handler.EraseDisplayCalls);
        Assert.Equal(1, handler.ClearScrollbackCalls);
    }

    [Fact]
    public void CsiSaveAndRestoreCursor_AreForwarded()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[sHello\u001b[u"));

        Assert.Equal(1, handler.SaveCursorCalls);
        Assert.Equal(1, handler.RestoreCursorCalls);
    }

    [Fact]
    public void CsiDeviceStatusReport6_UsesOnDeviceStatusReport()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[6n"));

        Assert.Single(handler.DeviceStatusReportCodes);
        Assert.Equal(6, handler.DeviceStatusReportCodes[0]);
        Assert.Equal(0, handler.CursorPositionReportCalls);
    }

    [Fact]
    public void CsiPrivateDeviceStatusReport6_UsesOnCursorPositionReport()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?6n"));

        Assert.Empty(handler.DeviceStatusReportCodes);
        Assert.Equal(1, handler.CursorPositionReportCalls);
    }

    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public List<bool> SetCursorVisibilityCalls { get; } = new();
        public List<(int mode, bool enabled)> SetMouseModeCalls { get; } = new();
        public List<int> EraseDisplayCalls { get; } = new();
        public int ClearScrollbackCalls { get; set; }
        public int SaveCursorCalls { get; set; }
        public int RestoreCursorCalls { get; set; }
        public List<int> DeviceStatusReportCodes { get; } = new();
        public int CursorPositionReportCalls { get; set; }

        object? ITerminalHandler.Buffer => null;
        event Action<string>? ITerminalHandler.RenderRequested {
            add {}
            remove {}
        }
        event Action<string>? ITerminalHandler.ClipboardWriteRequested {
            add {}
            remove {}
        }
        event Action<string>? ITerminalHandler.TitleChanged {
            add {}
            remove {}
        }
        event Action<string>? ITerminalHandler.LinkOpened {
            add {}
            remove {}
        }
        void ITerminalHandler.OnHyperlink(string uri) {} 
        // add { } remove { } }

        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintCalls.Add(text.ToString());
        void ITerminalHandler.OnEraseDisplay(int mode) => EraseDisplayCalls.Add(mode);
        void ITerminalHandler.OnClearScrollback() => ClearScrollbackCalls++;
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> parameters) { }
        void ITerminalHandler.OnBell() { }
        void ITerminalHandler.OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload) { }
        void ITerminalHandler.OnMoveCursor(int row, int col) { }
        void ITerminalHandler.OnCursorUp(int n) { }
        void ITerminalHandler.OnCursorDown(int n) { }
        void ITerminalHandler.OnCursorForward(int n) { }
        void ITerminalHandler.OnCursorBack(int n) { }
        void ITerminalHandler.OnEraseLine(int mode) { }
        void ITerminalHandler.OnCarriageReturn() { }
        void ITerminalHandler.OnLineFeed() { }
        void ITerminalHandler.OnSetScrollRegion(int top1Based, int bottom1Based) { }
        void ITerminalHandler.OnSetOriginMode(bool enabled) { }
        void ITerminalHandler.OnSetAlternateScreen(bool enabled) { }
        void ITerminalHandler.OnSetCursorVisibility(bool visible) => SetCursorVisibilityCalls.Add(visible);
        void ITerminalHandler.OnSaveCursor() => SaveCursorCalls++;
        void ITerminalHandler.OnRestoreCursor() => RestoreCursorCalls++;
        void ITerminalHandler.OnInsertChars(int n) { }
        void ITerminalHandler.OnDeleteChars(int n) { }
        void ITerminalHandler.OnInsertLines(int n) { }
        void ITerminalHandler.OnDeleteLines(int n) { }
        void ITerminalHandler.OnSetAutoWrap(bool enabled) { }
        void ITerminalHandler.OnSetTabStop() { }
        void ITerminalHandler.OnClearTabStop() { }
        void ITerminalHandler.OnClearAllTabStops() { }
        void ITerminalHandler.OnReverseIndex() { }
        void ITerminalHandler.OnSetBracketedPasteMode(bool enabled) { }
        void ITerminalHandler.OnDeviceStatusReport(int code) => DeviceStatusReportCodes.Add(code);
        void ITerminalHandler.OnCursorPositionReport() => CursorPositionReportCalls++;
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
        void ITerminalHandler.OnSetSynchronizedUpdate(bool enabled) { }
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) => SetMouseModeCalls.Add((mode, enabled));
        void ITerminalHandler.FlushRender() { }
    }
}
