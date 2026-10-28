using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

/// <summary>
/// Canonical, implementation-independent text form of a <see cref="TerminalBuffer"/> after reflow (plan 08, WI-R1): the
/// observable contract every reflow rewrite (anchor-only mapping, rows-only fast path, streaming emit, deferred hidden-main
/// reflow) must keep. One header line, then one line per retained row of the active screen (scrollback rows have negative
/// indices) and, while the alternate screen is active, of the hidden main screen.
///
/// Deliberately robust to storage changes: cells are rendered as rune / style / flags / hyperlink URL / resolved grapheme
/// cluster, never as raw cold-cell fields or grapheme-store indices (the store is process-wide, so indices depend on test
/// order). Per-row metadata (continuation flag, <c>RowEndCol</c>, <c>RowMaxCol</c>) is part of the contract. Private
/// <see cref="TerminalBuffer"/> state that has no public accessor (wrap-pending, saved cursors, scroll counters) is read by
/// field name; a missing field renders as "null", so a rename degrades the dump instead of throwing.
///
/// Only <see cref="ReadRow"/> touches row storage. If scrollback rows stop being addressable through
/// <c>Screen.GetCellRef</c> / <c>GetColdCellRef</c> / the physical-row metadata arrays (for example when a history-row
/// reader replaces them), adapt that one method.
/// </summary>
internal static class ReflowCanonicalDump
{
    private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static object? Field(TerminalBuffer buffer, string name) =>
        typeof(TerminalBuffer).GetField(name, NonPublicInstance)?.GetValue(buffer);

    internal static string Dump(TerminalBuffer buffer)
    {
        var sb = new StringBuilder(4096);
        bool alt = buffer.IsAlternateScreenActive;
        sb.Append("buffer ").Append(buffer.Rows).Append('x').Append(buffer.Columns)
            .Append(" alt=").Append(alt ? 1 : 0)
            .Append(" scrollback=").Append(buffer.ScrollbackCount)
            .Append(" totalScrolled=").Append(Field(buffer, "_totalScrolled"))
            .Append(" savedTotalScrolled=").Append(Field(buffer, "_savedTotalScrolled"))
            .Append(" scrollRegion=").Append(Field(buffer, "_scrollTop")).Append("..").Append(Field(buffer, "_scrollBottom"))
            .Append('\n');

        int wrapPending = Field(buffer, "_cursor") is CursorController cursor ? (cursor.WrapPending ? 1 : 0) : -1;
        sb.Append("cursor row=").Append(buffer.CursorRow)
            .Append(" col=").Append(buffer.CursorCol)
            .Append(" wrapPending=").Append(wrapPending)
            .Append(" visible=").Append(buffer.CursorVisible ? 1 : 0)
            .Append('\n');
        sb.Append("savedCursor has=").Append(Field(buffer, "_hasSavedCursor"))
            .Append(' ').Append(Field(buffer, "_savedCursorState")).Append('\n');
        sb.Append("alternateSavedCursor has=").Append(Field(buffer, "_hasAlternateSavedCursor"))
            .Append(' ').Append(Field(buffer, "_alternateSavedCursorState")).Append('\n');

        foreach (var mark in buffer.GetPromptMarks())
        {
            sb.Append("promptMark absoluteRow=").Append(mark.AbsoluteRow)
                .Append(" visibleRow=").Append(buffer.GetPromptVisibleRow(mark))
                .Append(" column=").Append(mark.AbsoluteColumn)
                .Append(" kind=").Append(mark.Kind)
                .Append('\n');
        }

        AppendScreen(sb, "active", buffer.ActiveBuffer, buffer.ScrollbackCount, buffer);
        if (alt && Field(buffer, "_screens") is ScreenManager manager)
        {
            Screen main = manager.Main;
            int saved = Field(buffer, "_savedTotalScrolled") is int value ? value : 0;
            AppendScreen(sb, "main-hidden", main, Math.Clamp(saved, 0, main.ScrollbackCapacity), buffer);
        }

        return sb.ToString();
    }

    /// <summary>First 16 hex digits of the SHA-256 of <paramref name="dump"/> (golden-record key).</summary>
    internal static string Hash(string dump) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..16];

    private static void AppendScreen(StringBuilder sb, string label, Screen screen, int scrollbackRows, TerminalBuffer owner)
    {
        sb.Append("screen ").Append(label).Append(' ').Append(screen.Rows).Append('x').Append(screen.Columns)
            .Append(" retainedScrollback=").Append(scrollbackRows).Append('\n');
        for (int logicalRow = -scrollbackRows; logicalRow < screen.Rows; logicalRow++)
            ReadRow(sb, screen, logicalRow, owner);
    }

    // The single place that reads row storage (see the class remarks).
    private static void ReadRow(StringBuilder sb, Screen screen, int logicalRow, TerminalBuffer owner)
    {
        int physical = screen.GetPhysicalRow(logicalRow);
        sb.Append("row ").Append(logicalRow)
            .Append(" cont=").Append(screen.RowContinuesPrevious[physical] ? 1 : 0)
            .Append(" end=").Append(screen.RowEndCol[physical])
            .Append(" max=").Append(screen.RowMaxCol[physical])
            .Append(" :");
        for (int column = 0; column < screen.Columns; column++)
        {
            CellHot hot = screen.GetCellRef(logicalRow, column);
            ColdCell cold = screen.GetColdCellRef(logicalRow, column);
            sb.Append(' ');
            AppendCell(sb, hot, cold, owner);
        }

        sb.Append('\n');
    }

    private static void AppendCell(StringBuilder sb, CellHot hot, ColdCell cold, TerminalBuffer owner)
    {
        string? url = cold.HyperlinkId != 0 ? owner.GetHyperlinkUrl(cold.HyperlinkId) : null;
        string? cluster = cold.GraphemeIndex > 0 ? GraphemeHelper.Resolve(hot.Rune, cold.GraphemeIndex) : null;
        if (hot.Rune == 0 && hot.StyleId == 0 && hot.PackedFlags == 0 && url is null && cluster is null && cold.HyperlinkId == 0)
        {
            sb.Append('.');
            return;
        }

        sb.Append(hot.Rune.ToString("X", CultureInfo.InvariantCulture));
        if (hot.StyleId != 0)
            sb.Append('s').Append(hot.StyleId);
        if (hot.PackedFlags != 0)
            sb.Append('f').Append(hot.PackedFlags.ToString("X", CultureInfo.InvariantCulture));
        if (cold.HyperlinkId != 0)
            sb.Append("h(").Append(url ?? "?").Append(')');
        if (cluster is not null)
        {
            sb.Append("g(");
            bool first = true;
            foreach (Rune rune in cluster.EnumerateRunes())
            {
                if (!first)
                    sb.Append('.');
                sb.Append(rune.Value.ToString("X", CultureInfo.InvariantCulture));
                first = false;
            }

            sb.Append(')');
        }
    }
}
