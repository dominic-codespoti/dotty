using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Dotty.Terminal.Adapter.Buffer;
using Dotty.Terminal.Graphics;

namespace Dotty.Terminal.Adapter;

internal sealed class BufferTextWriter
{
    private readonly CursorController _cursor;
    private readonly BufferEraser _eraser;
    private readonly TerminalBuffer _ctx;
    private readonly StyleSet _styleSet;
    private const int MaxGraphemeCacheEntries = 4096;
    // A cell's cluster (at most MaxCellChars) plus at least as many chars of the next write: enough to see every
    // continuation that still fits under the per-cell scalar cap.
    private const int JoinWindowChars = GraphemeHelper.MaxCellChars * 2;
    private static readonly ConcurrentDictionary<string, string> _graphemeCache = new();
    private int _lastDirtyRow = -1;
    // The last print wrote a non-ASCII cluster, which a following ASCII character may still continue (a Prepend
    // character such as U+0600 joins the next base). ASCII after ASCII never joins, so the ASCII paths test this only.
    private bool _clusterPending;

    public BufferTextWriter(TerminalBuffer ctx, CursorController cursor, BufferEraser eraser, StyleSet styleSet)
    {
        _ctx = ctx;
        _cursor = cursor;
        _eraser = eraser;
        _styleSet = styleSet;
    }

    public void WriteText(ReadOnlySpan<char> text, in CellAttributes attributes)
    {
        if (text.IsEmpty) return;

        ushort styleId = _styleSet.GetOrCreateId(in attributes);
        ushort hyperlinkId = attributes.HyperlinkId;
        int length = text.Length;
        int i = 0;
        // Clustering is always on (DECRQM 2027 reports "permanently set") and spans writes: a cluster that starts in
        // an earlier write (or before an ASCII run of this one) is re-joined with the cell it was written to
        // (TryJoinPrevious). A cluster segmented from this same text is already maximal, so the cluster after it is
        // not tested again.
        bool tryJoin = true;
        bool lastWasCluster = false;
        if (_clusterPending)
        {
            _clusterPending = false;
            char first = text[0];
            if (first >= 32 && first < 127 && TryJoinPrevious(text, out int joined))
            {
                i = joined;
                tryJoin = false;
                lastWasCluster = true;
            }
        }

        while (i < length)
        {
            int runStart = i;
            while (i < length)
            {
                char ch = text[i];
                if (ch < 32 || ch >= 127) break;
                i++;
            }

            if (i > runStart)
            {
                var run = text.Slice(runStart, i - runStart);
                WriteAsciiRunBulk(run, styleId, hyperlinkId);
                tryJoin = true;
                lastWasCluster = false;
                if (i >= length) break;
            }

            if (i >= length) break;

            char c = text[i];
            if (c == '\r' && i + 1 < length && text[i + 1] == '\n')
            {
                _ctx.CarriageReturn();
                _ctx.LineFeed();
                tryJoin = true;
                lastWasCluster = false;
                i += 2;
                continue;
            }

            if (c >= 127)
            {
                if (c == 127)
                {
                    _eraser.ErasePreviousGlyph(_ctx.ActiveBuffer, _cursor, _ctx.Rows, _ctx.Columns);
                    RequestMarkRowDirty(_cursor.Row);
                    tryJoin = true;
                    lastWasCluster = false;
                    i++;
                    continue;
                }

                if (tryJoin && TryJoinPrevious(text.Slice(i), out int joined))
                {
                    i += joined;
                    tryJoin = false;
                    lastWasCluster = true;
                    continue;
                }

                int len = StringInfo.GetNextTextElementLength(text.Slice(i));
                if (len > 0)
                {
                    // Cap the cluster at base + 64 combining code points; the rest is consumed but dropped.
                    int keep = len <= GraphemeHelper.MaxScalarsPerCell
                        ? len
                        : GraphemeHelper.TruncateToScalars(text.Slice(i, len), GraphemeHelper.MaxScalarsPerCell);
                    WriteGrapheme(GetCachedString(text.Slice(i, keep)), styleId, hyperlinkId);
                    tryJoin = false;
                    lastWasCluster = true;
                    i += len;
                    continue;
                }
                i++;
                continue;
            }

            switch (c)
            {
                case '\r': _ctx.CarriageReturn(); break;
                case '\n': case '\v': case '\f': _ctx.LineFeed(); break;
                case '\t': WriteTab(styleId, hyperlinkId); break;
                case '\b':
                    _eraser.ErasePreviousGlyph(_ctx.ActiveBuffer, _cursor, _ctx.Rows, _ctx.Columns);
                    RequestMarkRowDirty(_cursor.Row);
                    break;
            }
            tryJoin = true;
            lastWasCluster = false;
            i++;
        }

        _clusterPending = lastWasCluster;
    }

    /// <summary>
    /// Returns the interned string for a cluster. The cache is process-wide and keyed by arbitrary program output,
    /// so it is bounded.
    /// </summary>
    private static string GetCachedString(ReadOnlySpan<char> cluster)
    {
        var lookup = _graphemeCache.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!lookup.TryGetValue(cluster, out string? element))
        {
            if (_graphemeCache.Count > MaxGraphemeCacheEntries) _graphemeCache.Clear();
            element = cluster.ToString();
            lookup.TryAdd(cluster, element);
        }
        return element;
    }

    /// <summary>
    /// Writes a pure-ASCII byte span directly to the buffer, skipping all
    /// control-character / non-ASCII / grapheme dispatch that WriteText performs.
    /// The parser already validated every byte is in [0x20, 0x7E]. Only the
    /// first byte can continue a cluster left open by the previous write (after
    /// a Prepend character): ASCII never extends an ASCII base.
    /// </summary>
    internal void WriteAscii(ReadOnlySpan<byte> text, in CellAttributes attributes)
    {
        if (text.IsEmpty) return;

        if (_clusterPending)
        {
            _clusterPending = false;
            char first = (char)text[0];
            if (TryJoinPrevious(new ReadOnlySpan<char>(in first), out _))
            {
                text = text.Slice(1);
                if (text.IsEmpty) return;
            }
        }

        ushort styleId = _styleSet.GetOrCreateId(in attributes);
        ushort hyperlinkId = attributes.HyperlinkId;
        WriteAsciiRunBulk(text, styleId, hyperlinkId);
    }

    private static void ApplyCellTemplate(ref CellHot cell, ushort styleId)
    {
        cell.StyleId = styleId;
        cell.PackedFlags = 0;
    }

    private unsafe void WriteAsciiRunBulk(ReadOnlySpan<char> run, ushort styleId, ushort hyperlinkId)
    {
        if (run.IsEmpty) return;

        int remaining = run.Length;
        int offset = 0;

        while (remaining > 0)
        {
            int row = _cursor.Row;
            int col = _cursor.Col;
            int cols = _ctx.Columns;
            bool consumedWrap = false;

            if (_ctx._autoWrap && _cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
                consumedWrap = true;
                row = _cursor.Row;
                col = 0;
            }

            if (col < 0 || col >= cols) return;

            int spaceOnRow = cols - col;
            int chunkLen = Math.Min(remaining, spaceOnRow);

            var buf = _ctx.ActiveBuffer;
            int rowMapIdx = buf.GetPhysicalRow(row);
            int baseIdx = rowMapIdx * cols + col;
            int endCol = col + chunkLen - 1;
            ref CellHot cellData = ref Unsafe.AsRef<CellHot>((void*)buf.CellsPtr);
            ref ColdCell coldData = ref Unsafe.AsRef<ColdCell>((void*)buf.ColdCellsPtr);

            // Check first cell for continuation/width (rare, only at row start)
            {
                ref CellHot first = ref Unsafe.Add(ref cellData, baseIdx);
                if (first.IsContinuation || first.Width > 1)
                {
                    buf.ClearCell(row, col);
                }
            }

            int i = 0;
            uint styleUint = styleId; // StyleId in low 16 bits, PackedFlags=0 in byte 2, _pad=0 in byte 3

            // AVX2: process 4 cells per vector store (32 bytes = 4 × 8-byte CellHot)
            if (Avx2.IsSupported)
            {
                int vecLimit = chunkLen - 3;
                for (; i < vecLimit; i += 4)
                {
                    var v = Vector256.Create(
                        (uint)run[offset + i], styleUint,
                        (uint)run[offset + i + 1], styleUint,
                        (uint)run[offset + i + 2], styleUint,
                        (uint)run[offset + i + 3], styleUint);
                    ref byte dst = ref Unsafe.As<CellHot, byte>(
                        ref Unsafe.Add(ref cellData, baseIdx + i));
                    Unsafe.WriteUnaligned(ref dst, v);
                }
            }

            // Process remaining cells (2 at a time)
            for (; i < chunkLen; i += 2)
            {
                int left = chunkLen - i;
                if (left >= 2)
                {
                    Unsafe.WriteUnaligned(
                        ref Unsafe.As<CellHot, byte>(ref Unsafe.Add(ref cellData, baseIdx + i)),
                        (uint)run[offset + i] | ((ulong)styleUint << 32));
                    Unsafe.WriteUnaligned(
                        ref Unsafe.As<CellHot, byte>(ref Unsafe.Add(ref cellData, baseIdx + i + 1)),
                        (uint)run[offset + i + 1] | ((ulong)styleUint << 32));
                }
                else
                {
                    ref CellHot cell = ref Unsafe.Add(ref cellData, baseIdx + i);
                    ApplyCellTemplate(ref cell, styleId);
                    cell.Rune = run[offset + i];
                }
            }

            // Reset cold cells for the chunk (skip when row has no hyperlinks/graphemes).
            if (buf.RowColdFlags[rowMapIdx])
            {
                for (int j = 0; j < chunkLen; j++)
                {
                    ref var cold = ref Unsafe.Add(ref coldData, baseIdx + j);
                    if (cold.HyperlinkId != 0 || cold.GraphemeIndex != -1)
                        cold.Reset();
                }
            }

            // If the previous contents ended with a wide glyph that extended past
            // this ASCII overwrite, clear its trailing continuation cells so stale
            // fragments do not survive at the right edge of the rewritten run.
            int trailingCol = endCol + 1;
            while (trailingCol < cols)
            {
                ref var trailing = ref buf.GetCellRef(row, trailingCol);
                if (!trailing.IsContinuation)
                    break;

                trailing.Reset();
                buf.GetColdCellRef(row, trailingCol).Reset();
                trailingCol++;
            }

            // Append fast path: when the run reaches or passes the row's
            // current max column, endCol IS the new upper bound — no backward
            // scan. The scan only runs when this write shrank the content
            // (overwrite past previous end), which floods never hit. The
            // scan+update pair measured 139ms per 500K-line flood.
            if (endCol < buf.GetRowMaxCol(row))
                buf.RecalculateRowMaxCol(row);

            if (hyperlinkId != 0)
            {
                for (int j = 0; j < chunkLen; j++)
                    buf.SetColdHyperlink(row, col + j, hyperlinkId);
            }

            buf.UpdateRowMaxCol(row, endCol);
            buf.MarkRowEnd(row, endCol);
            if (consumedWrap)
                buf.SetRowContinuation(row, true);
            RequestMarkRowDirty(row);

            remaining -= chunkLen;
            offset += chunkLen;

            if (remaining > 0)
            {
                _cursor.Set(row, cols - 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                if (_ctx._autoWrap && endCol >= cols - 1)
                {
                    _cursor.Set(row, cols - 1, _ctx.Rows, cols);
                    _cursor.SetWrapPending(true);
                }
                else
                {
                    _cursor.Set(row, endCol + 1, _ctx.Rows, cols);
                    _cursor.SetWrapPending(false);
                }
            }
        }
    }

    // Byte-based overload: for pure-ASCII calls from TerminalAdapter.OnPrintAscii.
    // The body is identical to the char overload because (uint)byte and (uint)char
    // produce the same value for ASCII and both index identically.
    private unsafe void WriteAsciiRunBulk(ReadOnlySpan<byte> run, ushort styleId, ushort hyperlinkId)
    {
        if (run.IsEmpty) return;

        int remaining = run.Length;
        int offset = 0;

        while (remaining > 0)
        {
            int row = _cursor.Row;
            int col = _cursor.Col;
            int cols = _ctx.Columns;
            bool consumedWrap = false;

            if (_ctx._autoWrap && _cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
                consumedWrap = true;
                row = _cursor.Row;
                col = 0;
            }

            if (col < 0 || col >= cols) return;

            int spaceOnRow = cols - col;
            int chunkLen = Math.Min(remaining, spaceOnRow);

            var buf = _ctx.ActiveBuffer;
            int rowMapIdx = buf.GetPhysicalRow(row);
            int baseIdx = rowMapIdx * cols + col;
            int endCol = col + chunkLen - 1;
            ref CellHot cellData = ref Unsafe.AsRef<CellHot>((void*)buf.CellsPtr);
            ref ColdCell coldData = ref Unsafe.AsRef<ColdCell>((void*)buf.ColdCellsPtr);

            // Check first cell for continuation/width (rare, only at row start)
            {
                ref CellHot first = ref Unsafe.Add(ref cellData, baseIdx);
                if (first.IsContinuation || first.Width > 1)
                {
                    buf.ClearCell(row, col);
                }
            }

            int i = 0;
            uint styleUint = styleId;

            // AVX2: process 4 cells per vector store
            if (Avx2.IsSupported)
            {
                int vecLimit = chunkLen - 3;
                for (; i < vecLimit; i += 4)
                {
                    var v = Vector256.Create(
                        (uint)run[offset + i], styleUint,
                        (uint)run[offset + i + 1], styleUint,
                        (uint)run[offset + i + 2], styleUint,
                        (uint)run[offset + i + 3], styleUint);
                    ref byte dst = ref Unsafe.As<CellHot, byte>(
                        ref Unsafe.Add(ref cellData, baseIdx + i));
                    Unsafe.WriteUnaligned(ref dst, v);
                }
            }

            // Process remaining cells (2 at a time)
            for (; i < chunkLen; i += 2)
            {
                int left = chunkLen - i;
                if (left >= 2)
                {
                    Unsafe.WriteUnaligned(
                        ref Unsafe.As<CellHot, byte>(ref Unsafe.Add(ref cellData, baseIdx + i)),
                        (uint)run[offset + i] | ((ulong)styleUint << 32));
                    Unsafe.WriteUnaligned(
                        ref Unsafe.As<CellHot, byte>(ref Unsafe.Add(ref cellData, baseIdx + i + 1)),
                        (uint)run[offset + i + 1] | ((ulong)styleUint << 32));
                }
                else
                {
                    ref CellHot cell = ref Unsafe.Add(ref cellData, baseIdx + i);
                    ApplyCellTemplate(ref cell, styleId);
                    cell.Rune = run[offset + i];
                }
            }

            // Reset cold cells for the chunk (skip when row has no hyperlinks/graphemes).
            if (buf.RowColdFlags[rowMapIdx])
            {
                for (int j = 0; j < chunkLen; j++)
                {
                    ref var cold = ref Unsafe.Add(ref coldData, baseIdx + j);
                    if (cold.HyperlinkId != 0 || cold.GraphemeIndex != -1)
                        cold.Reset();
                }
            }

            // Clear trailing continuation cells from previous content
            int trailingCol = endCol + 1;
            while (trailingCol < cols)
            {
                ref var trailing = ref buf.GetCellRef(row, trailingCol);
                if (!trailing.IsContinuation)
                    break;

                trailing.Reset();
                buf.GetColdCellRef(row, trailingCol).Reset();
                trailingCol++;
            }

            // Append fast path: when the run reaches or passes the row's
            // current max column, endCol IS the new upper bound — no backward
            // scan. The scan only runs when this write shrank the content
            // (overwrite past previous end), which floods never hit. The
            // scan+update pair measured 139ms per 500K-line flood.
            if (endCol < buf.GetRowMaxCol(row))
                buf.RecalculateRowMaxCol(row);

            if (hyperlinkId != 0)
            {
                for (int j = 0; j < chunkLen; j++)
                    buf.SetColdHyperlink(row, col + j, hyperlinkId);
            }

            buf.UpdateRowMaxCol(row, endCol);
            buf.MarkRowEnd(row, endCol);
            if (consumedWrap)
                buf.SetRowContinuation(row, true);
            RequestMarkRowDirty(row);

            remaining -= chunkLen;
            offset += chunkLen;

            if (remaining > 0)
            {
                _cursor.Set(row, cols - 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                if (_ctx._autoWrap && endCol >= cols - 1)
                {
                    _cursor.Set(row, cols - 1, _ctx.Rows, cols);
                    _cursor.SetWrapPending(true);
                }
                else
                {
                    _cursor.Set(row, endCol + 1, _ctx.Rows, cols);
                    _cursor.SetWrapPending(false);
                }
            }
        }
    }

    private void WriteTab(ushort styleId, ushort hyperlinkId)
    {
        int cols = _ctx.Columns;
        int current = _cursor.Col;
        int target = _ctx.GetNextTabStopFrom(current);
        if (target <= current) target = Math.Min(cols - 1, current + 1);
        int spaces = target - current;
        for (int i = 0; i < spaces; i++)
        {
            WriteGraphemeAscii(' ', styleId, hyperlinkId);
        }
    }

    private void WriteGraphemeAscii(char ch, ushort styleId, ushort hyperlinkId)
    {
        bool autoWrap = _ctx._autoWrap;
        bool consumedWrap = false;
        int startCol;
        if (autoWrap)
        {
            if (_cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
                consumedWrap = true;
            }
            _cursor.EnsureSpace(1, _ctx.Rows, _ctx.Columns);
            startCol = _cursor.Col;
        }
        else
        {
            int cols = _ctx.Columns;
            startCol = _cursor.Col;
            if (startCol > cols - 1) startCol = Math.Max(0, cols - 1);
            _cursor.Set(_cursor.Row, startCol, _ctx.Rows, cols);
        }

        var buf = _ctx.ActiveBuffer;
        int currentRow = _cursor.Row;

        ref var cell = ref buf.GetCellRef(currentRow, startCol);

        if (cell.IsContinuation || cell.Width > 1)
        {
            buf.ClearCell(currentRow, startCol);
            cell = ref buf.GetCellRef(currentRow, startCol);
        }

        bool hadCold = cell.HasHyperlink || cell.HasGrapheme || cell.Rune == PlaceholderTable.PlaceholderRune;
        ApplyCellTemplate(ref cell, styleId);
        cell.Rune = ch;

        if (hadCold)
        {
            buf.GetColdCellRef(currentRow, startCol).Reset();
        }

        if (hyperlinkId != 0)
        {
            buf.SetColdHyperlink(currentRow, startCol, hyperlinkId);
        }

        buf.UpdateRowMaxCol(currentRow, startCol);
        buf.MarkRowEnd(currentRow, startCol);
        if (consumedWrap)
            buf.SetRowContinuation(currentRow, true);

        if (autoWrap)
        {
            int cols = _ctx.Columns;
            if (startCol >= cols - 1)
            {
                _cursor.Set(currentRow, Math.Min(cols - 1, startCol), _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                _cursor.Set(currentRow, startCol + 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(false);
            }
        }
        else
        {
            int cols = _ctx.Columns;
            _cursor.Set(currentRow, Math.Min(startCol + 1, cols - 1), _ctx.Rows, cols);
            _cursor.SetWrapPending(false);
        }
        RequestMarkRowDirty(currentRow);
    }

    private void WriteGrapheme(string grapheme, ushort styleId, ushort hyperlinkId)
    {
        if (string.IsNullOrEmpty(grapheme)) return;
        // A Unicode-placeholder cluster (U+10EEEE + diacritics) is always one cell wide and never enters the grapheme store.
        bool placeholder = PlaceholderTable.IsPlaceholderCluster(grapheme);
        int width = placeholder ? 1 : UnicodeWidth.GetWidth(grapheme);
        if (width == 0)
        {
            if (AttachCombiningMark(grapheme)) return;
            width = 1;
        }

        PlaceCluster(grapheme, placeholder, width, styleId, hyperlinkId);
    }

    /// <summary>
    /// Writes one cluster of <paramref name="width"/> cells at the cursor (wrapping first when autowrap is on and
    /// it does not fit) and advances the cursor past it.
    /// </summary>
    private void PlaceCluster(string grapheme, bool placeholder, int width, ushort styleId, ushort hyperlinkId)
    {
        int columns = _ctx.Columns;
        if (width > columns) width = columns;
        bool autoWrap = _ctx._autoWrap;
        bool consumedWrap = false;
        int startCol;
        if (autoWrap)
        {
            if (_cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
                consumedWrap = true;
            }
            if (_cursor.Col > columns - width)
            {
                // A wide cluster that does not fit before the right edge leaves the last column blank and moves to
                // the next line; LineFeed scrolls at the bottom of the scroll region.
                _ctx.LineFeed();
                _ctx.CarriageReturn();
            }
            startCol = _cursor.Col;
        }
        else
        {
            startCol = _cursor.Col;
            if (startCol > columns - width) startCol = Math.Max(0, columns - width);
            _cursor.Set(_cursor.Row, startCol, _ctx.Rows, columns);
        }

        var buf = _ctx.ActiveBuffer;
        int currentRow = _cursor.Row;
        // Overwriting cells never changes whether this row soft-wraps from the previous one, but clearing does.
        bool continuesPrevious = consumedWrap || buf.GetRowContinuesPrevious(currentRow);
        // Clear every covered cell, so a wide glyph partly overwritten here is removed whole.
        buf.ClearColumns(currentRow, startCol, startCol + width);

        uint rune;
        try { rune = (uint)char.ConvertToUtf32(grapheme, 0); }
        catch (ArgumentException) { rune = 0; }

        ref var cell = ref buf.GetCellRef(_cursor.Row, startCol);
        cell.Rune = rune;
        cell.StyleId = styleId;
        cell.Width = (byte)Math.Clamp(width, 1, 2);
        cell.IsContinuation = false;
        cell.HasHyperlink = hyperlinkId != 0;
        cell.HasGrapheme = !placeholder && grapheme.Length > 1;

        if (hyperlinkId != 0)
        {
            buf.SetColdHyperlink(currentRow, startCol, hyperlinkId);
        }

        if (placeholder)
        {
            short code = PlaceholderTable.Encode(grapheme.AsSpan(2));
            if (code != -1) buf.SetColdGraphemeIndex(currentRow, startCol, code);
        }
        else if (grapheme.Length > 1)
        {
            buf.SetColdGraphemeIndex(currentRow, startCol, GraphemeHelper.StoreGrapheme(grapheme));
        }

        for (int i = 1; i < width; i++)
        {
            ref var cont = ref buf.GetCellRef(_cursor.Row, startCol + i);
            cont.Reset();
            cont.IsContinuation = true;
            cont.StyleId = styleId;
            // The cell may have held a placeholder code (or other cold data); a continuation never keeps it.
            buf.GetColdCellRef(_cursor.Row, startCol + i).Reset();
            if (hyperlinkId != 0)
            {
                cont.HasHyperlink = true;
                buf.SetColdHyperlink(_cursor.Row, startCol + i, hyperlinkId);
            }
        }

        buf.UpdateRowMaxCol(currentRow, startCol + width - 1);
        buf.MarkRowEnd(currentRow, startCol + width - 1);
        if (continuesPrevious)
            buf.SetRowContinuation(currentRow, true);

        if (autoWrap)
        {
            int cols = _ctx.Columns;
            int endCol = startCol + width - 1;
            if (endCol >= cols - 1)
            {
                _cursor.Set(_cursor.Row, Math.Min(cols - 1, endCol), _ctx.Rows, cols);
                _cursor.SetWrapPending(true);
            }
            else
            {
                _cursor.Set(_cursor.Row, endCol + 1, _ctx.Rows, cols);
                _cursor.SetWrapPending(false);
            }
        }
        else
        {
            int cols = _ctx.Columns;
            _cursor.Set(_cursor.Row, Math.Min(_cursor.Col + width, cols - 1), _ctx.Rows, cols);
            _cursor.SetWrapPending(false);
        }
        RequestMarkRowDirty(currentRow);
    }

    /// <summary>
    /// Re-joins a grapheme cluster split across writes. The base is the cell before the cursor on the cursor row (the
    /// cursor cell itself while a wrap is pending), as in Ghostty; it never reaches into the previous row. When that
    /// cell's cluster followed by the start of <paramref name="text"/> segments (UAX #29, .NET
    /// <see cref="StringInfo"/>) as one cluster, the continuation is appended (up to
    /// <see cref="GraphemeHelper.MaxScalarsPerCell"/> scalars; the rest is consumed and dropped) and the cell is
    /// re-measured: a late U+FE0F widens it in place, or, at the right edge with autowrap on, moves it to the next
    /// line; a late U+FE0E narrows it. Returns false, consuming nothing, when <paramref name="text"/> starts a new
    /// cluster.
    /// </summary>
    private bool TryJoinPrevious(ReadOnlySpan<char> text, out int consumed)
    {
        consumed = 0;
        int columns = _ctx.Columns;
        int row = _cursor.Row;
        int col = Math.Min(_cursor.Col, columns - 1);
        if (!_cursor.WrapPending)
        {
            if (col == 0) return false;
            col--;
        }

        var buf = _ctx.ActiveBuffer;
        while (col > 0 && buf.GetCellRef(row, col).IsContinuation) col--;
        ref var cell = ref buf.GetCellRef(row, col);
        // Unicode-placeholder diacritics are decoded by AttachCombiningMark, not appended as text.
        if (cell.IsEmpty || cell.IsContinuation || cell.Rune == PlaceholderTable.PlaceholderRune) return false;

        var cold = buf.GetColdCell(row, col);
        string? previous = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        if (string.IsNullOrEmpty(previous) || previous.Length > GraphemeHelper.MaxCellChars) return false;

        Span<char> window = stackalloc char[JoinWindowChars];
        int take = Math.Min(text.Length, window.Length - previous.Length);
        if (take < text.Length && char.IsHighSurrogate(text[take - 1])) take--;
        previous.AsSpan().CopyTo(window);
        text[..take].CopyTo(window[previous.Length..]);
        int extension = StringInfo.GetNextTextElementLength(window[..(previous.Length + take)]) - previous.Length;
        if (extension <= 0) return false;
        consumed = extension;

        int room = GraphemeHelper.MaxScalarsPerCell - GraphemeHelper.CountScalars(previous);
        if (room <= 0) return true;
        int keep = GraphemeHelper.TruncateToScalars(text[..extension], room);
        ReadOnlySpan<char> joined = window[..(previous.Length + keep)];

        int oldWidth = cell.Width;
        int newWidth = Math.Clamp(UnicodeWidth.GetWidth(joined), 1, 2);
        bool fits = newWidth <= columns && (_ctx._autoWrap || col + newWidth <= columns);
        if (newWidth != oldWidth && (newWidth < oldWidth || fits))
        {
            ushort styleId = cell.StyleId;
            // Rewrite the cluster in place: clear its cells, put the cursor back on it and print it at its new width.
            bool continuesPrevious = buf.GetRowContinuesPrevious(row);
            buf.ClearColumns(row, col, col + oldWidth);
            if (continuesPrevious) buf.SetRowContinuation(row, true);
            RequestMarkRowDirty(row);
            _cursor.Set(row, col, _ctx.Rows, columns);
            _cursor.SetWrapPending(false);
            PlaceCluster(GetCachedString(joined), placeholder: false, newWidth, styleId, cold.HyperlinkId);
            return true;
        }

        // Same width (or a widening that cannot fit): keep the cell and store the longer cluster. A full grapheme
        // store (index <= 0) keeps the cell as it was.
        short index = GraphemeHelper.StoreGrapheme(joined);
        if (index > 0)
        {
            buf.SetColdGraphemeIndex(row, col, index);
            RequestMarkRowDirty(row);
        }
        return true;
    }

    /// <summary>
    /// Appends a zero-width cluster that does not continue the previous cluster (a format character such as U+200B,
    /// or a mark with no base on the cursor row) to the previous non-empty cell, or completes a Unicode placeholder.
    /// </summary>
    private bool AttachCombiningMark(string mark)
    {
        var (row, col) = GetPreviousBaseCell();
        if (row < 0) return false;
        var buf = _ctx.ActiveBuffer;
        ref var cell = ref buf.GetCellRef(row, col);
        var cold = buf.GetColdCell(row, col);
        if (cell.Rune == PlaceholderTable.PlaceholderRune)
        {
            // Diacritics arriving after the placeholder (e.g. in a later OnPrint call) complete its row/column/msb;
            // any other mark is consumed and dropped.
            short current = cold.GraphemeIndex;
            short updated = PlaceholderTable.Append(current, mark);
            if (updated != current)
            {
                buf.SetColdGraphemeIndex(row, col, updated);
                RequestMarkRowDirty(row);
            }
            return true;
        }

        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        if (string.IsNullOrEmpty(grapheme)) return false;

        // A cell holds at most base + 64 combining code points; further marks are consumed and dropped.
        int room = GraphemeHelper.MaxScalarsPerCell - GraphemeHelper.CountScalars(grapheme);
        if (room <= 0) return true;

        int markLength = GraphemeHelper.TruncateToScalars(mark, room);
        Span<char> combined = stackalloc char[GraphemeHelper.MaxCellChars];
        grapheme.AsSpan().CopyTo(combined);
        mark.AsSpan(0, markLength).CopyTo(combined[grapheme.Length..]);
        short index = GraphemeHelper.StoreGrapheme((ReadOnlySpan<char>)combined[..(grapheme.Length + markLength)]);
        // A full grapheme store (index <= 0) keeps the cell as it was rather than losing its existing marks.
        if (index > 0)
        {
            buf.SetColdGraphemeIndex(row, col, index);
            RequestMarkRowDirty(row);
        }
        return true;
    }

    private void RequestMarkRowDirty(int row)
    {
        if (row == _lastDirtyRow) return;
        _lastDirtyRow = row;
        _ctx.MarkRowDirty(row);
    }

    /// <summary>
    /// Resets the consecutive-same-row write coalescing. The canvas calls this
    /// at every render boundary (via <see cref="TerminalBuffer.MarkRender"/>)
    /// so that writes arriving after a render always bump the row's generation
    /// and motion epoch — the incremental renderer depends on a bump per render
    /// cycle; without it, consecutive keystrokes in the same row would never
    /// mark the row dirty and the display would go stale.
    /// </summary>
    internal void ResetRowDirtyCoalescing()
    {
        _lastDirtyRow = -1;
    }

    private (int row, int col) GetPreviousBaseCell()
    {
        int row = _cursor.Row;
        int col = _cursor.Col;
        // With a wrap pending the cursor still rests on the cell that was just written (it only moves on the next
        // print), so that cell is the base; otherwise the base is the cell before the cursor.
        if (!_cursor.WrapPending)
        {
            if (row == 0 && col == 0) return (-1, -1);
            if (col == 0) { row--; col = _ctx.Columns - 1; }
            else col--;
        }

        var buf = _ctx.ActiveBuffer;
        while (row >= 0)
        {
            ref var cell = ref buf.GetCellRef(row, col);
            if (!cell.IsContinuation)
            {
                if (!cell.IsEmpty) return (row, col);
                return (-1, -1);
            }
            if (col == 0) { row--; col = _ctx.Columns - 1; }
            else col--;
        }
        return (-1, -1);
    }
}
