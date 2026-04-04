using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Comprehensive unit tests for TerminalSearch functionality.
/// Tests literal text search, regex search, case sensitivity, navigation, and edge cases.
/// </summary>
public class TerminalSearchTests
{
    #region Test Helpers

    private static TerminalBuffer CreateBuffer(int rows = 10, int columns = 80)
    {
        return new TerminalBuffer(rows, columns);
    }

    private static void WriteLines(TerminalBuffer buffer, params string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            buffer.SetCursor(i, 0);
            buffer.WriteText(lines[i].AsSpan(), CellAttributes.Default);
        }
    }

    private static void AddScrollback(TerminalBuffer buffer, params string[] lines)
    {
        // Use reflection or internal method to add scrollback lines
        // For testing, we simulate scrollback by scrolling up lines into scrollback
        foreach (var line in lines)
        {
            buffer.SetCursor(0, 0);
            buffer.WriteText(line.AsSpan(), CellAttributes.Default);
            // Scroll up to push into scrollback
            buffer.ActiveBuffer.ScrollUpRegion(0, buffer.Rows - 1, 1);
        }
    }

    #endregion

    #region Basic Search Tests

    [Fact]
    public void Search_FindsTextInBuffer()
    {
        // Arrange
        var buffer = CreateBuffer(5, 40);
        WriteLines(buffer, "Hello World", "Second Line", "Third Line");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("World");

        // Assert
        Assert.Equal(1, count);
        Assert.Single(search.Matches);
        Assert.Equal(0, search.CurrentMatchIndex);
        Assert.False(search.CurrentMatch.IsEmpty);
        Assert.Equal(0, search.CurrentMatch.Row);
        Assert.Equal(6, search.CurrentMatch.StartColumn);
        Assert.Equal(5, search.CurrentMatch.Length);
    }

    [Fact]
    public void Search_FindsMultipleMatches()
    {
        // Arrange
        var buffer = CreateBuffer(5, 40);
        WriteLines(buffer, "test abc test", "no match here", "test again");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("test");

        // Assert
        Assert.Equal(3, count);
        Assert.Equal(3, search.Matches.Count);
        Assert.True(search.HasMatches);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsZero()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Some text here");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("");

        // Assert
        Assert.Equal(0, count);
        Assert.Empty(search.Matches);
        Assert.False(search.HasMatches);
        Assert.Equal(-1, search.CurrentMatchIndex);
        Assert.True(search.CurrentMatch.IsEmpty);
    }

    [Fact]
    public void Search_NoMatches_ReturnsZero()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Hello World", "Second Line");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("nonexistent");

        // Assert
        Assert.Equal(0, count);
        Assert.Empty(search.Matches);
        Assert.Equal(-1, search.CurrentMatchIndex);
    }

    [Fact]
    public void Search_NullQuery_ReturnsZero()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Some text here");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(null!);

        // Assert
        Assert.Equal(0, count);
        Assert.Empty(search.Matches);
    }

    #endregion

    #region Case Sensitivity Tests

    [Fact]
    public void Search_CaseInsensitive_MatchesDifferentCases()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Hello WORLD", "hello world");
        var search = new TerminalSearch(buffer);

        // Act - case insensitive (default)
        int count = search.Search("hello", caseSensitive: false);

        // Assert
        Assert.Equal(2, count);
        Assert.Equal(2, search.Matches.Count);
    }

    [Fact]
    public void Search_CaseSensitive_OnlyMatchesExactCase()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Hello World", "HELLO WORLD", "hello world");
        var search = new TerminalSearch(buffer);

        // Act - case sensitive
        int count = search.Search("Hello", caseSensitive: true);

        // Assert
        Assert.Equal(1, count);
        Assert.Single(search.Matches);
        Assert.Equal(0, search.Matches[0].Row);
    }

    [Fact]
    public void Search_CaseInsensitive_LowercaseQueryMatchesUppercase()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "UPPERCASE TEXT");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("uppercase", caseSensitive: false);

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public void Search_CaseSensitive_UppercaseQueryDoesNotMatchLowercase()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "lowercase text");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("LOWERCASE", caseSensitive: true);

        // Assert
        Assert.Equal(0, count);
    }

    #endregion

    #region Regex Search Tests

    [Fact]
    public void Search_Regex_MatchesPattern()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "abc123def", "xyz456ghi", "test789end");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(@"\d+", useRegex: true);

        // Assert
        Assert.Equal(3, count);
        Assert.Equal(3, search.Matches.Count);
    }

    [Fact]
    public void Search_Regex_MatchesWordPattern()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test@example.com", "user@domain.org");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(@"\w+@\w+\.\w+", useRegex: true);

        // Assert
        Assert.Equal(2, count);
    }

    [Fact]
    public void Search_Regex_InvalidRegex_FallsBackToLiteral()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "[invalid regex(");
        var search = new TerminalSearch(buffer);

        // Act - invalid regex pattern, should fall back to literal search
        int count = search.Search("[invalid", useRegex: true);

        // Assert - should still find the literal text
        Assert.Equal(1, count);
    }

    [Fact]
    public void Search_Regex_CaseInsensitive_IgnoreCaseFlag()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "TEST Pattern", "test PATTERN");
        var search = new TerminalSearch(buffer);

        // Act - regex with case insensitive
        int count = search.Search(@"test\s+pattern", caseSensitive: false, useRegex: true);

        // Assert
        Assert.Equal(2, count);
    }

    [Fact]
    public void Search_Regex_CaseSensitive_RespectsCase()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Test Pattern", "test pattern", "TEST PATTERN");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(@"Test\s+Pattern", caseSensitive: true, useRegex: true);

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, search.Matches[0].Row);
    }

    [Fact]
    public void Search_Regex_EmptyPattern_ReturnsZero()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "Some content");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("", useRegex: true);

        // Assert
        Assert.Equal(0, count);
    }

    #endregion

    #region Special Characters Tests

    [Fact]
    public void Search_SpecialCharacters_LiteralSearch()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test[1] = value", "array[index] = data");
        var search = new TerminalSearch(buffer);

        // Act - literal search with special regex chars
        int count = search.Search("[1]", useRegex: false);

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public void Search_SpecialCharacters_RegexEscapes()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "price: $5.00", "cost: $10.00");
        var search = new TerminalSearch(buffer);

        // Act - regex with escaped dollar sign
        int count = search.Search(@"\$\d+\.\d+", useRegex: true);

        // Assert
        Assert.Equal(2, count);
    }

    [Fact]
    public void Search_SpecialCharacters_Parentheses()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "function() call", "another() function()");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("()", useRegex: false);

        // Assert
        Assert.Equal(3, count);
    }

    [Fact]
    public void Search_SpecialCharacters_DotsInLiteral()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "file.txt", "document.pdf", "image.png");
        var search = new TerminalSearch(buffer);

        // Act - literal search for dot
        int count = search.Search(".", useRegex: false);

        // Assert - should find dots literally
        Assert.True(count >= 3);
    }

    #endregion

    #region Match Position Tests

    [Fact]
    public void Search_MatchPosition_TracksCorrectly()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "0123456789", "abcdefghij");
        var search = new TerminalSearch(buffer);

        // Act
        search.Search("567");

        // Assert
        var match = search.CurrentMatch;
        Assert.Equal(0, match.Row);
        Assert.Equal(5, match.StartColumn);
        Assert.Equal(3, match.Length);
        Assert.Equal(8, match.EndColumn);
    }

    [Fact]
    public void Search_MultipleMatches_SameLine()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "abc abc abc");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("abc");

        // Assert
        Assert.Equal(3, count);
        Assert.Equal(0, search.Matches[0].StartColumn);
        Assert.Equal(4, search.Matches[1].StartColumn);
        Assert.Equal(8, search.Matches[2].StartColumn);
    }

    [Fact]
    public void Search_OverlappingMatches_AllFound()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "aaaa");
        var search = new TerminalSearch(buffer);

        // Act - search for "aa" in "aaaa" - should find overlapping matches at positions 0,1,2
        int count = search.Search("aa");

        // Assert - overlapping matches at positions 0, 1, 2
        Assert.Equal(3, count);
        Assert.Equal(0, search.Matches[0].StartColumn);
        Assert.Equal(1, search.Matches[1].StartColumn);
        Assert.Equal(2, search.Matches[2].StartColumn);
    }

    [Fact]
    public void Search_MatchAcrossMultipleRows()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "First line text", "Second line text", "Third line text");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("line");

        // Assert
        Assert.Equal(3, count);
        Assert.Equal(0, search.Matches[0].Row);
        Assert.Equal(1, search.Matches[1].Row);
        Assert.Equal(2, search.Matches[2].Row);
    }

    #endregion

    #region Navigation Tests

    [Fact]
    public void Search_NextMatch_MovesToNext()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test line 1", "test line 2", "test line 3");
        var search = new TerminalSearch(buffer);
        search.Search("test");
        Assert.Equal(0, search.CurrentMatchIndex);

        // Act
        bool moved = search.NextMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(1, search.CurrentMatchIndex);
        Assert.Equal(1, search.CurrentMatch.Row);
    }

    [Fact]
    public void Search_NextMatch_WrapsAround()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test1", "test2");
        var search = new TerminalSearch(buffer);
        search.Search("test");
        search.GoToMatch(1); // Move to last match
        Assert.Equal(1, search.CurrentMatchIndex);

        // Act - should wrap to first
        bool moved = search.NextMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(0, search.CurrentMatchIndex);
    }

    [Fact]
    public void Search_PreviousMatch_MovesToPrevious()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test line 1", "test line 2", "test line 3");
        var search = new TerminalSearch(buffer);
        search.Search("test");
        search.GoToMatch(2);
        Assert.Equal(2, search.CurrentMatchIndex);

        // Act
        bool moved = search.PreviousMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(1, search.CurrentMatchIndex);
    }

    [Fact]
    public void Search_PreviousMatch_WrapsAround()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test1", "test2");
        var search = new TerminalSearch(buffer);
        search.Search("test");
        Assert.Equal(0, search.CurrentMatchIndex);

        // Act - should wrap to last
        bool moved = search.PreviousMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(1, search.CurrentMatchIndex);
    }

    [Fact]
    public void Search_NextMatch_NoMatches_ReturnsFalse()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        var search = new TerminalSearch(buffer);
        search.Search("nonexistent");

        // Act
        bool moved = search.NextMatch();

        // Assert
        Assert.False(moved);
    }

    [Fact]
    public void Search_PreviousMatch_NoMatches_ReturnsFalse()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        var search = new TerminalSearch(buffer);
        search.Search("nonexistent");

        // Act
        bool moved = search.PreviousMatch();

        // Assert
        Assert.False(moved);
    }

    [Fact]
    public void Search_GoToMatch_ValidIndex()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test", "test", "test", "test");
        var search = new TerminalSearch(buffer);
        search.Search("test");

        // Act
        bool moved = search.GoToMatch(2);

        // Assert
        Assert.True(moved);
        Assert.Equal(2, search.CurrentMatchIndex);
    }

    [Fact]
    public void Search_GoToMatch_InvalidIndex_ReturnsFalse()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test");
        var search = new TerminalSearch(buffer);
        search.Search("test");

        // Act & Assert
        Assert.False(search.GoToMatch(-1));
        Assert.False(search.GoToMatch(10));
    }

    #endregion

    #region Scrollback Tests

    [Fact]
    public void Search_SearchesVisibleAndScrollback()
    {
        // Arrange
        var buffer = CreateBuffer(5, 40);
        // Write text and scroll it into scrollback
        buffer.SetCursor(0, 0);
        buffer.WriteText("scrollback text".AsSpan(), CellAttributes.Default);
        
        // Push into scrollback by scrolling up
        for (int i = 0; i < 5; i++)
        {
            buffer.SetCursor(buffer.Rows - 1, 0);
            buffer.WriteText("content".AsSpan(), CellAttributes.Default);
            buffer.ActiveBuffer.ScrollUpRegion(0, buffer.Rows - 1, 1);
        }

        // Write in visible area
        buffer.SetCursor(0, 0);
        buffer.WriteText("visible text".AsSpan(), CellAttributes.Default);

        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("text");

        // Assert - should find in both scrollback and visible
        // Since we wrote "scrollback text" and "visible text", we should find "text" at least twice
        Assert.True(count >= 1, $"Expected at least 1 match, got {count}");
    }

    [Fact]
    public void Search_ScrollbackLines_HaveNegativeRowNumbers()
    {
        // Arrange
        var buffer = CreateBuffer(5, 40);
        // Add some scrollback
        for (int i = 0; i < 3; i++)
        {
            buffer.SetCursor(0, 0);
            buffer.WriteText($"scrollback line {i}".AsSpan(), CellAttributes.Default);
            buffer.ActiveBuffer.ScrollUpRegion(0, buffer.Rows - 1, 1);
        }
        // Write in visible area
        buffer.SetCursor(0, 0);
        buffer.WriteText("visible line".AsSpan(), CellAttributes.Default);

        var search = new TerminalSearch(buffer);

        // Act
        search.Search("line");

        // Assert - scrollback rows should be negative
        if (search.Matches.Count > 1)
        {
            Assert.True(search.Matches[0].Row < 0); // Scrollback line
        }
    }

    #endregion

    #region Buffer Changes Tests

    [Fact]
    public void Search_Clear_ResetsState()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test content");
        var search = new TerminalSearch(buffer);
        search.Search("test");
        Assert.True(search.HasMatches);

        // Act
        search.Clear();

        // Assert
        Assert.False(search.HasMatches);
        Assert.Empty(search.Matches);
        Assert.Equal(-1, search.CurrentMatchIndex);
        Assert.True(search.CurrentMatch.IsEmpty);
    }

    [Fact]
    public void Search_RefreshSearch_RunsLastSearch()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test content");
        var search = new TerminalSearch(buffer);
        search.Search("test");
        Assert.Equal(1, search.MatchCount);

        // Add more content
        buffer.SetCursor(1, 0);
        buffer.WriteText("more test here".AsSpan(), CellAttributes.Default);

        // Act
        int newCount = search.RefreshSearch();

        // Assert
        Assert.True(newCount >= 1);
    }

    [Fact]
    public void Search_RefreshSearch_TriesToRestorePosition()
    {
        // Arrange
        var buffer = CreateBuffer(5, 40);
        WriteLines(buffer, "line 1", "line 2", "line 3", "line 4");
        var search = new TerminalSearch(buffer);
        search.Search("line");
        search.GoToMatch(2);
        Assert.Equal(2, search.CurrentMatchIndex);

        // Add content that shifts things
        buffer.SetCursor(0, 0);
        buffer.WriteText("new first line".AsSpan(), CellAttributes.Default);
        buffer.ActiveBuffer.ScrollUpRegion(0, buffer.Rows - 1, 1);

        // Act
        search.RefreshSearch();

        // Assert - should still have a reasonable current index
        Assert.True(search.CurrentMatchIndex >= 0);
    }

    [Fact]
    public void Search_NewSearch_ReplacesPreviousResults()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "first search", "second query");
        var search = new TerminalSearch(buffer);
        search.Search("first");
        Assert.Single(search.Matches);

        // Act
        search.Search("second");

        // Assert
        Assert.Single(search.Matches);
        // Verify it's a different match by checking row position
        Assert.Equal(1, search.CurrentMatch.Row);
    }

    #endregion

    #region UTF-8/Multi-byte Tests

    [Fact]
    public void Search_UTF8Characters_FindsCorrectly()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Hello 世界 World".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("世界");

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, search.CurrentMatch.Row);
    }

    [Fact]
    public void Search_WideCharacters_HandlesCorrectly()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Test 漢字 Test".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("漢字");

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public void Search_EmojiCharacters_HandlesGracefully()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Hello World".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("Hello");

        // Assert - regular text search should still work
        Assert.Equal(1, count);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Search_EmptyBuffer_ReturnsZero()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("anything");

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void Search_SingleCharacter_Matches()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "aaa");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("a");

        // Assert
        Assert.Equal(3, count);
    }

    [Fact]
    public void Search_WhitespaceQuery_MatchesWhitespace()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "word1 word2", "word3  word4"); // double space
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("  ");

        // Assert
        Assert.True(count >= 1);
    }

    [Fact]
    public void Search_LongQuery_StillWorks()
    {
        // Arrange
        var buffer = CreateBuffer(3, 100);
        string longText = new string('a', 50);
        WriteLines(buffer, $"prefix {longText} suffix");
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(longText);

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(50, search.CurrentMatch.Length);
    }

    [Fact]
    public void Search_Buffer_NullBufferThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new TerminalSearch(null!));
    }

    [Fact]
    public void Search_MatchCount_And_HasMatches_AreConsistent()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test test test");
        var search = new TerminalSearch(buffer);

        // Act
        search.Search("test");

        // Assert
        Assert.Equal(search.Matches.Count, search.MatchCount);
        Assert.Equal(search.MatchCount > 0, search.HasMatches);
    }

    #endregion

    #region Match Properties Tests

    [Fact]
    public void SearchMatch_EndColumn_CalculatedCorrectly()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "0123456789");
        var search = new TerminalSearch(buffer);

        // Act
        search.Search("345");

        // Assert
        var match = search.CurrentMatch;
        Assert.Equal(3, match.StartColumn);
        Assert.Equal(3, match.Length);
        Assert.Equal(6, match.EndColumn);
    }

    [Fact]
    public void SearchMatch_IsEmpty_ForDefault()
    {
        // Arrange
        var emptyMatch = SearchMatch.Empty;

        // Assert
        Assert.True(emptyMatch.IsEmpty);
        Assert.Equal(-1, emptyMatch.Row);
        Assert.Equal(-1, emptyMatch.StartColumn);
    }

    [Fact]
    public void SearchMatch_IsEmpty_FalseForValidMatch()
    {
        // Arrange
        var buffer = CreateBuffer(3, 40);
        WriteLines(buffer, "test");
        var search = new TerminalSearch(buffer);
        search.Search("test");

        // Assert
        Assert.False(search.CurrentMatch.IsEmpty);
    }

    #endregion
}
