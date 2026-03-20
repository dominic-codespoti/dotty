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

    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public List<bool> SetCursorVisibilityCalls { get; } = new();
        public List<(int mode, bool enabled)> SetMouseModeCalls { get; } = new();

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
        void ITerminalHandler.OnEraseDisplay(int mode) { }
        void ITerminalHandler.OnClearScrollback() { }
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
        void ITerminalHandler.OnSaveCursor() { }
        void ITerminalHandler.OnRestoreCursor() { }
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
        void ITerminalHandler.OnSetSynchronizedUpdate(bool enabled) { }
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) => SetMouseModeCalls.Add((mode, enabled));
        void ITerminalHandler.FlushRender() { }
    }
}
