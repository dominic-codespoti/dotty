using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Provides search functionality for TerminalBuffer.
/// Searches through both scrollback and visible buffer content.
/// </summary>
public sealed class TerminalSearch
{
    private TerminalBuffer _buffer;
    private readonly List<SearchMatch> _matches = new();
    private int _currentMatchIndex = -1;
    private string _lastQuery = string.Empty;
    private bool _lastCaseSensitive;
    private bool _lastUseRegex;

    public TerminalSearch(TerminalBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    /// <summary>
    /// Updates the buffer reference. Used when the terminal buffer changes (e.g., new session).
    /// Clears current search results as they're tied to the old buffer.
    /// </summary>
    public void SetBuffer(TerminalBuffer buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        
        // Only update if it's a different buffer
        if (_buffer != buffer)
        {
            _buffer = buffer;
            // Clear current search results since they're tied to the old buffer
            _matches.Clear();
            _currentMatchIndex = -1;
        }
    }

    /// <summary>
    /// All matches from the last search.
    /// </summary>
    public IReadOnlyList<SearchMatch> Matches => _matches;

    /// <summary>
    /// Index of the currently selected match (-1 if no search or no matches).
    /// </summary>
    public int CurrentMatchIndex => _currentMatchIndex;

    /// <summary>
    /// The currently selected match, or Empty if none.
    /// </summary>
    public SearchMatch CurrentMatch =>
        _currentMatchIndex >= 0 && _currentMatchIndex < _matches.Count
            ? _matches[_currentMatchIndex]
            : SearchMatch.Empty;

    /// <summary>
    /// Returns true if there are any matches.
    /// </summary>
    public bool HasMatches => _matches.Count > 0;

    /// <summary>
    /// Number of matches found.
    /// </summary>
    public int MatchCount => _matches.Count;

    /// <summary>
    /// Performs a search through the buffer.
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="caseSensitive">Whether to perform case-sensitive search.</param>
    /// <param name="useRegex">Whether to treat query as a regular expression.</param>
    /// <returns>Number of matches found.</returns>
    public int Search(string query, bool caseSensitive = false, bool useRegex = false)
    {
        _matches.Clear();
        _currentMatchIndex = -1;

        if (string.IsNullOrEmpty(query))
        {
            _lastQuery = string.Empty;
            return 0;
        }

        _lastQuery = query;
        _lastCaseSensitive = caseSensitive;
        _lastUseRegex = useRegex;

        // Search scrollback lines first (oldest to newest)
        int scrollbackCount = _buffer.ScrollbackCount;
        for (int i = 0; i < scrollbackCount; i++)
        {
            var line = _buffer.GetScrollbackLine(i);
            if (line.Length == 0) continue;

            SearchInLine(line.ToString(), i - scrollbackCount, caseSensitive, useRegex);
        }

        // Search visible buffer rows
        for (int row = 0; row < _buffer.Rows; row++)
        {
            var lineText = GetVisibleLineText(row);
            if (string.IsNullOrEmpty(lineText)) continue;

            SearchInLine(lineText, row, caseSensitive, useRegex);
        }

        // Select first match if any found
        if (_matches.Count > 0)
        {
            _currentMatchIndex = 0;
        }

        return _matches.Count;
    }

    /// <summary>
    /// Clears the current search results.
    /// </summary>
    public void Clear()
    {
        _matches.Clear();
        _currentMatchIndex = -1;
        _lastQuery = string.Empty;
    }

    /// <summary>
    /// Navigates to the next match.
    /// </summary>
    /// <returns>True if navigation was successful, false if no matches.</returns>
    public bool NextMatch()
    {
        if (_matches.Count == 0) return false;

        _currentMatchIndex = (_currentMatchIndex + 1) % _matches.Count;
        return true;
    }

    /// <summary>
    /// Navigates to the previous match.
    /// </summary>
    /// <returns>True if navigation was successful, false if no matches.</returns>
    public bool PreviousMatch()
    {
        if (_matches.Count == 0) return false;

        _currentMatchIndex = _currentMatchIndex <= 0
            ? _matches.Count - 1
            : _currentMatchIndex - 1;
        return true;
    }

    /// <summary>
    /// Jumps to a specific match by index.
    /// </summary>
    public bool GoToMatch(int index)
    {
        if (index < 0 || index >= _matches.Count) return false;
        _currentMatchIndex = index;
        return true;
    }

    /// <summary>
    /// Re-runs the last search (useful after buffer content changes).
    /// </summary>
    public int RefreshSearch()
    {
        if (string.IsNullOrEmpty(_lastQuery))
        {
            return 0;
        }

        // Remember current match position to try to restore it
        var currentMatch = CurrentMatch;

        int count = Search(_lastQuery, _lastCaseSensitive, _lastUseRegex);

        // Try to find and select the match closest to the previous position
        if (!currentMatch.IsEmpty && count > 0)
        {
            int closestIndex = 0;
            int closestDistance = int.MaxValue;

            for (int i = 0; i < _matches.Count; i++)
            {
                var match = _matches[i];
                int distance = Math.Abs(match.Row - currentMatch.Row) * 1000 +
                               Math.Abs(match.StartColumn - currentMatch.StartColumn);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestIndex = i;
                }
            }

            _currentMatchIndex = closestIndex;
        }

        return count;
    }

    private void SearchInLine(string line, int row, bool caseSensitive, bool useRegex)
    {
        if (string.IsNullOrEmpty(line)) return;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (useRegex)
        {
            try
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                var matches = Regex.Matches(line, _lastQuery, options);
                foreach (Match match in matches)
                {
                    if (match.Success && match.Length > 0)
                    {
                        _matches.Add(new SearchMatch(row, match.Index, match.Length));
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is RegexMatchTimeoutException)
            {
                // Invalid regex, fall back to literal search
                SearchLiteral(line, row, comparison);
            }
        }
        else
        {
            SearchLiteral(line, row, comparison);
        }
    }

    private void SearchLiteral(string line, int row, StringComparison comparison)
    {
        int index = 0;
        while (index < line.Length)
        {
            int foundIndex = line.IndexOf(_lastQuery, index, comparison);
            if (foundIndex < 0) break;

            _matches.Add(new SearchMatch(row, foundIndex, _lastQuery.Length));
            index = foundIndex + 1; // Allow overlapping matches
        }
    }

    private string GetVisibleLineText(int row)
    {
        // Use StringBuilder to properly handle multi-char graphemes
        // Skip continuation cells entirely so wide characters like "世界" 
        // appear consecutively in the output string for proper searching
        var sb = new System.Text.StringBuilder(_buffer.Columns);
        
        for (int col = 0; col < _buffer.Columns; col++)
        {
            var cell = _buffer.GetCell(row, col);
            if (cell.IsContinuation)
            {
                // Skip continuation cells - don't add anything
                // This ensures wide characters appear consecutively
                continue;
            }
            else if (string.IsNullOrEmpty(cell.Grapheme))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(cell.Grapheme);  // Append full grapheme, not just first char
            }
        }
        
        return sb.ToString();
    }
}
