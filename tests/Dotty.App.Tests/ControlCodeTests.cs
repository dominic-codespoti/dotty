using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public class ControlCodeTests
{
    [Fact]
    public void CarriageReturnHandled()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        parser.Feed(Encoding.UTF8.GetBytes("Hello\rWorld"));
        
        Assert.Equal("HelloWorld", string.Concat(handler.PrintCalls));
        Assert.Equal(1, handler.CarriageReturnCalls);
    }
    
    [Fact]
    public void LineFeedHandled()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        parser.Feed(Encoding.UTF8.GetBytes("Line1\nLine2"));
        
        Assert.Equal("Line1Line2", string.Concat(handler.PrintCalls));
        Assert.Equal(1, handler.LineFeedCalls);
    }
    
    [Fact]
    public void TabHandled()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        parser.Feed(Encoding.UTF8.GetBytes("Col1\tCol2"));
        
        Assert.Equal("Col1Col2", string.Concat(handler.PrintCalls));
        Assert.Equal(1, handler.TabCalls);
    }
    
    [Fact]
    public void CRLFSequenceHandled()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        parser.Feed(Encoding.UTF8.GetBytes("Line1\r\nLine2\r\nLine3"));
        
        Assert.Equal("Line1Line2Line3", string.Concat(handler.PrintCalls));
        Assert.Equal(2, handler.CarriageReturnCalls);
        Assert.Equal(2, handler.LineFeedCalls);
    }
    
    [Fact]
    public void MixedControlCodesHandled()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Mix of CR, LF, and HT
        parser.Feed(Encoding.UTF8.GetBytes("Start\t\tTab\r\nNewLine\tEnd"));
        
        Assert.Equal("StartTabNewLineEnd", string.Concat(handler.PrintCalls));
        Assert.Equal(1, handler.CarriageReturnCalls);
        Assert.Equal(1, handler.LineFeedCalls);
        Assert.Equal(3, handler.TabCalls);
    }
    
    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public int CarriageReturnCalls { get; set; }
        public int LineFeedCalls { get; set; }
        public int TabCalls { get; set; }

        object? ITerminalHandler.Buffer => null;
        event System.Action<string>? ITerminalHandler.RenderRequested { add { } remove { } }

        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintCalls.Add(text.ToString());
        void ITerminalHandler.OnEraseDisplay(int mode) { }
        void ITerminalHandler.OnClearScrollback() { }
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> parameters) { }
        void ITerminalHandler.OnBell() { }
        void ITerminalHandler.OnOperatingSystemCommand(ReadOnlySpan<char> payload) { }
        void ITerminalHandler.OnMoveCursor(int row, int col) { }
        void ITerminalHandler.OnCursorUp(int n) { }
        void ITerminalHandler.OnCursorDown(int n) { }
        void ITerminalHandler.OnCursorForward(int n) { }
        void ITerminalHandler.OnCursorBack(int n) { }
        void ITerminalHandler.OnEraseLine(int mode) { }
        void ITerminalHandler.OnCarriageReturn() => CarriageReturnCalls++;
        void ITerminalHandler.OnLineFeed() => LineFeedCalls++;
        void ITerminalHandler.OnSetScrollRegion(int top1Based, int bottom1Based) { }
        void ITerminalHandler.OnSetOriginMode(bool enabled) { }
        void ITerminalHandler.OnSetAlternateScreen(bool enabled) { }
        void ITerminalHandler.OnSetCursorVisibility(bool visible) { }
        void ITerminalHandler.OnSetCursorShape(int shape) { }
        void ITerminalHandler.OnSetApplicationCursorKeys(bool enabled) { }
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
        void ITerminalHandler.OnSetBracketedPasteMode(bool enabled) { }
        void ITerminalHandler.OnDeviceStatusReport(int code) { }
        void ITerminalHandler.OnCursorPositionReport() { }
        void ITerminalHandler.OnSendDeviceAttributes(int daType) { }
        void ITerminalHandler.OnReverseIndex() { }
        void ITerminalHandler.OnCursorHorizontalAbsolute(int col) { }
        void ITerminalHandler.OnCursorVerticalAbsolute(int row) { }
        void ITerminalHandler.OnCursorNextLine(int n) { }
        void ITerminalHandler.OnCursorPreviousLine(int n) { }
        void ITerminalHandler.OnScrollUp(int n) { }
        void ITerminalHandler.OnScrollDown(int n) { }
        void ITerminalHandler.OnFullReset() { }
        void ITerminalHandler.OnRepeatCharacter(int n) { }
        void ITerminalHandler.OnTab() => TabCalls++;
        void ITerminalHandler.OnBackTab(int n) { }
        void ITerminalHandler.OnMouseEvent(int button, int col, int row, bool isPress) { }
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) { }
        void ITerminalHandler.FlushRender() { }
    }
}
