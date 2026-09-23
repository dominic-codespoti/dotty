using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Scans terminal buffer rows for explicit (OSC 8) and implicit (HTTP/HTTPS/git URLs) hyperlinks,
/// and performs coordinate hit-testing.
/// </summary>
public static partial class HyperlinkScanner
{
    // Regex matching implicit web URLs (https?://[^\s()<>"]+)
    // and git SSH URLs (git@[^\s:]+:[^\s]+)
    [GeneratedRegex(@"(?:https?://[^\s()<>""']+)|(?:git@[^\s:]+:[^\s()<>""']+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>
    /// Scans a single visible row of the <see cref="IRenderSource"/> for hyperlinks,
    /// returning all explicit OSC 8 links and implicit URL matches.
    /// </summary>
    /// <param name="source">The terminal render source.</param>
    /// <param name="row">0-based visible row index.</param>
    /// <returns>A read-only list of detected <see cref="HyperlinkSpan"/> instances.</returns>
    public static IReadOnlyList<HyperlinkSpan> ScanRow(IRenderSource source, int row)
    {
        if (source == null || row < 0 || row >= source.Rows)
        {
            return Array.Empty<HyperlinkSpan>();
        }

        var results = new List<HyperlinkSpan>();
        int cols = source.Columns;
        if (cols <= 0)
        {
            return results;
        }

        var hotCells = source.GetRowCells(row);
        var coldCells = source.GetRowColdCells(row);

        // 1. Scan explicit OSC 8 hyperlinks stored in ColdCells
        if (!coldCells.IsEmpty)
        {
            ScanExplicitHyperlinks(source, row, cols, hotCells, coldCells, results);
        }

        // 2. Build row text with column mapping and scan implicit URLs via regex
        ScanImplicitHyperlinks(source, row, cols, hotCells, coldCells, results);

        return results;
    }

    /// <summary>Reusable per-thread row storage used by allocation-free hover queries.</summary>

    private sealed class HoverScratch
    {
        public char[] Text = Array.Empty<char>();
        public int[] Columns = Array.Empty<int>();
    }

    [ThreadStatic]
    private static HoverScratch? t_hoverScratch;

    /// <summary>Tests link coverage without materializing URLs or allocating per hover event.</summary>
    public static bool ContainsLinkAt(IRenderSource source, int row, int col)
    {
        if (source == null || row < 0 || row >= source.Rows || col < 0 || col >= source.Columns)
            return false;

        var hotCells = source.GetRowCells(row);
        var coldCells = source.GetRowColdCells(row);
        if (col < coldCells.Length)
        {
            ushort id = coldCells[col].HyperlinkId;
            if (id != 0 && source is TerminalBuffer buffer && !string.IsNullOrEmpty(buffer.GetHyperlinkUrl(id)))
                return true;
        }

        int colLimit = Math.Min(source.Columns, hotCells.Length);
        if (colLimit == 0)
            return false;

        var scratch = t_hoverScratch ??= new HoverScratch();
        EnsureHoverCapacity(scratch, Math.Max(16, colLimit * 2));
        int textLength = 0;
        for (int c = 0; c < colLimit; c++)
        {
            var hot = hotCells[c];
            if (hot.IsContinuation)
                continue;

            short graphemeIndex = c < coldCells.Length ? coldCells[c].GraphemeIndex : (short)-1;
            if (graphemeIndex > 0)
            {
                string? grapheme = GraphemeHelper.Resolve(hot.Rune, graphemeIndex);
                if (string.IsNullOrEmpty(grapheme))
                    AppendHoverChar(scratch, ref textLength, ' ', c);
                else
                    AppendHoverSpan(scratch, ref textLength, grapheme.AsSpan(), c);
            }
            else if (hot.Rune == 0)
            {
                AppendHoverChar(scratch, ref textLength, ' ', c);
            }
            else if (Rune.TryCreate(hot.Rune, out Rune rune))
            {
                EnsureHoverCapacity(scratch, textLength + 2);
                int written = rune.EncodeToUtf16(scratch.Text.AsSpan(textLength));
                for (int i = 0; i < written; i++)
                    scratch.Columns[textLength + i] = c;
                textLength += written;
            }
            else
            {
                AppendHoverChar(scratch, ref textLength, ' ', c);
            }
        }

        if (textLength == 0)
            return false;

        ReadOnlySpan<char> text = scratch.Text.AsSpan(0, textLength);
        foreach (var match in UrlRegex().EnumerateMatches(text))
        {
            int start = match.Index;
            int end = match.Index + match.Length;
            if (match.Length == 0 || start >= textLength || end > textLength)
                continue;

            int trimmed = GetTrailingPunctuationLength(text.Slice(start, end - start));
            end -= trimmed;
            if (end <= start)
                continue;

            int startCol = scratch.Columns[start];
            int endCol = scratch.Columns[end - 1];
            if (col < startCol || col > endCol)
                continue;
            if (OverlapsExplicitLink(source, startCol, endCol, coldCells))
                continue;
            return true;
        }
        return false;
    }

    private static bool OverlapsExplicitLink(
        IRenderSource source,
        int startCol,
        int endCol,
        ReadOnlySpan<ColdCell> coldCells)
    {
        if (source is not TerminalBuffer buffer)
            return false;
        int limit = Math.Min(endCol, coldCells.Length - 1);
        for (int col = Math.Max(0, startCol); col <= limit; col++)
        {
            ushort id = coldCells[col].HyperlinkId;
            if (id != 0 && !string.IsNullOrEmpty(buffer.GetHyperlinkUrl(id)))
                return true;
        }
        return false;
    }

    private static int GetTrailingPunctuationLength(ReadOnlySpan<char> url)
    {
        int end = url.Length;
        while (end > 0)
        {
            char c = url[end - 1];
            if (c is not ('.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}'))
                break;
            if (c == ')' && CountChar(url[..(end - 1)], '(') >= CountChar(url[..end], ')'))
                break;
            if (c == ']' && CountChar(url[..(end - 1)], '[') >= CountChar(url[..end], ']'))
                break;
            end--;
        }
        return url.Length - end;
    }

    private static void AppendHoverChar(HoverScratch scratch, ref int length, char value, int col)
    {
        EnsureHoverCapacity(scratch, length + 1);
        scratch.Text[length] = value;
        scratch.Columns[length++] = col;
    }

    private static void AppendHoverSpan(HoverScratch scratch, ref int length, ReadOnlySpan<char> value, int col)
    {
        EnsureHoverCapacity(scratch, length + value.Length);
        value.CopyTo(scratch.Text.AsSpan(length));
        scratch.Columns.AsSpan(length, value.Length).Fill(col);
        length += value.Length;
    }

    private static void EnsureHoverCapacity(HoverScratch scratch, int required)
    {
        if (scratch.Text.Length >= required)
            return;
        int size = Math.Max(required, Math.Max(32, scratch.Text.Length * 2));
        Array.Resize(ref scratch.Text, size);
        Array.Resize(ref scratch.Columns, size);
    }
    /// <summary>Returns the matching link with its URL materialized for activation.</summary>
    public static HyperlinkSpan? FindLinkAt(IRenderSource source, int row, int col)
    {
        if (source == null || row < 0 || row >= source.Rows || col < 0 || col >= source.Columns)
        {
            return null;
        }

        var spans = ScanRow(source, row);
        for (int i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (col >= span.StartCol && col <= span.EndCol)
            {
                return span;
            }
        }

        return null;
    }

    private static void ScanExplicitHyperlinks(
        IRenderSource source,
        int row,
        int cols,
        ReadOnlySpan<CellHot> hotCells,
        ReadOnlySpan<ColdCell> coldCells,
        List<HyperlinkSpan> results)
    {
        int col = 0;
        int limit = Math.Min(cols, coldCells.Length);

        while (col < limit)
        {
            ushort linkId = coldCells[col].HyperlinkId;
            if (linkId == 0)
            {
                col++;
                continue;
            }

            int startCol = col;
            while (col < limit && coldCells[col].HyperlinkId == linkId)
            {
                col++;
            }
            int endCol = col - 1;

            string? url = null;
            if (source is TerminalBuffer tb)
            {
                url = tb.GetHyperlinkUrl(linkId);
            }

            if (!string.IsNullOrEmpty(url))
            {
                results.Add(new HyperlinkSpan(row, startCol, endCol, url, linkId.ToString()));
            }
        }
    }

    private static void ScanImplicitHyperlinks(
        IRenderSource source,
        int row,
        int cols,
        ReadOnlySpan<CellHot> hotCells,
        ReadOnlySpan<ColdCell> coldCells,
        List<HyperlinkSpan> results)
    {
        var sb = new StringBuilder(cols);
        var charToCol = new List<int>(cols);

        int colLimit = Math.Min(cols, hotCells.Length);

        for (int c = 0; c < colLimit; c++)
        {
            var hot = hotCells[c];
            if (hot.IsContinuation)
            {
                continue;
            }

            short graphemeIndex = -1;
            if (c < coldCells.Length)
            {
                graphemeIndex = coldCells[c].GraphemeIndex;
            }

            string? grapheme = GraphemeHelper.Resolve(hot.Rune, graphemeIndex);
            if (string.IsNullOrEmpty(grapheme))
            {
                charToCol.Add(c);
                sb.Append(' ');
            }
            else
            {
                for (int g = 0; g < grapheme.Length; g++)
                {
                    charToCol.Add(c);
                }
                sb.Append(grapheme);
            }
        }

        string rowText = sb.ToString();
        if (string.IsNullOrWhiteSpace(rowText))
        {
            return;
        }

        var matches = UrlRegex().Matches(rowText);
        foreach (Match match in matches)
        {
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            int charStart = match.Index;
            int charEnd = match.Index + match.Length - 1;

            if (charStart >= charToCol.Count || charEnd >= charToCol.Count)
            {
                continue;
            }

            int startCol = charToCol[charStart];
            int endCol = charToCol[charEnd];
            string rawUrl = match.Value;

            // Trim common trailing punctuation that might get picked up at the end of a sentence
            string cleanedUrl = CleanTrailingPunctuation(rawUrl, out int trimmedChars);
            if (trimmedChars > 0 && charEnd - trimmedChars >= 0 && (charEnd - trimmedChars) < charToCol.Count)
            {
                endCol = charToCol[charEnd - trimmedChars];
            }

            // Check if this implicit URL range overlaps an existing explicit link
            bool overlapsExplicit = false;
            for (int i = 0; i < results.Count; i++)
            {
                var explicitLink = results[i];
                if (startCol <= explicitLink.EndCol && endCol >= explicitLink.StartCol)
                {
                    overlapsExplicit = true;
                    break;
                }
            }

            if (!overlapsExplicit && !string.IsNullOrWhiteSpace(cleanedUrl))
            {
                results.Add(new HyperlinkSpan(row, startCol, endCol, cleanedUrl, null));
            }
        }
    }

    private static string CleanTrailingPunctuation(string url, out int trimmedCount)
    {
        trimmedCount = 0;
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        int end = url.Length;
        while (end > 0)
        {
            char c = url[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}')
            {
                // If closing parenthesis/bracket, only trim if unmatched in URL
                if (c == ')' && CountChar(url.AsSpan(0, end - 1), '(') >= CountChar(url.AsSpan(0, end), ')'))
                {
                    break;
                }
                if (c == ']' && CountChar(url.AsSpan(0, end - 1), '[') >= CountChar(url.AsSpan(0, end), ']'))
                {
                    break;
                }

                end--;
                trimmedCount++;
            }
            else
            {
                break;
            }
        }

        return url[..end];
    }

    private static int CountChar(ReadOnlySpan<char> span, char target)
    {
        int count = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == target) count++;
        }
        return count;
    }
}
