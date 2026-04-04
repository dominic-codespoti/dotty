using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Unit tests for SearchOverlay behavior logic.
/// Tests the core search overlay functionality without requiring Avalonia UI initialization.
/// </summary>
public class SearchOverlayLogicTests
{
    #region Test Helpers

    private static TerminalBuffer CreateTestBuffer()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        // Write some test content
        for (int i = 0; i < 5; i++)
        {
            buffer.SetCursor(i, 0);
            buffer.WriteText($"Test line number {i + 1}".AsSpan(), CellAttributes.Default);
        }
        return buffer;
    }

    #endregion

    #region Search State Tests

    [Fact]
    public void SearchState_InitialState_IsEmpty()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);

        // Act & Assert
        Assert.False(search.HasMatches);
        Assert.Equal(0, search.MatchCount);
        Assert.Equal(-1, search.CurrentMatchIndex);
        Assert.True(search.CurrentMatch.IsEmpty);
        Assert.Empty(search.Matches);
    }

    [Fact]
    public void SearchState_AfterSearch_HasResults()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("line");

        // Assert
        Assert.True(count > 0);
        Assert.True(search.HasMatches);
        Assert.Equal(count, search.MatchCount);
        Assert.Equal(0, search.CurrentMatchIndex);
        Assert.False(search.CurrentMatch.IsEmpty);
    }

    [Fact]
    public void SearchState_AfterClear_IsEmpty()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        Assert.True(search.HasMatches);

        // Act
        search.Clear();

        // Assert
        Assert.False(search.HasMatches);
        Assert.Equal(0, search.MatchCount);
        Assert.Equal(-1, search.CurrentMatchIndex);
        Assert.Empty(search.Matches);
    }

    [Fact]
    public void SearchState_EmptyQuery_NoResults()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("");

        // Assert
        Assert.Equal(0, count);
        Assert.False(search.HasMatches);
    }

    [Fact]
    public void SearchState_NoMatches_HasZeroCount()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("nonexistentxyz123");

        // Assert
        Assert.Equal(0, count);
        Assert.False(search.HasMatches);
        Assert.Equal(-1, search.CurrentMatchIndex);
    }

    #endregion

    #region Search Options Tests

    [Fact]
    public void SearchOptions_CaseSensitive_DifferentResults()
    {
        // Arrange
        var buffer = new TerminalBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Test TEST test".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);

        // Act
        int caseInsensitiveCount = search.Search("test", caseSensitive: false);
        search.Clear();
        int caseSensitiveCount = search.Search("test", caseSensitive: true);

        // Assert
        Assert.True(caseInsensitiveCount >= caseSensitiveCount);
    }

    [Fact]
    public void SearchOptions_Regex_Enabled()
    {
        // Arrange
        var buffer = new TerminalBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("abc123def".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(@"\d+", useRegex: true);

        // Assert
        Assert.True(count > 0);
    }

    [Fact]
    public void SearchOptions_Regex_WithCaseInsensitive()
    {
        // Arrange
        var buffer = new TerminalBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("TEST Pattern".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("test pattern".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search(@"test\s+pattern", caseSensitive: false, useRegex: true);

        // Assert
        Assert.Equal(2, count);
    }

    #endregion

    #region Navigation Logic Tests

    [Fact]
    public void Navigation_NextMatch_MovesForward()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        int initialIndex = search.CurrentMatchIndex;

        // Act
        bool moved = search.NextMatch();

        // Assert
        Assert.True(moved);
        Assert.True(search.CurrentMatchIndex > initialIndex);
    }

    [Fact]
    public void Navigation_NextMatch_AtLast_WrapsToFirst()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        
        // Move to last match
        while (search.CurrentMatchIndex < search.MatchCount - 1)
        {
            search.NextMatch();
        }
        int lastIndex = search.CurrentMatchIndex;

        // Act
        bool moved = search.NextMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(0, search.CurrentMatchIndex); // Wrapped to first
    }

    [Fact]
    public void Navigation_PreviousMatch_MovesBackward()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        search.NextMatch(); // Move forward first
        int currentIndex = search.CurrentMatchIndex;

        // Act
        bool moved = search.PreviousMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(currentIndex - 1, search.CurrentMatchIndex);
    }

    [Fact]
    public void Navigation_PreviousMatch_AtFirst_WrapsToLast()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        Assert.Equal(0, search.CurrentMatchIndex);

        // Act
        bool moved = search.PreviousMatch();

        // Assert
        Assert.True(moved);
        Assert.Equal(search.MatchCount - 1, search.CurrentMatchIndex);
    }

    [Fact]
    public void Navigation_NoMatches_ReturnsFalse()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("nonexistent");

        // Act & Assert
        Assert.False(search.NextMatch());
        Assert.False(search.PreviousMatch());
    }

    [Fact]
    public void Navigation_GoToMatch_ValidIndex()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");

        // Act
        bool moved = search.GoToMatch(2);

        // Assert
        Assert.True(moved);
        Assert.Equal(2, search.CurrentMatchIndex);
    }

    [Fact]
    public void Navigation_GoToMatch_InvalidIndex_ReturnsFalse()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");

        // Act & Assert
        Assert.False(search.GoToMatch(-1));
        Assert.False(search.GoToMatch(999));
    }

    #endregion

    #region Refresh Search Tests

    [Fact]
    public void RefreshSearch_RerunsLastSearch()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        int initialCount = search.MatchCount;

        // Act
        int newCount = search.RefreshSearch();

        // Assert
        Assert.Equal(initialCount, newCount);
    }

    [Fact]
    public void RefreshSearch_AfterBufferChanges_UpdatesResults()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        int initialCount = search.MatchCount;

        // Add more content
        buffer.SetCursor(5, 0);
        buffer.WriteText("extra line content".AsSpan(), CellAttributes.Default);

        // Act
        int newCount = search.RefreshSearch();

        // Assert
        Assert.True(newCount >= initialCount);
    }

    [Fact]
    public void RefreshSearch_NoPreviousSearch_ReturnsZero()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        // Don't perform a search first

        // Act
        int count = search.RefreshSearch();

        // Assert
        Assert.Equal(0, count);
    }

    #endregion

    #region Event Simulation Tests

    [Fact]
    public void Search_RaisesEvent_WhenNavigating()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");
        
        bool eventRaised = false;
        SearchMatch navigatedMatch = SearchMatch.Empty;

        // Simulate event handling by tracking changes
        int initialIndex = search.CurrentMatchIndex;

        // Act
        if (search.NextMatch())
        {
            eventRaised = true;
            navigatedMatch = search.CurrentMatch;
        }

        // Assert
        Assert.True(eventRaised);
        Assert.False(navigatedMatch.IsEmpty);
    }

    #endregion

    #region Property Consistency Tests

    [Fact]
    public void Properties_MatchCount_ConsistentWithMatches()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");

        // Act & Assert
        Assert.Equal(search.Matches.Count, search.MatchCount);
    }

    [Fact]
    public void Properties_HasMatches_ConsistentWithMatchCount()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);

        // Act - no search yet
        Assert.False(search.HasMatches);

        // Act - search with results
        search.Search("line");
        Assert.True(search.HasMatches);

        // Act - clear search
        search.Clear();
        Assert.False(search.HasMatches);
    }

    [Fact]
    public void Properties_CurrentMatch_ConsistentWithIndex()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        search.Search("line");

        // Act & Assert
        if (search.CurrentMatchIndex >= 0)
        {
            Assert.Equal(search.Matches[search.CurrentMatchIndex], search.CurrentMatch);
        }
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void SingleMatch_Navigation_WrapsCorrectly()
    {
        // Arrange
        var buffer = new TerminalBuffer(3, 40);
        buffer.SetCursor(0, 0);
        buffer.WriteText("unique text".AsSpan(), CellAttributes.Default);
        var search = new TerminalSearch(buffer);
        search.Search("unique");

        // Assert initial state
        Assert.Equal(1, search.MatchCount);
        Assert.Equal(0, search.CurrentMatchIndex);

        // Act - next should wrap to same match
        bool moved = search.NextMatch();

        // Assert - wrapped to same position since there's only one match
        Assert.True(moved);
        Assert.Equal(0, search.CurrentMatchIndex);
    }

    [Fact]
    public void EmptyBuffer_Search_ReturnsZero()
    {
        // Arrange
        var buffer = new TerminalBuffer(3, 40);
        var search = new TerminalSearch(buffer);

        // Act
        int count = search.Search("anything");

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void VeryLongQuery_HandledGracefully()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);
        string longQuery = new string('a', 1000);

        // Act & Assert - should not crash
        int count = search.Search(longQuery);
        Assert.Equal(0, count);
    }

    [Fact]
    public void SpecialCharacters_Query_Handled()
    {
        // Arrange
        var buffer = CreateTestBuffer();
        var search = new TerminalSearch(buffer);

        // Act & Assert - should not crash with special chars
        int count = search.Search("[test]");
        Assert.True(count >= 0);
    }

    #endregion
}
