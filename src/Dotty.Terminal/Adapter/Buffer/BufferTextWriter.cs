using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Dotty.Terminal.Adapter.Buffer;

namespace Dotty.Terminal.Adapter;

internal sealed class BufferTextWriter
{
    private readonly CursorController _cursor;
    private readonly BufferEraser _eraser;
    private readonly TerminalBuffer _ctx;
    private readonly StyleSet _styleSet;
    private static readonly ConcurrentDictionary<string, string> _graphemeCache = new();
    private int _lastDirtyRow = -1;

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
                if (i >= length) break;
            }

            if (i >= length) break;

            char c = text[i];
            if (c == '\r' && i + 1 < length && text[i + 1] == '\n')
            {
                _ctx.CarriageReturn();
                _ctx.LineFeed();
                i += 2;
                continue;
            }

            if (c >= 127)
            {
                if (c == 127)
                {
                    _eraser.ErasePreviousGlyph(_ctx.ActiveBuffer, _cursor, _ctx.Rows, _ctx.Columns);
                    RequestMarkRowDirty(_cursor.Row);
                    i++;
                    continue;
                }

                int len = StringInfo.GetNextTextElementLength(text.Slice(i));
                if (len > 0)
                {
                    var slice = text.Slice(i, len);
                    var lookup = _graphemeCache.GetAlternateLookup<ReadOnlySpan<char>>();
                    if (!lookup.TryGetValue(slice, out string? element))
                    {
                        element = slice.ToString();
                        lookup.TryAdd(slice, element);
                    }
                    WriteGrapheme(element, styleId, hyperlinkId);
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
            i++;
        }
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

            if (_ctx._autoWrap && _cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
                row = _cursor.Row;
                col = 0;
            }

            if (col < 0 || col >= cols) return;

            if (_ctx._clearLineOnNextWrite)
            {
                _eraser.ClearLineFromCursor(_ctx.ActiveBuffer, _cursor, _ctx.Columns);
                _ctx._clearLineOnNextWrite = false;
                RequestMarkRowDirty(row);
            }

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

            // Reset cold cells for the chunk (rare, only when cells had hyperlinks/graphemes)
            for (int j = 0; j < chunkLen; j++)
            {
                ref var cold = ref Unsafe.Add(ref coldData, baseIdx + j);
                if (cold.HyperlinkId != 0 || cold.GraphemeIndex >= 0)
                    cold.Reset();
            }

            if (hyperlinkId != 0)
            {
                for (int j = 0; j < chunkLen; j++)
                    buf.SetColdHyperlink(row, col + j, hyperlinkId);
            }

            buf.UpdateRowMaxCol(row, endCol);
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
        int startCol;
        if (autoWrap)
        {
            if (_cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
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

        bool hadCold = cell.HasHyperlink || cell.HasGrapheme;
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
        int width = UnicodeWidth.GetWidth(grapheme);
        if (width == 0)
        {
            if (AttachCombiningMark(grapheme)) return;
            width = 1;
        }

        bool autoWrap = _ctx._autoWrap;
        int startCol;
        if (autoWrap)
        {
            if (_cursor.WrapPending)
            {
                _ctx.LineFeed();
                _ctx.CarriageReturn();
                _cursor.SetWrapPending(false);
            }
            _cursor.EnsureSpace(width, _ctx.Rows, _ctx.Columns);
            startCol = _cursor.Col;
        }
        else
        {
            int cols = _ctx.Columns;
            startCol = _cursor.Col;
            if (startCol > cols - width) startCol = Math.Max(0, cols - width);
            _cursor.Set(_cursor.Row, startCol, _ctx.Rows, cols);
        }

        var buf = _ctx.ActiveBuffer;
        int currentRow = _cursor.Row;
        buf.ClearCell(currentRow, startCol);

        uint rune;
        try { rune = (uint)char.ConvertToUtf32(grapheme, 0); }
        catch (ArgumentException) { rune = 0; }

        ref var cell = ref buf.GetCellRef(_cursor.Row, startCol);
        cell.Rune = rune;
        cell.StyleId = styleId;
        cell.Width = (byte)Math.Clamp(width, 1, 2);
        cell.IsContinuation = false;
        cell.HasHyperlink = hyperlinkId != 0;
        cell.HasGrapheme = grapheme.Length > 1;

        if (hyperlinkId != 0 || grapheme.Length > 1)
        {
            ref var cold = ref buf.GetColdCellRef(_cursor.Row, startCol);
            cold.HyperlinkId = hyperlinkId;
            cold.GraphemeIndex = grapheme.Length > 1 ? GraphemeHelper.StoreGrapheme(grapheme) : (short)-1;
        }

        for (int i = 1; i < width; i++)
        {
            ref var cont = ref buf.GetCellRef(_cursor.Row, startCol + i);
            cont.Reset();
            cont.IsContinuation = true;
            cont.StyleId = styleId;
            if (hyperlinkId != 0)
            {
                cont.HasHyperlink = true;
                buf.SetColdHyperlink(_cursor.Row, startCol + i, hyperlinkId);
            }
        }

        buf.UpdateRowMaxCol(currentRow, startCol + width - 1);

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

    private bool AttachCombiningMark(string mark)
    {
        var (row, col) = GetPreviousBaseCell();
        if (row < 0) return false;
        var buf = _ctx.ActiveBuffer;
        ref var cell = ref buf.GetCellRef(row, col);
        var cold = buf.GetColdCell(row, col);
        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        if (string.IsNullOrEmpty(grapheme)) return false;

        var newGrapheme = grapheme + mark;
        if (newGrapheme.Length > 1)
        {
            try { cell.Rune = (uint)char.ConvertToUtf32(newGrapheme, 0); }
            catch (ArgumentException) { cell.Rune = 0; }
            buf.SetColdGraphemeIndex(row, col, GraphemeHelper.StoreGrapheme(newGrapheme));
        }
        else
        {
            try { cell.Rune = (uint)char.ConvertToUtf32(newGrapheme, 0); }
            catch (ArgumentException) { cell.Rune = 0; }
            buf.SetColdGraphemeIndex(row, col, -1);
        }
        RequestMarkRowDirty(row);
        return true;
    }

    private void RequestMarkRowDirty(int row)
    {
        if (row == _lastDirtyRow) return;
        _lastDirtyRow = row;
        _ctx.MarkRowDirty(row);
    }

    private (int row, int col) GetPreviousBaseCell()
    {
        int row = _cursor.Row;
        int col = _cursor.Col;
        if (row == 0 && col == 0) return (-1, -1);
        if (col == 0) { row--; col = _ctx.Columns - 1; }
        else col--;

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
