using System;
using System.Runtime.InteropServices;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Render-thread-confined cache of the composed instance run for each visible
/// terminal row. Entries retain their storage across invalidation and scroll
/// remapping so the cache reaches a stable high-water allocation.
/// </summary>
public sealed class QuadRowCache
{
    public struct Entry
    {
        public bool Valid;
        public int RowIdentity;
        public ulong Generation;
        public ulong ContentHash;
        public int SelectionStart;
        public int SelectionEnd;
        public int CursorColumn;
        public byte CursorShape;
        public ulong SearchHash;
        public CellInstance[] Instances = Array.Empty<CellInstance>();
        public int InstanceCount;
        public CellInstance[] SelectionExtras = Array.Empty<CellInstance>();
        public int SelectionExtraCount;
        public CellHot[] SourceCells = Array.Empty<CellHot>();
        public ColdCell[] SourceColdCells = Array.Empty<ColdCell>();
        public CellInstance CursorExtra;
        public bool HasCursorExtra;

        // Kept for the existing row-local quad cache helpers and callers.
        public SKPoint[] GlyphPos = Array.Empty<SKPoint>();
        public SKPoint[] GlyphUv = Array.Empty<SKPoint>();
        public SKColor[] GlyphCol = Array.Empty<SKColor>();
        public int GlyphCount;
        public SKPoint[] SolidPos = Array.Empty<SKPoint>();
        public SKColor[] SolidCol = Array.Empty<SKColor>();
        public int SolidCount;

        public Entry() { }
    }

    private Entry[] _entries = Array.Empty<Entry>();
    private Entry[] _previousEntries = Array.Empty<Entry>();
    private int[] _rowSources = Array.Empty<int>();
    private bool[] _sourceUsed = Array.Empty<bool>();
    private int _rows;
    private int _columns;
    private float _cellW;
    private float _cellH;

    public long Hits;
    public long Misses;
    public long ShiftedRows;

    public int Rows => _rows;
    public int Columns => _columns;

    public void EnsureGeometry(int rows, int columns, float cellW, float cellH)
    {
        if (_rows == rows && _columns == columns && _cellW.Equals(cellW) && _cellH.Equals(cellH))
            return;

        bool changed = _rows != rows || _columns != columns || !_cellW.Equals(cellW) || !_cellH.Equals(cellH);
        _rows = rows;
        _columns = columns;
        _cellW = cellW;
        _cellH = cellH;
        if (_entries.Length < rows)
            Array.Resize(ref _entries, rows);
        EnsureMappingCapacity(rows);
        if (changed)
            InvalidateAll();
    }

    public void Reset()
    {
        InvalidateAll();
        Hits = Misses = ShiftedRows = 0;
    }

    public ref Entry GetEntryRef(int row) => ref _entries[row];

    public ref readonly Entry GetPreviousEntryRef(int row) => ref _previousEntries[row];

    public void InvalidateAll()
    {
        for (int i = 0; i < _entries.Length; i++)
            ClearEntry(ref _entries[i]);
    }

    public void InvalidateRow(int row)
    {
        if ((uint)row < (uint)_entries.Length)
            ClearEntry(ref _entries[row]);
    }

    /// <summary>Snapshots the prior rows before the caller classifies the next frame.</summary>
    public void BeginFrame()
    {
        EnsureMappingCapacity(_rows);
        Array.Copy(_entries, _previousEntries, _rows);
        Array.Fill(_rowSources, -1, 0, _rows);
        Array.Clear(_sourceUsed, 0, _rows);
    }

    /// <summary>Maps a destination row to one unique reusable row from the previous frame.</summary>
    public bool TryMapRow(int destinationRow, int sourceRow)
    {
        if ((uint)destinationRow >= (uint)_rows || (uint)sourceRow >= (uint)_rows
            || _rowSources[destinationRow] >= 0 || _sourceUsed[sourceRow]
            || !_previousEntries[sourceRow].Valid)
        {
            return false;
        }

        _rowSources[destinationRow] = sourceRow;
        _sourceUsed[sourceRow] = true;
        return true;
    }

    public int GetMappedSource(int destinationRow) =>
        (uint)destinationRow < (uint)_rows ? _rowSources[destinationRow] : -1;

    /// <summary>
    /// Applies the row mapping without aliasing pooled storage. Unmatched rows
    /// receive unused prior entries so their buffers can be rebuilt in place.
    /// </summary>
    public void ApplyMappings()
    {
        int nextUnused = 0;
        for (int row = 0; row < _rows; row++)
        {
            int source = _rowSources[row];
            if (source < 0)
            {
                while (nextUnused < _rows && _sourceUsed[nextUnused])
                    nextUnused++;
                if (nextUnused < _rows)
                {
                    source = nextUnused++;
                    _sourceUsed[source] = true;
                }
            }

            if (source >= 0)
            {
                _entries[row] = _previousEntries[source];
                if (_rowSources[row] < 0)
                    ClearEntry(ref _entries[row]);
            }
            else
            {
                ClearEntry(ref _entries[row]);
            }
        }
    }

    /// <summary>
    /// Scroll helper retained for direct cache consumers. Content moves up, so
    /// each surviving destination row takes the next row's cached entry.
    /// </summary>
    public void ShiftUp(int delta, ReadOnlySpan<ulong> generations)
    {
        if (delta <= 0 || delta > _rows)
            return;

        BeginFrame();
        int survive = _rows - delta;
        for (int row = 0; row < survive; row++)
            TryMapRow(row, row + delta);
        ApplyMappings();

        for (int row = 0; row < survive; row++)
        {
            _entries[row].Generation = row < generations.Length ? generations[row] : 0;
            ShiftedRows++;
        }
        for (int row = survive; row < _rows; row++)
            ClearEntry(ref _entries[row]);
    }

    public static void Ensure<T>(ref T[] arr, int needed)
    {
        if (arr == null || arr.Length < needed)
            arr = new T[Math.Max(needed, 16)];
    }

    public void EnsureEntryCapacity(ref Entry entry, int cells)
    {
        Ensure(ref entry.Instances, cells);
        Ensure(ref entry.SelectionExtras, cells);
        Ensure(ref entry.SourceCells, cells);
        Ensure(ref entry.SourceColdCells, cells);
    }

    public void AddGlyphQuad(ref Entry e, float x, float yRel, float w, float h, SKRect uv, SKColor color)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(ref e.GlyphPos, e.GlyphCount + 4);
        Ensure(ref e.GlyphUv, e.GlyphCount + 4);
        Ensure(ref e.GlyphCol, e.GlyphCount + 4);

        float u0 = uv.Left, v0 = uv.Top, u1 = uv.Right, v1 = uv.Bottom;
        int i = e.GlyphCount;
        e.GlyphPos[i] = new SKPoint(x, yRel);
        e.GlyphUv[i] = new SKPoint(u0, v0);
        e.GlyphCol[i] = color;
        e.GlyphPos[i + 1] = new SKPoint(x + w, yRel);
        e.GlyphUv[i + 1] = new SKPoint(u1, v0);
        e.GlyphCol[i + 1] = color;
        e.GlyphPos[i + 2] = new SKPoint(x + w, yRel + h);
        e.GlyphUv[i + 2] = new SKPoint(u1, v1);
        e.GlyphCol[i + 2] = color;
        e.GlyphPos[i + 3] = new SKPoint(x, yRel + h);
        e.GlyphUv[i + 3] = new SKPoint(u0, v1);
        e.GlyphCol[i + 3] = color;
        e.GlyphCount += 4;
    }

    public void AddSolidQuad(ref Entry e, float x, float yRel, float w, float h, SKColor color)
    {
        if (w <= 0 || h <= 0) return;
        Ensure(ref e.SolidPos, e.SolidCount + 4);
        Ensure(ref e.SolidCol, e.SolidCount + 4);

        int i = e.SolidCount;
        e.SolidPos[i] = new SKPoint(x, yRel);
        e.SolidCol[i] = color;
        e.SolidPos[i + 1] = new SKPoint(x + w, yRel);
        e.SolidCol[i + 1] = color;
        e.SolidPos[i + 2] = new SKPoint(x + w, yRel + h);
        e.SolidCol[i + 2] = color;
        e.SolidPos[i + 3] = new SKPoint(x, yRel + h);
        e.SolidCol[i + 3] = color;
        e.SolidCount += 4;
    }

    private void EnsureMappingCapacity(int rows)
    {
        if (_previousEntries.Length < rows)
            Array.Resize(ref _previousEntries, rows);
        if (_rowSources.Length < rows)
            Array.Resize(ref _rowSources, rows);
        if (_sourceUsed.Length < rows)
            Array.Resize(ref _sourceUsed, rows);
    }

    private static void ClearEntry(ref Entry entry)
    {
        entry.Valid = false;
        entry.Generation = 0;
        entry.ContentHash = 0;
        entry.SelectionStart = -1;
        entry.SelectionEnd = -1;
        entry.CursorColumn = -1;
        entry.CursorShape = 0;
        entry.SearchHash = 0;
        entry.InstanceCount = 0;
        entry.SelectionExtraCount = 0;
        entry.CursorExtra = default;
        entry.RowIdentity = 0;
        entry.HasCursorExtra = false;
        entry.GlyphCount = 0;
        entry.SolidCount = 0;
    }
}
