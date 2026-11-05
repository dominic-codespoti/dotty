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

    /// <summary>
    /// Finds every match of <paramref name="query"/> in the scrollback and visible rows of <paramref name="source"/>.
    /// Scrollback rows are numbered <c>index - ScrollbackCount</c> (-1 = newest); visible rows 0..Rows-1. Scrollback is
    /// read from the source's cells (<see cref="IRenderSource.TryGetScrollbackRow"/>), so the source must have captured
    /// them: use <c>TerminalBuffer.CaptureRenderSnapshotForSearch</c>, not the per-frame visible-only capture.
    /// </summary>
    public static IReadOnlyList<SearchMatch> FindMatches(
        IRenderSource source,
        string query,
        bool regex = false,
        bool matchCase = false,
        int activeMatchIndex = -1,
        bool wholeWord = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(query)) return Array.Empty<SearchMatch>();

        var results = new List<SearchMatch>();
        var matcher = new LineMatcher(query, regex, matchCase, wholeWord, results);
        var map = new ColumnMap();
        var text = ZStr.CreateStringBuilder(notNested: false);
        try
        {
            int scrollbackCount = source.ScrollbackCount;
            for (int i = 0; i < scrollbackCount; i++)
            {
                text.Clear();
                map.Reset();
                int length;
                if (source.TryGetScrollbackRow(i, out var scrollbackHot, out var scrollbackCold))
                {
                    if (scrollbackHot.IsEmpty) continue;
                    length = BuildCellText(ref text, map, scrollbackHot, scrollbackCold, scrollbackHot.Length);
                }
                else
                {
                    // Source without scrollback cells (legacy materialized text range).
                    string lineText = source.GetScrollbackLineText(i);
                    if (string.IsNullOrEmpty(lineText)) continue;
                    BuildScrollbackText(lineText, ref text, map);
                    length = text.Length;
                }

                if (length != 0)
                    matcher.SearchLine(text.AsSpan().Slice(0, length), map, i - scrollbackCount);
            }

            int rows = source.Rows;
            int cols = source.Columns;
            for (int r = 0; r < rows; r++)
            {
                var cells = source.GetRowCells(r);
                var cold = source.GetRowColdCells(r);
                if (cells.IsEmpty) continue;

                text.Clear();
                map.Reset();
                int length = BuildCellText(ref text, map, cells, cold, Math.Min(cols, cells.Length));
                if (length != 0)
                    matcher.SearchLine(text.AsSpan().Slice(0, length), map, r);
            }
        }
        finally
        {
            text.Dispose();
            map.Dispose();
        }

        if (results.Count == 0) return Array.Empty<SearchMatch>();
        if (activeMatchIndex >= 0 && activeMatchIndex < results.Count)
        {
            var active = results[activeMatchIndex];
            results[activeMatchIndex] = new SearchMatch(active.Row, active.StartCol, active.EndCol, true);
        }
        return results;
    }

    /// <summary>
    /// True when <paramref name="pattern"/> compiles as a .NET regular expression. An invalid pattern is searched
    /// literally by <see cref="FindMatches"/>; the overlay marks it.
    /// </summary>
    public static bool IsValidRegex(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    /// <summary>
    /// Appends the searchable text of the first <paramref name="length"/> cells to <paramref name="text"/> and records, for
    /// every character, the display columns of its cell. Wide-glyph placeholder cells are skipped and trailing blanks are
    /// not searchable. Returns the searchable text length.
    /// </summary>
    private static int BuildCellText(
        ref ValueStringBuilder text,
        ColumnMap map,
        ReadOnlySpan<CellHot> cells,
        ReadOnlySpan<ColdCell> cold,
        int length)
    {
        for (int c = 0; c < length; c++)
        {
            ref readonly var hot = ref cells[c];
            if (hot.IsContinuation) continue;

            short graphemeIndex = c < cold.Length ? cold[c].GraphemeIndex : (short)-1;
            int written = RowText.AppendCell(ref text, in hot, graphemeIndex);
            map.Add(written, c, c + (hot.Rune == 0 ? 1 : Math.Max(1, (int)hot.Width)));
        }

        // Match the previous visible-row behavior: trailing blank cells are not searchable.
        ReadOnlySpan<char> built = text.AsSpan();
        int end = built.Length;
        while (end > 0 && built[end - 1] == ' ')
            end--;
        map.Truncate(end);
        return end;
    }

    /// <summary>
    /// Builds searchable text and its column map from a scrollback line materialized as a string (the legacy capture):
    /// graphemes are re-segmented and widths re-derived, and the placeholder space that follows a wide grapheme is consumed.
    /// </summary>
    private static void BuildScrollbackText(string source, ref ValueStringBuilder text, ColumnMap map)
    {
        ReadOnlySpan<char> line = source;
        int position = 0;
        while (position < line.Length)
        {
            int length = StringInfo.GetNextTextElementLength(line.Slice(position));
            ReadOnlySpan<char> grapheme = line.Slice(position, length);
            int width = GetGraphemeWidth(grapheme);
            text.Append(grapheme);
            map.Add(length, map.DisplayColumn, map.DisplayColumn + width);
            position += length;

            // Scrollback serializes the continuation cell of a wide grapheme as one
            // literal space. Consume that placeholder, but retain any following space.
            if (width == 2 && position < line.Length)
            {
                int nextLength = StringInfo.GetNextTextElementLength(line.Slice(position));
                if (nextLength == 1 && line[position] == ' ')
                    position++;
            }
        }
    }

    private static int GetGraphemeWidth(ReadOnlySpan<char> grapheme)
    {
        if (grapheme.IsEmpty) return 0;
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

    private static bool ContainsEmojiIndicator(ReadOnlySpan<char> value)
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

    /// <summary>Per-call matcher: one query, one lazily built <see cref="Regex"/>, shared result list.</summary>
    private sealed class LineMatcher
    {
        private readonly string _query;
        private readonly Regex? _regex;
        private readonly StringComparison _comparison;
        private readonly bool _wholeWord;
        private readonly List<SearchMatch> _results;

        public LineMatcher(string query, bool regex, bool matchCase, bool wholeWord, List<SearchMatch> results)
        {
            _query = query;
            _results = results;
            _wholeWord = wholeWord;
            _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (regex)
            {
                try
                {
                    var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    _regex = new Regex(query, options, RegexTimeout);
                }
                catch (ArgumentException)
                {
                    // Invalid pattern: every row falls back to a literal search.
                    _regex = null;
                }
            }
        }

        public void SearchLine(ReadOnlySpan<char> text, ColumnMap map, int row)
        {
            if (_regex is null)
            {
                SearchLiteral(text, map, row);
                return;
            }

            try
            {
                foreach (ValueMatch match in _regex.EnumerateMatches(text))
                {
                    if (match.Length > 0)
                        AddMatch(text, map, row, match.Index, match.Length);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                SearchLiteral(text, map, row);
            }
        }

        private void SearchLiteral(ReadOnlySpan<char> text, ColumnMap map, int row)
        {
            ReadOnlySpan<char> query = _query;
            if (query.Length > text.Length) return;

            int index = 0;
            while (index < text.Length)
            {
                int found = text.Slice(index).IndexOf(query, _comparison);
                if (found < 0) break;
                int foundIndex = index + found;
                AddMatch(text, map, row, foundIndex, query.Length);
                index = foundIndex + 1; // Preserve overlapping literal matches.
            }
        }

        private void AddMatch(ReadOnlySpan<char> text, ColumnMap map, int row, int index, int length)
        {
            int textLength = text.Length;
            if (_wholeWord && !IsWholeWord(text, index, length))
                return;
            int start = Math.Clamp(index, 0, textLength);
            int end = Math.Clamp(index + length, start, textLength);
            _results.Add(new SearchMatch(row, map.StartAt(start), map.EndAt(end), false));
        }

        private static bool IsWholeWord(ReadOnlySpan<char> text, int index, int length)
        {
            int end = index + length;
            return (index <= 0 || !IsWordChar(text[index - 1])) && (end >= text.Length || !IsWordChar(text[end]));
        }
    }

    /// <summary>
    /// Character-index to display-column map for one line, backed by pooled arrays reused across rows. Entry <c>i</c> of
    /// the start map is the column where character <c>i</c> begins (the column after its predecessor); entry <c>i</c> of
    /// the end map is the column where character <c>i - 1</c> ends. A match [index, index + length) therefore spans
    /// columns <c>StartAt(index)</c> to <c>EndAt(index + length)</c>.
    /// </summary>
    private sealed class ColumnMap : IDisposable
    {
        private int[] _starts = ArrayPool<int>.Shared.Rent(256);
        private int[] _ends = ArrayPool<int>.Shared.Rent(256);

        /// <summary>Characters mapped so far.</summary>
        public int Length { get; private set; }

        /// <summary>Column where the last appended unit ended.</summary>
        public int DisplayColumn { get; private set; }

        public int StartAt(int index) => _starts[index];

        public int EndAt(int index) => _ends[index];

        public void Reset()
        {
            Length = 0;
            DisplayColumn = 0;
            _starts[0] = 0;
            _ends[0] = 0;
        }

        /// <summary>Maps the next <paramref name="charCount"/> characters to the cell columns [startColumn, endColumn).</summary>
        public void Add(int charCount, int startColumn, int endColumn)
        {
            if (charCount <= 0) return;
            int first = Length + 1;
            Ensure(first + charCount);
            _starts.AsSpan(first, charCount).Fill(startColumn);
            _ends.AsSpan(first, charCount).Fill(endColumn);
            // The slot after the unit's last character starts where the unit ends.
            _starts[Length + charCount] = endColumn;
            Length += charCount;
            DisplayColumn = endColumn;
        }

        /// <summary>Drops characters past <paramref name="length"/> (trailing-blank trim).</summary>
        public void Truncate(int length)
        {
            if (length >= Length) return;
            Length = length;
            DisplayColumn = _ends[length];
        }

        private void Ensure(int required)
        {
            if (_starts.Length >= required) return;
            int size = Math.Max(required, _starts.Length * 2);
            int[] starts = ArrayPool<int>.Shared.Rent(size);
            int[] ends = ArrayPool<int>.Shared.Rent(size);
            _starts.AsSpan(0, Length + 1).CopyTo(starts);
            _ends.AsSpan(0, Length + 1).CopyTo(ends);
            ArrayPool<int>.Shared.Return(_starts);
            ArrayPool<int>.Shared.Return(_ends);
            _starts = starts;
            _ends = ends;
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_starts);
            ArrayPool<int>.Shared.Return(_ends);
            _starts = Array.Empty<int>();
            _ends = Array.Empty<int>();
        }
    }
}
