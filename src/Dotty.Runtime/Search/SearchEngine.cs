using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Search;

/// <summary>
/// High-performance search engine for terminal visible rows and scrollback lines.
/// Zero-allocation / low-allocation matching algorithms for literal and regex searches.
/// </summary>
public static class SearchEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Searches visible rows and scrollback lines in the render source for matches.
    /// </summary>
    /// <param name="source">The render source providing terminal cells and scrollback text.</param>
    /// <param name="query">The search query text or regular expression pattern.</param>
    /// <param name="regex">Whether to interpret the query as a regular expression.</param>
    /// <param name="matchCase">Whether search is case-sensitive.</param>
    /// <param name="activeMatchIndex">Index of the active match, if any.</param>
    /// <returns>A read-only list of search matches.</returns>
    public static IReadOnlyList<SearchMatch> FindMatches(
        IRenderSource source,
        string query,
        bool regex = false,
        bool matchCase = false,
        int activeMatchIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrEmpty(query))
        {
            return Array.Empty<SearchMatch>();
        }

        var results = new List<SearchMatch>();

        // 1. Search scrollback lines (oldest to newest: -scrollbackCount to -1)
        int scrollbackCount = source.ScrollbackCount;
        for (int i = 0; i < scrollbackCount; i++)
        {
            string lineText = source.GetScrollbackLineText(i);
            if (string.IsNullOrEmpty(lineText))
                continue;

            int row = i - scrollbackCount;
            SearchLine(lineText, row, query, regex, matchCase, results);
        }

        // 2. Search visible rows (0 to Rows - 1)
        int rows = source.Rows;
        int cols = source.Columns;

        for (int r = 0; r < rows; r++)
        {
            var cellHotSpan = source.GetRowCells(r);
            var coldSpan = source.GetRowColdCells(r);
            if (cellHotSpan.IsEmpty)
                continue;

            int lineLen = Math.Min(cols, cellHotSpan.Length);
            string lineText = BuildRowText(cellHotSpan, coldSpan, lineLen);
            if (string.IsNullOrEmpty(lineText))
                continue;

            SearchLine(lineText, r, query, regex, matchCase, results);
        }

        if (results.Count == 0)
        {
            return Array.Empty<SearchMatch>();
        }

        // Apply active flag if activeMatchIndex is valid
        if (activeMatchIndex >= 0 && activeMatchIndex < results.Count)
        {
            var active = results[activeMatchIndex];
            results[activeMatchIndex] = new SearchMatch(active.Row, active.StartCol, active.EndCol, true);
        }

        return results;
    }

    private static void SearchLine(
        string lineText,
        int row,
        string query,
        bool regex,
        bool matchCase,
        List<SearchMatch> results)
    {
        if (regex)
        {
            try
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                var matches = Regex.Matches(lineText, query, options, RegexTimeout);
                foreach (Match match in matches)
                {
                    if (match.Success && match.Length > 0)
                    {
                        results.Add(new SearchMatch(row, match.Index, match.Index + match.Length, false));
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // Fallback to literal search on invalid regex or timeout
                SearchLiteral(lineText, row, query, matchCase, results);
            }
        }
        else
        {
            SearchLiteral(lineText, row, query, matchCase, results);
        }
    }

    private static void SearchLiteral(
        string lineText,
        int row,
        string query,
        bool matchCase,
        List<SearchMatch> results)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int queryLen = query.Length;
        int index = 0;

        while (index < lineText.Length)
        {
            int foundIndex = lineText.IndexOf(query, index, comparison);
            if (foundIndex < 0)
                break;

            results.Add(new SearchMatch(row, foundIndex, foundIndex + queryLen, false));
            index = foundIndex + 1; // Allow overlapping matches
        }
    }

    private static string BuildRowText(
        ReadOnlySpan<CellHot> cells,
        ReadOnlySpan<ColdCell> cold,
        int length)
    {
        // Zero-allocation rental when possible
        char[]? rented = null;
        Span<char> charBuffer = length <= 256
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(length * 2));

        try
        {
            int charCount = 0;

            for (int c = 0; c < length; c++)
            {
                ref readonly var hot = ref cells[c];
                if (hot.IsContinuation)
                {
                    continue; // Skip wide char continuations
                }

                if (hot.Rune == 0)
                {
                    if (charCount < charBuffer.Length)
                        charBuffer[charCount++] = ' ';
                    continue;
                }

                short graphemeIdx = c < cold.Length ? cold[c].GraphemeIndex : (short)-1;
                string? grapheme = GraphemeHelper.Resolve(hot.Rune, graphemeIdx);

                if (string.IsNullOrEmpty(grapheme))
                {
                    if (charCount < charBuffer.Length)
                        charBuffer[charCount++] = ' ';
                }
                else
                {
                    for (int g = 0; g < grapheme.Length; g++)
                    {
                        if (charCount < charBuffer.Length)
                            charBuffer[charCount++] = grapheme[g];
                    }
                }
            }

            // Trim trailing spaces from row
            while (charCount > 0 && charBuffer[charCount - 1] == ' ')
            {
                charCount--;
            }

            return charCount > 0 ? new string(charBuffer[..charCount]) : string.Empty;
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
