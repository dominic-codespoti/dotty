using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Comprehensive tests for OSC 8 Hyperlink parsing functionality.
/// Tests the BasicAnsiParser's handling of OSC 8 sequences for terminal hyperlinks.
/// </summary>
public class Osc8ParserTests
{
    #region OSC 8 Start Sequence Tests

    [Fact]
    public void ParseOsc8_StartSequence_WithUrl_ExtractsUrl()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 start: ESC ] 8 ; ; URL BEL
        var input = "\u001b]8;;https://example.com\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_StartSequence_WithSTTerminator_ExtractsUrl()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 start: ESC ] 8 ; ; URL ESC \
        var input = "\u001b]8;;https://example.com\u001b\\";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_WithIdParam_ExtractsUrlAndPreservesId()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 with id parameter: ESC ] 8 ; id=xyz ; URL BEL
        var input = "\u001b]8;id=xyz;https://example.com\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert - the URL is extracted; id is handled at adapter level
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_WithMultipleParams_ExtractsUrl()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 with multiple params: ESC ] 8 ; id=xyz:foo=bar ; URL BEL
        var input = "\u001b]8;id=xyz:foo=bar;https://example.com\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_EndSequence_StopsHyperlink()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 end: ESC ] 8 ; ; BEL
        var input = "\u001b]8;;\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert - empty URI signals end of hyperlink
        Assert.Single(handler.Osc8Calls);
        Assert.Equal(string.Empty, handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_EndSequence_WithSTTerminator()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 end: ESC ] 8 ; ; ESC \
        var input = "\u001b]8;;\u001b\\";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal(string.Empty, handler.Osc8Calls[0]);
    }

    #endregion

    #region Text with Hyperlinks Tests

    [Fact]
    public void ParseOsc8_TextAfterUrl_IsPrinted()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // OSC 8 followed by text: ESC ] 8 ; ; URL BEL text
        var input = "\u001b]8;;https://example.com\u0007Click here";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
        Assert.Single(handler.PrintCalls);
        Assert.Equal("Click here", handler.PrintCalls[0]);
    }

    [Fact]
    public void ParseOsc8_FullFlow_StartTextEnd_PrintsCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Full hyperlink flow: ESC ] 8 ; ; URL BEL text ESC ] 8 ; ; BEL
        var input = "\u001b]8;;https://example.com\u0007Click here\u001b]8;;\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Equal(2, handler.Osc8Calls.Count);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
        Assert.Equal(string.Empty, handler.Osc8Calls[1]);
        Assert.Single(handler.PrintCalls);
        Assert.Equal("Click here", handler.PrintCalls[0]);
    }

    [Fact]
    public void ParseOsc8_MultipleLinks_HandlesCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Two hyperlinks in sequence
        var input = "\u001b]8;;https://first.com\u0007First\u001b]8;;\u0007 \u001b]8;;https://second.com\u0007Second\u001b]8;;\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Equal(4, handler.Osc8Calls.Count);
        Assert.Equal("https://first.com", handler.Osc8Calls[0]);
        Assert.Equal(string.Empty, handler.Osc8Calls[1]);
        Assert.Equal("https://second.com", handler.Osc8Calls[2]);
        Assert.Equal(string.Empty, handler.Osc8Calls[3]);
    }

    #endregion

    #region Edge Cases and Malformed Sequences

    [Fact]
    public void ParseOsc8_EmptyUrl_HandlesGracefully()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Empty URL in OSC 8
        var input = "\u001b]8;;;\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal(";", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_NoSemicolon_HandlesGracefully()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Missing semicolon - should not crash
        var input = "\u001b]8https://example.com\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert - no OSC 8 call made (code would be parsed as 8https... which fails)
        Assert.Empty(handler.Osc8Calls);
    }

    [Fact]
    public void ParseOsc8_UnterminatedSequence_SavesAsLeftover()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Unterminated OSC sequence
        var input = "\u001b]8;;https://example.com";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert - no complete OSC 8 call yet
        Assert.Empty(handler.Osc8Calls);
        
        // Complete with BEL
        parser.Feed(Encoding.UTF8.GetBytes("\u0007"));
        
        // Now it should be processed
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_UnicodeUrl_ExtractsCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // URL with unicode characters
        var input = "\u001b]8;;https://example.com/\u4e2d\u6587\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert - Unicode URL should be extracted correctly
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com/\u4e2d\u6587", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_VeryLongUrl_ExtractsCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Very long URL (2000 chars)
        var longPath = new string('a', 2000);
        var input = $"\u001b]8;;https://example.com/{longPath}\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal($"https://example.com/{longPath}", handler.Osc8Calls[0]);
        // Length: "https://example.com/" (20 chars, note trailing slash) + 2000 = 2020
        Assert.Equal(2020, handler.Osc8Calls[0].Length);
    }

    [Fact]
    public void ParseOsc8_UrlWithQueryParams_ExtractsCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // URL with query parameters
        var input = "\u001b]8;;https://example.com/search?q=test&page=1\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert - OSC 8 payload ";;;" means code="" and data=";" (after first semicolon)
        // The handler extracts everything after the first semicolon
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com/search?q=test&page=1", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_UrlWithFragment_ExtractsCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // URL with fragment
        var input = "\u001b]8;;https://example.com/page#section1\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com/page#section1", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_UrlWithSpecialChars_ExtractsCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // URL with special characters that need encoding
        var input = "\u001b]8;;https://example.com/path%20with%20spaces\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com/path%20with%20spaces", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_DifferentSchemes_HandlesCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Test various URL schemes
        var inputs = new[]
        {
            "\u001b]8;;http://example.com\u0007",
            "\u001b]8;;https://example.com\u0007",
            "\u001b]8;;file:///path/to/file\u0007",
            "\u001b]8;;ftp://ftp.example.com\u0007"
        };
        
        var expected = new[]
        {
            "http://example.com",
            "https://example.com",
            "file:///path/to/file",
            "ftp://ftp.example.com"
        };
        
        // Act & Assert
        for (int i = 0; i < inputs.Length; i++)
        {
            parser.Feed(Encoding.UTF8.GetBytes(inputs[i]));
            Assert.Equal(expected[i], handler.Osc8Calls[i]);
        }
        
        Assert.Equal(4, handler.Osc8Calls.Count);
    }

    #endregion

    #region Chunked Input Tests

    [Fact]
    public void ParseOsc8_ChunkedInput_HandlesCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Send OSC 8 in chunks
        parser.Feed(Encoding.UTF8.GetBytes("\u001b]8;;"));
        Assert.Empty(handler.Osc8Calls);
        
        parser.Feed(Encoding.UTF8.GetBytes("https://"));
        Assert.Empty(handler.Osc8Calls);
        
        parser.Feed(Encoding.UTF8.GetBytes("example.com"));
        Assert.Empty(handler.Osc8Calls);
        
        parser.Feed(Encoding.UTF8.GetBytes("\u0007"));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    [Fact]
    public void ParseOsc8_ChunkedWithSTTerminator_HandlesCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Send OSC 8 with ESC \ terminator in chunks
        parser.Feed(Encoding.UTF8.GetBytes("\u001b]8;;"));
        parser.Feed(Encoding.UTF8.GetBytes("https://example.com"));
        parser.Feed(Encoding.UTF8.GetBytes("\u001b"));
        Assert.Empty(handler.Osc8Calls);
        
        parser.Feed(Encoding.UTF8.GetBytes("\\"));
        
        // Assert
        Assert.Single(handler.Osc8Calls);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
    }

    #endregion

    #region Integration with Text

    [Fact]
    public void ParseOsc8_TextBeforeAndAfter_HandlesCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Text before, hyperlink, text after
        var input = "Before \u001b]8;;https://example.com\u0007Link\u001b]8;;\u0007 After";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Equal(2, handler.Osc8Calls.Count);
        // Parser may split text into multiple print calls
        Assert.True(handler.PrintCalls.Count >= 2);
        Assert.Equal("Before ", handler.PrintCalls[0]);
        Assert.Contains("Link", handler.PrintCalls);
        // Note: " After" might be in a separate call depending on parser implementation
    }

    [Fact]
    public void ParseOsc8_MultipleLines_HandlesCorrectly()
    {
        // Arrange
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;
        
        // Hyperlink spanning lines
        var input = "\u001b]8;;https://example.com\u0007Line1\nLine2\u001b]8;;\u0007";
        
        // Act
        parser.Feed(Encoding.UTF8.GetBytes(input));
        
        // Assert
        Assert.Equal(2, handler.Osc8Calls.Count);
        Assert.Equal("https://example.com", handler.Osc8Calls[0]);
        Assert.Equal(string.Empty, handler.Osc8Calls[1]);
    }

    #endregion

    #region Helper Class

    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public List<string> Osc8Calls { get; } = new();
        public List<(int code, string payload)> OscCalls { get; } = new();

        object? ITerminalHandler.Buffer => null;
        
        event Action<string>? ITerminalHandler.RenderRequested { add { } remove { } }
        event Action<string>? ITerminalHandler.ClipboardWriteRequested { add { } remove { } }
        event Action<string>? ITerminalHandler.TitleChanged { add { } remove { } }
        event Action<string>? ITerminalHandler.LinkOpened { add { } remove { } }

        void ITerminalHandler.OnHyperlink(string uri) => Osc8Calls.Add(uri);
        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintCalls.Add(text.ToString());
        void ITerminalHandler.OnEraseDisplay(int mode) { }
        void ITerminalHandler.OnClearScrollback() { }
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> parameters) { }
        void ITerminalHandler.OnBell() { }
        
        void ITerminalHandler.OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload)
        {
            OscCalls.Add((code, payload.ToString()));
            // Simulate the OSC 8 handling
            if (code == 8)
            {
                var payloadStr = payload.ToString();
                int semiIdx = payloadStr.IndexOf(';');
                if (semiIdx >= 0)
                {
                    var uri = payloadStr.Substring(semiIdx + 1);
                    Osc8Calls.Add(uri);
                }
                else
                {
                    Osc8Calls.Add(string.Empty);
                }
            }
        }
        
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
        void ITerminalHandler.OnSetCursorVisibility(bool visible) { }
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
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) { }
        void ITerminalHandler.OnSetSynchronizedUpdate(bool enabled) { }
        void ITerminalHandler.FlushRender() { }
    }

    #endregion
}
