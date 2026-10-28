using System;
using System.Text;

namespace Dotty.Terminal.Adapter;

/// <summary>How a wide glyph's trailing placeholder cell (<see cref="CellHot.IsContinuation"/>) is rendered as text.</summary>
public enum ContinuationPolicy : byte
{
    /// <summary>The placeholder contributes nothing (search / hyperlink text: "界a" stays two characters).</summary>
    SkipCell = 0,

    /// <summary>The placeholder becomes one space so string index == display column (row / scrollback / snapshot strings).</summary>
    SpaceCell = 1,
}

/// <summary>
/// The single row-to-text implementation. Every consumer that turns hot/cold cell spans into characters
/// (<see cref="Screen.GetScrollbackRow"/>, <see cref="RenderSnapshot"/> row text, <see cref="TerminalBuffer.GetRowText"/>,
/// the search engine) goes through <see cref="AppendCell"/>, so cell-to-character rules cannot drift.
/// </summary>
/// <remarks>
/// Cell rules: an empty cell (<c>Rune == 0</c>) is one space; a cell with a stored multi-codepoint grapheme
/// (<c>GraphemeIndex &gt; 0</c>) is that grapheme; an ASCII cell is its character; any other scalar is encoded to UTF-16
/// without allocating; an unrepresentable scalar (surrogate range or above U+10FFFF) is one space.
/// Trailing-blank trimming is the caller's choice: scrollback rows are cut at their end column and search trims.
/// </remarks>
public static class RowText
{
    /// <summary>
    /// Appends the text of one non-continuation cell and returns the number of UTF-16 code units written (at least 1).
    /// </summary>
    /// <param name="sb">Destination builder.</param>
    /// <param name="cell">The cell's hot data.</param>
    /// <param name="graphemeIndex">The cell's <see cref="ColdCell.GraphemeIndex"/> (use -1 when no cold data exists).</param>
    public static int AppendCell(ref ValueStringBuilder sb, in CellHot cell, short graphemeIndex)
    {
        uint rune = cell.Rune;
        if (rune == 0)
        {
            sb.Append(' ');
            return 1;
        }

        if (graphemeIndex > 0)
        {
            string? grapheme = GraphemeHelper.Resolve(rune, graphemeIndex);
            if (!string.IsNullOrEmpty(grapheme))
            {
                sb.Append(grapheme);
                return grapheme.Length;
            }
        }

        if (rune < 128)
        {
            sb.Append((char)rune);
            return 1;
        }

        if (Rune.TryCreate(rune, out Rune scalar))
        {
            Span<char> destination = sb.GetSpan(2);
            int written = scalar.EncodeToUtf16(destination);
            sb.Advance(written);
            return written;
        }

        sb.Append(' ');
        return 1;
    }

    /// <summary>
    /// Appends the text of cells <paramref name="startCol"/>..<paramref name="endCol"/> (inclusive, clamped to the row)
    /// to <paramref name="sb"/>. <paramref name="cold"/> may be shorter than <paramref name="hot"/> (or empty): missing
    /// cold cells mean "no grapheme".
    /// </summary>
    public static void Append(
        ref ValueStringBuilder sb,
        ReadOnlySpan<CellHot> hot,
        ReadOnlySpan<ColdCell> cold,
        int startCol,
        int endCol,
        ContinuationPolicy policy)
    {
        int last = Math.Min(endCol, hot.Length - 1);
        for (int col = Math.Max(0, startCol); col <= last; col++)
        {
            ref readonly CellHot cell = ref hot[col];
            if (cell.IsContinuation)
            {
                if (policy == ContinuationPolicy.SpaceCell)
                    sb.Append(' ');
                continue;
            }

            short graphemeIndex = (uint)col < (uint)cold.Length ? cold[col].GraphemeIndex : (short)-1;
            AppendCell(ref sb, in cell, graphemeIndex);
        }
    }

    /// <summary>Builds the text of cells <paramref name="startCol"/>..<paramref name="endCol"/>: one string allocation.</summary>
    public static string Build(
        ReadOnlySpan<CellHot> hot,
        ReadOnlySpan<ColdCell> cold,
        int startCol,
        int endCol,
        ContinuationPolicy policy)
    {
        int first = Math.Max(0, startCol);
        int last = Math.Min(endCol, hot.Length - 1);
        if (last < first)
            return string.Empty;

        // Not a `using` variable: C# forbids passing those by ref.
        var sb = ZStr.CreateStringBuilder(last - first + 1);
        try
        {
            Append(ref sb, hot, cold, first, last, policy);
            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }
}
