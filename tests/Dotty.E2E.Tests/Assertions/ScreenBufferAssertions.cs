using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dotty.E2E.Tests.Assertions;

/// <summary>
/// Assertions for terminal screen buffer content.
/// </summary>
public static class ScreenBufferAssertions
{
    /// <summary>
    /// Asserts that the screen contains the specified text.
    /// </summary>
    public static void ContainsText(string[] screenLines, string expectedText, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        Assert.NotNull(screenLines);
        Assert.NotEmpty(screenLines);
        
        var found = screenLines.Any(line => line.Contains(expectedText, comparison));
        Assert.True(found, $"Expected screen to contain '{expectedText}' but it was not found.\nScreen content:\n{string.Join("\n", screenLines)}");
    }
    
    /// <summary>
    /// Asserts that the screen does not contain the specified text.
    /// </summary>
    public static void DoesNotContainText(string[] screenLines, string text, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        Assert.NotNull(screenLines);
        
        var found = screenLines.Any(line => line.Contains(text, comparison));
        Assert.False(found, $"Expected screen to NOT contain '{text}' but it was found.\nScreen content:\n{string.Join("\n", screenLines)}");
    }
    
    /// <summary>
    /// Asserts that a specific line matches the expected content.
    /// </summary>
    public static void LineEquals(string[] screenLines, int lineIndex, string expectedText)
    {
        Assert.NotNull(screenLines);
        Assert.True(lineIndex >= 0 && lineIndex < screenLines.Length, 
            $"Line index {lineIndex} is out of range. Screen has {screenLines.Length} lines.");
        
        var actual = screenLines[lineIndex];
        Assert.Equal(expectedText, actual);
    }
    
    /// <summary>
    /// Asserts that a specific line contains the expected content.
    /// </summary>
    public static void LineContains(string[] screenLines, int lineIndex, string expectedText)
    {
        Assert.NotNull(screenLines);
        Assert.True(lineIndex >= 0 && lineIndex < screenLines.Length,
            $"Line index {lineIndex} is out of range. Screen has {screenLines.Length} lines.");
        
        var actual = screenLines[lineIndex];
        Assert.Contains(expectedText, actual);
    }
    
    /// <summary>
    /// Asserts that the screen has the expected number of lines.
    /// </summary>
    public static void HasLineCount(string[] screenLines, int expectedCount)
    {
        Assert.NotNull(screenLines);
        Assert.Equal(expectedCount, screenLines.Length);
    }
    
    /// <summary>
    /// Asserts that the cursor is at the expected position.
    /// </summary>
    public static void CursorAt(int actualRow, int actualCol, int expectedRow, int expectedCol)
    {
        Assert.Equal(expectedRow, actualRow);
        Assert.Equal(expectedCol, actualCol);
    }
    
    /// <summary>
    /// Asserts that the cursor is within the expected range.
    /// </summary>
    public static void CursorWithin(int actualRow, int actualCol, int minRow, int maxRow, int minCol, int maxCol)
    {
        Assert.True(actualRow >= minRow && actualRow <= maxRow,
            $"Cursor row {actualRow} is not within range [{minRow}, {maxRow}]");
        Assert.True(actualCol >= minCol && actualCol <= maxCol,
            $"Cursor column {actualCol} is not within range [{minCol}, {maxCol}]");
    }
    
    /// <summary>
    /// Asserts that the screen matches an expected pattern (wildcards allowed).
    /// </summary>
    public static void MatchesPattern(string[] screenLines, string[] expectedPatterns)
    {
        Assert.NotNull(screenLines);
        Assert.NotNull(expectedPatterns);
        
        Assert.Equal(expectedPatterns.Length, screenLines.Length);
        
        for (int i = 0; i < expectedPatterns.Length; i++)
        {
            var pattern = expectedPatterns[i];
            var actual = screenLines[i];
            
            // Support * as wildcard
            if (pattern.Contains('*'))
            {
                var parts = pattern.Split('*');
                var currentIndex = 0;
                
                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part))
                        continue;
                        
                    var foundIndex = actual.IndexOf(part, currentIndex);
                    Assert.True(foundIndex >= 0, 
                        $"Pattern part '{part}' not found in line {i}.\nExpected pattern: {pattern}\nActual: {actual}");
                    currentIndex = foundIndex + part.Length;
                }
            }
            else
            {
                Assert.Equal(pattern, actual);
            }
        }
    }
    
    /// <summary>
    /// Asserts that the scrollback buffer contains the expected content.
    /// </summary>
    public static void ScrollbackContains(List<string> scrollbackLines, string expectedText)
    {
        Assert.NotNull(scrollbackLines);
        
        var found = scrollbackLines.Any(line => line.Contains(expectedText));
        Assert.True(found, $"Expected scrollback to contain '{expectedText}' but it was not found.");
    }
    
    /// <summary>
    /// Asserts that the scrollback buffer has at least the expected number of lines.
    /// </summary>
    public static void ScrollbackHasMinimumLines(List<string> scrollbackLines, int minLines)
    {
        Assert.NotNull(scrollbackLines);
        Assert.True(scrollbackLines.Count >= minLines,
            $"Expected scrollback to have at least {minLines} lines, but has {scrollbackLines.Count}");
    }
}
