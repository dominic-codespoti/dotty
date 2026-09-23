using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Search;

/// <summary>High-performance search engine for terminal visible rows and scrollback lines.</summary>
public static class SearchEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<SearchMatch> FindMatches(
        IRenderSource source,
        string query,
        bool regex = false,
        bool matchCase = false,
        int activeMatchIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(query)) return Array.Empty<SearchMatch>();

        var results = new List<SearchMatch>();
        int scrollbackCount = source.ScrollbackCount;
        for (int i = 0; i < scrollbackCount; i++)
        {
            string lineText = source.GetScrollbackLineText(i);
            if (string.IsNullOrEmpty(lineText)) continue;
            SearchLine(BuildScrollbackText(lineText), i - scrollbackCount, query, regex, matchCase, results);
        }

        int rows = source.Rows;
        int cols = source.Columns;
        for (int r = 0; r < rows; r++)
        {
            var cells = source.GetRowCells(r);
            var cold = source.GetRowColdCells(r);
            if (cells.IsEmpty) continue;
            int lineLen = Math.Min(cols, cells.Length);
            var line = BuildRowText(cells, cold, lineLen);
            if (line.Text.Length != 0)
                SearchLine(line, r, query, regex, matchCase, results);
        }

        if (results.Count == 0) return Array.Empty<SearchMatch>();
        if (activeMatchIndex >= 0 && activeMatchIndex < results.Count)
        {
            var active = results[activeMatchIndex];
            results[activeMatchIndex] = new SearchMatch(active.Row, active.StartCol, active.EndCol, true);
        }
        return results;
    }

    private static void SearchLine(
        MappedText line,
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
                foreach (Match match in Regex.Matches(line.Text, query, options, RegexTimeout))
                {
                    if (match.Success && match.Length > 0)
                        AddMatch(line, row, match.Index, match.Length, results);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                SearchLiteral(line, row, query, matchCase, results);
            }
        }
        else
        {
            SearchLiteral(line, row, query, matchCase, results);
        }
    }

    private static void SearchLiteral(
        MappedText line,
        int row,
        string query,
        bool matchCase,
        List<SearchMatch> results)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int index = 0;
        while (index < line.Text.Length)
        {
            int foundIndex = line.Text.IndexOf(query, index, comparison);
            if (foundIndex < 0) break;
            AddMatch(line, row, foundIndex, query.Length, results);
            index = foundIndex + 1; // Preserve overlapping literal matches.
        }
    }

    private static void AddMatch(MappedText line, int row, int index, int length, List<SearchMatch> results)
    {
        int start = Math.Clamp(index, 0, line.Text.Length);
        int end = Math.Clamp(index + length, start, line.Text.Length);
        results.Add(new SearchMatch(row, line.StartColumns[start], line.EndColumns[end], false));
    }

    private static MappedText BuildRowText(ReadOnlySpan<CellHot> cells, ReadOnlySpan<ColdCell> cold, int length)
    {
        var line = new MappedText();
        for (int c = 0; c < length; c++)
        {
            ref readonly var hot = ref cells[c];
            if (hot.IsContinuation) continue;

            string? grapheme = null;
            if (hot.Rune != 0)
            {
                short index = c < cold.Length ? cold[c].GraphemeIndex : (short)-1;
                grapheme = GraphemeHelper.Resolve(hot.Rune, index);
            }
            line.Append(grapheme ?? " ", c, c + (hot.Rune == 0 ? 1 : Math.Max(1, (int)hot.Width)));
        }

        // Match the previous visible-row behavior: trailing blank cells are not searchable.
        while (line.Text.Length > 0 && line.Text[^1] == ' ')
            line.RemoveLast();
        return line;
    }

    private static MappedText BuildScrollbackText(string text)
    {
        var line = new MappedText();
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        for (int i = 0; i < starts.Length; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            string grapheme = text.Substring(start, end - start);
            int width = GetGraphemeWidth(grapheme);
            line.Append(grapheme, line.DisplayColumn, line.DisplayColumn + width);

            // Scrollback serializes the continuation cell of a wide grapheme as one
            // literal space. Consume that placeholder, but retain any following space.
            if (width == 2 && i + 1 < starts.Length)
            {
                int nextStart = starts[i + 1];
                int nextEnd = i + 2 < starts.Length ? starts[i + 2] : text.Length;
                if (nextEnd - nextStart == 1 && text[nextStart] == ' ')
                    i++;
            }
        }
        return line;
    }

    private static int GetGraphemeWidth(string grapheme)
    {
        if (string.IsNullOrEmpty(grapheme)) return 0;
        var runes = grapheme.EnumerateRunes();
        if (!runes.MoveNext()) return 0;
        Rune first = runes.Current;
        UnicodeCategory category = Rune.GetUnicodeCategory(first);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Control)
            return 0;
        int value = first.Value;
        if (IsWide(value) || ContainsEmojiIndicator(grapheme)) return 2;
        return 1;
    }

    private static bool ContainsEmojiIndicator(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            int v = rune.Value;
            if ((v >= 0x1F3FB && v <= 0x1F3FF) || v == 0x200D || (v >= 0xFE00 && v <= 0xFE0F))
                return true;
        }
        return false;
    }

    private static bool IsWide(int v) =>
        (v >= 0x1100 && v <= 0x115F) || (v >= 0x231A && v <= 0x231B) ||
        (v >= 0x2E80 && v <= 0xA4CF) || (v >= 0xAC00 && v <= 0xD7A3) ||
        (v >= 0xF900 && v <= 0xFAFF) || (v >= 0xFE10 && v <= 0xFE6F) ||
        (v >= 0xFF01 && v <= 0xFF60) || (v >= 0xFFE0 && v <= 0xFFE6) ||
        (v >= 0x1F300 && v <= 0x1FAFF) || (v >= 0x20000 && v <= 0x3FFFD);

    private sealed class MappedText
    {
        private readonly StringBuilder _text = new();
        private readonly List<int> _starts = new() { 0 };
        private readonly List<int> _ends = new() { 0 };

        public string Text => _text.ToString();
        public int DisplayColumn { get; private set; }
        public int[] StartColumns => _starts.ToArray();
        public int[] EndColumns => _ends.ToArray();

        public void Append(string value, int startColumn, int endColumn)
        {
            if (value.Length == 0) return;
            _text.Append(value);
            for (int i = 0; i < value.Length; i++)
            {
                _starts.Add(startColumn);
                _ends.Add(endColumn);
            }
            _starts[^1] = endColumn;
            _ends[^1] = endColumn;
            DisplayColumn = endColumn;
        }

        public void RemoveLast()
        {
            _text.Length--;
            _starts.RemoveAt(_starts.Count - 1);
            _ends.RemoveAt(_ends.Count - 1);
            DisplayColumn = _ends[^1];
        }
    }
}
