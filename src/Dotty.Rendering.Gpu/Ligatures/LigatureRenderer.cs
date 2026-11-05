using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Row-level programming-ligature state for <see cref="QuadFrameBuilder.BuildRow"/>: segments a row into runs
/// (<see cref="RunSegmenter"/>), shapes them (<see cref="IRunShaper"/>), maps clusters onto cells
/// (<see cref="ClusterCellMapper"/>) and caches the result per row index. A cached row is reused while its
/// candidate cells, cursor column, face, size and <see cref="ConfigStamp"/> (ligature mode + features) match.
/// Rows without two adjacent ASCII punctuation characters take a fast path that neither shapes nor allocates.
/// Ligature glyphs live in the atlas under a synthetic grapheme naming (glyph ids, span) together with the run
/// face, size and style, so (face, glyphId, size, span) identifies an entry. Not thread-safe.
/// </summary>
public sealed class LigatureRenderer : IDisposable
{
    private const int MaxGlyphsPerRun = 512;
    private readonly IRunShaper _shaper;
    private readonly bool _ownsShaper;
    private readonly List<Row?> _rows = new();
    private FontFeature[] _features = Array.Empty<FontFeature>();
    private IReadOnlyList<FontFeature>? _featureSource;
    private SegmentCell[] _segmentScratch = Array.Empty<SegmentCell>();
    private CellRun[] _runScratch = Array.Empty<CellRun>();
    private readonly ShapedGlyph[] _glyphScratch = new ShapedGlyph[MaxGlyphsPerRun];
    private readonly char[] _textScratch = new char[byte.MaxValue];
    private readonly ushort[] _nominalScratch = new ushort[byte.MaxValue];
    private readonly StringBuilder _keyBuilder = new();
    private readonly LigatureCell[] _mappedScratch = new LigatureCell[byte.MaxValue];
    private byte[]? _searchCoverage;
    private int _searchRows;
    private int _searchColumns;

    public LigatureRenderer(IRunShaper shaper, bool ownsShaper = false)
    {
        _shaper = shaper ?? throw new ArgumentNullException(nameof(shaper));
        _ownsShaper = ownsShaper;
    }

    public LigatureMode Mode { get; private set; } = LigatureMode.Cursor;

    /// <summary>Changes whenever the mode or the feature settings change; cached rows of an older stamp are reshaped.</summary>
    public int ConfigStamp { get; private set; }

    /// <summary>Number of runs handed to the shaper (diagnostics and tests).</summary>
    public int ShapedRunCount { get; private set; }

    /// <summary>Applies <c>font.ligatures</c> and <c>font.features</c>; cheap when nothing changed.</summary>
    public void Configure(LigatureMode mode, IReadOnlyList<FontFeature>? features)
    {
        bool changed = mode != Mode;
        if (!ReferenceEquals(features, _featureSource))
        {
            _featureSource = features;
            var array = features is null ? Array.Empty<FontFeature>() : new List<FontFeature>(features).ToArray();
            if (!array.AsSpan().SequenceEqual(_features))
            {
                _features = array;
                changed = true;
            }
        }
        if (changed)
        {
            Mode = mode;
            ConfigStamp++;
        }
    }
    /// <summary>Sets cell foreground groups for search shaping; null restores ordinary shaping.</summary>
    public void SetSearchCoverage(byte[]? coverage, int rows, int columns)
    {
        _searchCoverage = coverage;
        _searchRows = rows;
        _searchColumns = columns;
    }

    /// <summary>
    /// Returns the ligature layout of <paramref name="row"/>, or null when nothing in it is shaped. The result is
    /// valid until the next call for the same row index.
    /// </summary>
    public Row? PrepareRow(IRenderSource source, int row, int rowLength, SKTypeface typeface, float textSize)
    {
        if (Mode == LigatureMode.Never || rowLength < 2)
            return null;
        var hot = source.GetRowCells(row);
        rowLength = Math.Min(rowLength, hot.Length);
        if (!HasPunctuationPair(hot[..rowLength]))
            return null;

        if (_segmentScratch.Length < rowLength)
            _segmentScratch = new SegmentCell[rowLength];
        var cells = _segmentScratch.AsSpan(0, rowLength);
        for (int i = 0; i < rowLength; i++)
        {
            ref readonly var cell = ref hot[i];
            if (cell.Width == 1 && !cell.HasGrapheme && RunSegmenter.IsCandidate(cell.Rune))
            {
                ref readonly var style = ref source.GetStyle(cell.StyleId);
                byte paintGroup = _searchCoverage != null && row < _searchRows
                    && i < _searchColumns && row * _searchColumns + i < _searchCoverage.Length
                    ? _searchCoverage[row * _searchColumns + i]
                    : (byte)0;
                cells[i] = style.Invisible ? SegmentCell.None
                    : new SegmentCell((char)cell.Rune, (int)GlyphStyles.From(style.Bold, style.Italic), paintGroup);
            }
            else
            {
                cells[i] = SegmentCell.None;
            }
        }
        int cursor = Mode == LigatureMode.Cursor && source.CursorVisible && source.CursorRow == row ? source.CursorCol : -1;

        while (_rows.Count <= row)
            _rows.Add(null);
        var entry = _rows[row];
        if (entry != null && entry.Matches(cells, cursor, typeface, textSize, ConfigStamp))
            return entry.HasShaped ? entry : null;
        entry ??= new Row();
        _rows[row] = entry;
        Shape(entry, cells, cursor, typeface, textSize);
        return entry.HasShaped ? entry : null;
    }

    private static bool HasPunctuationPair(ReadOnlySpan<CellHot> hot)
    {
        bool previous = false;
        for (int i = 0; i < hot.Length; i++)
        {
            bool current = RunSegmenter.IsPunctuation(hot[i].Rune);
            if (current && previous)
                return true;
            previous = current;
        }
        return false;
    }

    private void Shape(Row entry, ReadOnlySpan<SegmentCell> cells, int cursor, SKTypeface typeface, float textSize)
    {
        entry.Reset(cells, cursor, typeface, textSize, ConfigStamp);
        if (_runScratch.Length < cells.Length / 2 + 1)
            _runScratch = new CellRun[cells.Length / 2 + 1];
        int runCount = RunSegmenter.Segment(cells, Mode, cursor, _runScratch);
        var mapped = _mappedScratch;
        for (int r = 0; r < runCount; r++)
        {
            var run = _runScratch[r];
            if (run.Length > byte.MaxValue)
                continue;
            var text = _textScratch.AsSpan(0, run.Length);
            var nominal = _nominalScratch.AsSpan(0, run.Length);
            for (int i = 0; i < run.Length; i++)
            {
                text[i] = cells[run.Start + i].Char;
                nominal[i] = _shaper.GetNominalGlyph(typeface, text[i]);
            }
            ShapedRunCount++;
            int glyphCount = _shaper.Shape(text, typeface, textSize, _features, _glyphScratch);
            if (glyphCount > _glyphScratch.Length)
                continue;
            var glyphs = _glyphScratch.AsSpan(0, glyphCount);
            var runCells = mapped.AsSpan(0, run.Length);
            if (!ClusterCellMapper.Map(glyphs, nominal, runCells))
                continue;
            for (int i = 0; i < run.Length; i++)
            {
                var cell = runCells[i];
                if (cell.Kind == LigatureCellKind.Start)
                {
                    var clusterGlyphs = glyphs.Slice(cell.GlyphStart, cell.GlyphCount);
                    entry.SetStart(run.Start + i, cell.Span, clusterGlyphs, BuildKey(clusterGlyphs, cell.Span), (GlyphStyle)run.Face);
                }
                else if (cell.Kind == LigatureCellKind.Continuation)
                {
                    entry.SetContinuation(run.Start + i);
                }
            }
        }
    }

    private string BuildKey(ReadOnlySpan<ShapedGlyph> glyphs, int span)
    {
        // U+0001 never reaches the atlas as a real grapheme, so these keys cannot collide with text.
        _keyBuilder.Clear().Append('\u0001').Append("lig").Append(span.ToString(CultureInfo.InvariantCulture));
        foreach (var glyph in glyphs)
            _keyBuilder.Append(':').Append(glyph.GlyphId.ToString(CultureInfo.InvariantCulture));
        return _keyBuilder.ToString();
    }

    public void Dispose()
    {
        if (_ownsShaper && _shaper is IDisposable disposable)
            disposable.Dispose();
    }

    /// <summary>Ligature layout of one row: per-column <see cref="LigatureCellKind"/> plus the glyphs of each start cell.</summary>
    public sealed class Row
    {
        private SegmentCell[] _cells = Array.Empty<SegmentCell>();
        private int _length;
        private int _cursor;
        private SKTypeface? _typeface;
        private float _textSize;
        private int _stamp;
        private LigatureCellKind[] _kinds = Array.Empty<LigatureCellKind>();
        private Start[] _starts = Array.Empty<Start>();

        private struct Start
        {
            public string Key;
            public GlyphStyle Style;
            public ShapedGlyph[] Glyphs;
        }

        public bool HasShaped { get; private set; }

        internal bool Matches(ReadOnlySpan<SegmentCell> cells, int cursor, SKTypeface typeface, float textSize, int stamp) =>
            _stamp == stamp && _cursor == cursor && ReferenceEquals(_typeface, typeface) && _textSize.Equals(textSize)
            && cells.SequenceEqual(_cells.AsSpan(0, _length));

        internal void Reset(ReadOnlySpan<SegmentCell> cells, int cursor, SKTypeface typeface, float textSize, int stamp)
        {
            if (_cells.Length < cells.Length)
            {
                _cells = new SegmentCell[cells.Length];
                _kinds = new LigatureCellKind[cells.Length];
                _starts = new Start[cells.Length];
            }
            cells.CopyTo(_cells);
            Array.Clear(_kinds);
            Array.Clear(_starts);
            _length = cells.Length;
            _cursor = cursor;
            _typeface = typeface;
            _textSize = textSize;
            _stamp = stamp;
            HasShaped = false;
        }

        internal void SetStart(int column, int span, ReadOnlySpan<ShapedGlyph> glyphs, string key, GlyphStyle style)
        {
            _kinds[column] = LigatureCellKind.Start;
            _starts[column] = new Start { Key = key, Style = style, Glyphs = glyphs.ToArray() };
            HasShaped = true;
        }

        internal void SetContinuation(int column) => _kinds[column] = LigatureCellKind.Continuation;

        /// <summary>How <paramref name="column"/> is drawn; <see cref="LigatureCellKind.None"/> outside the row.</summary>
        public LigatureCellKind KindAt(int column) =>
            (uint)column < (uint)_length ? _kinds[column] : LigatureCellKind.None;

        /// <summary>Atlas key of the cluster starting at <paramref name="column"/> (only valid for start cells).</summary>
        public GlyphKey KeyAt(int column) =>
            new(_starts[column].Key, _typeface!, _textSize, _starts[column].Style);

        /// <summary>Glyphs of the cluster starting at <paramref name="column"/>.</summary>
        public ReadOnlySpan<ShapedGlyph> GlyphsAt(int column) => _starts[column].Glyphs;

        /// <summary>
        /// Looks up, or rasterizes and packs, the cluster glyph drawn from start cell <paramref name="column"/>.
        /// </summary>
        public bool EnsureGlyph(GlyphAtlas atlas, int column, out GlyphInfo info, out bool added)
        {
            added = false;
            var key = KeyAt(column);
            if (atlas.TryGetGlyph(key, out info))
                return true;
            var glyphs = GlyphsAt(column);
            var policy = atlas.RasterPolicy;
            using var font = new SKFont(key.Typeface, key.TextSize)
            {
                Edging = policy.SkiaEdging,
                Hinting = policy.SkiaHinting,
                Subpixel = false,
                Embolden = key.IsBold,
                SkewX = key.IsItalic ? GlyphAtlas.SyntheticItalicSkewX : 0f,
            };
            using var builder = new SKTextBlobBuilder();
            var buffer = builder.AllocatePositionedRun(font, glyphs.Length);
            var ids = buffer.Glyphs;
            var points = buffer.Positions;
            float pen = 0f;
            for (int i = 0; i < glyphs.Length; i++)
            {
                ids[i] = glyphs[i].GlyphId;
                points[i] = new SKPoint(pen + glyphs[i].XOffset, glyphs[i].YOffset);
                pen += glyphs[i].XAdvance;
            }
            using var blob = builder.Build();
            if (blob == null || !atlas.EnsureGlyphShaped(key, blob, out info))
                return false;
            added = true;
            return true;
        }
    }
}
