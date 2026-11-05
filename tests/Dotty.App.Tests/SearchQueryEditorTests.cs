using System;
using System.Linq;
using Dotty.Runtime.Search;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class SearchQueryEditorTests
{
    // "e" + combining acute, a ZWJ family emoji, a flag (two regional indicators), a surrogate-pair emoji.
    private const string Combining = "e\u0301";
    private const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
    private const string Flag = "\U0001F1E9\U0001F1EA";
    private const string Grin = "\U0001F600";

    [Theory]
    [InlineData("a" + Combining + "b", new[] { 0, 1, 3, 4 })]
    [InlineData(Family + "x", new[] { 0, 8, 9 })]
    [InlineData(Flag + Grin, new[] { 0, 4, 6 })]
    [InlineData("abc", new[] { 0, 1, 2, 3 })]
    public void MoveRight_StopsOnlyOnGraphemeBoundaries(string text, int[] stops)
    {
        var editor = new SearchQueryEditor();
        editor.SetText(text);
        editor.MoveHome();
        var seen = new System.Collections.Generic.List<int> { editor.Cursor };
        while (editor.MoveRight())
            seen.Add(editor.Cursor);
        Assert.Equal(stops, seen);

        var back = new System.Collections.Generic.List<int> { editor.Cursor };
        while (editor.MoveLeft())
            back.Add(editor.Cursor);
        Assert.Equal(stops.Reverse(), back);
    }

    [Theory]
    [InlineData("x" + Family, "x")]
    [InlineData("a" + Combining, "a")]
    [InlineData(Flag + Flag, Flag)]
    [InlineData("ab", "a")]
    public void Backspace_RemovesOneWholeCluster(string text, string expected)
    {
        var editor = new SearchQueryEditor();
        editor.SetText(text);
        Assert.True(editor.Backspace());
        Assert.Equal(expected, editor.Text);
        Assert.Equal(expected.Length, editor.Cursor);
    }

    [Theory]
    [InlineData(Grin + "x", "x")]
    [InlineData(Combining + "x", "x")]
    public void Delete_RemovesOneWholeCluster(string text, string expected)
    {
        var editor = new SearchQueryEditor();
        editor.SetText(text);
        editor.MoveHome();
        Assert.True(editor.Delete());
        Assert.Equal(expected, editor.Text);
        Assert.Equal(0, editor.Cursor);
    }

    [Theory]
    [InlineData("foo bar baz", "foo bar ")]
    [InlineData("foo bar  ", "foo ")]
    public void CtrlBackspace_RemovesAWord(string text, string expected)
    {
        var editor = new SearchQueryEditor();
        editor.SetText(text);
        Assert.True(editor.Backspace(word: true));
        Assert.Equal(expected, editor.Text);
    }

    [Theory]
    [InlineData("needle", "needle")]
    [InlineData("first\nsecond", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData("a\x1b[31mb", "a [31mb")]   // PasteEncoder.Sanitize turns ESC into a space
    [InlineData("tab\there", "tabhere")]
    [InlineData("\nsecond", "")]
    [InlineData("", "")]
    public void Paste_KeepsTheFirstLineWithoutControlCharacters(string clipboard, string expected)
    {
        var editor = new SearchQueryEditor();
        editor.Paste(clipboard);
        Assert.Equal(expected, editor.Text);
        Assert.Equal(expected.Length, editor.Cursor);
    }

    [Fact]
    public void Paste_InsertsAtTheCursor()
    {
        var editor = new SearchQueryEditor();
        editor.SetText("ad");
        editor.MoveLeft();
        Assert.True(editor.Paste("bc\nignored"));
        Assert.Equal("abcd", editor.Text);
        Assert.Equal(3, editor.Cursor);
    }

    [Fact]
    public void Insert_DropsControlCharacters()
    {
        var editor = new SearchQueryEditor();
        Assert.False(editor.Insert("\x07\x1b"));
        Assert.True(editor.Insert("a\u0001b"));
        Assert.Equal("ab", editor.Text);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 11)]
    [InlineData(99, 11)]
    public void SetCursorToGrapheme_CountsClusters(int column, int expectedIndex)
    {
        var editor = new SearchQueryEditor();
        editor.SetText("a" + Combining + Family);
        editor.SetCursorToGrapheme(column);
        Assert.Equal(expectedIndex, editor.Cursor);
    }

    [Theory]
    [InlineData("cat", false, false, "0:0-3;0:4-7;0:12-15")]
    [InlineData("cat", false, true, "0:0-3;0:12-15")]
    [InlineData("Cat", true, false, "0:12-15")]
    [InlineData("c.t", false, true, "")]
    public void FindMatches_WholeWordAndCase(string query, bool matchCase, bool wholeWord, string expected)
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 30);
        buffer.SetCursor(0, 0);
        buffer.WriteText("cat cats_ x Cat".AsSpan(), CellAttributes.Default);
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        var matches = SearchEngine.FindMatches(snapshot, query, regex: false, matchCase: matchCase, wholeWord: wholeWord);
        Assert.Equal(expected, string.Join(";", matches.Select(m => $"{m.Row}:{m.StartCol}-{m.EndCol}")));
    }

    [Theory]
    [InlineData("c.t", true)]
    [InlineData("(", false)]
    [InlineData("[a-", false)]
    [InlineData("a{2}", true)]
    public void IsValidRegex(string pattern, bool valid) => Assert.Equal(valid, SearchEngine.IsValidRegex(pattern));

    [Fact]
    public void FindMatches_InvalidRegexIsSearchedLiterally()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 30);
        buffer.SetCursor(0, 0);
        buffer.WriteText("f(x) = 1".AsSpan(), CellAttributes.Default);
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        var match = Assert.Single(SearchEngine.FindMatches(snapshot, "f(", regex: true));
        Assert.Equal((0, 2), (match.StartCol, match.EndCol));
    }
}
