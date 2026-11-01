using System;
using System.Collections.Generic;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Shapes one grapheme cluster (ZWJ sequence, flag, keycap, skin-tone sequence, base + combining marks) with
/// HarfBuzz so the atlas can draw the font's composed glyphs with GPOS placement instead of one glyph per scalar.
/// HarfBuzz faces and fonts are cached per <see cref="SKTypeface"/> in a bounded least-recently-used cache;
/// <c>font.features</c> apply to every shape call. Thread-safe; a cache hit shaping into caller spans does not allocate.
/// </summary>
public sealed partial class GraphemeShaper : IDisposable
{
    public const int DefaultCapacity = 16;
    /// <summary>Upper bound of glyphs produced for one grapheme; longer results are not shaped.</summary>
    public const int MaxGlyphs = 32;
    // Shaping runs at a fixed scale; positions are rescaled to the requested text size.
    private const int HbScale = 1 << 12;

    private sealed class Entry : IDisposable
    {
        public required SKTypeface Typeface;
        public required Face Face;
        public required HbFont Font;
        public LinkedListNode<Entry>? Node;
        public void Dispose() { Font.Dispose(); Face.Dispose(); }
    }

    private readonly object _gate = new();
    private readonly Dictionary<SKTypeface, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Entry> _lru = new();
    private readonly HbBuffer _buffer = new();
    private readonly Feature[] _features;
    private readonly int _capacity;
    private bool _disposed;

    public GraphemeShaper(IReadOnlyList<FontFeature>? features = null, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        Features = features ?? Array.Empty<FontFeature>();
        _features = new Feature[Features.Count];
        for (int i = 0; i < _features.Length; i++)
            _features[i] = new Feature(Tag.Parse(Features[i].Tag), Features[i].Value);
    }

    /// <summary>The parsed <c>font.features</c> applied to every shape call.</summary>
    public IReadOnlyList<FontFeature> Features { get; }

    public int Capacity => _capacity;

    public int CachedFaceCount { get { lock (_gate) return _entries.Count; } }

    /// <summary>True when <paramref name="text"/> holds at least two Unicode scalars (the shaping threshold).</summary>
    public static bool HasMultipleScalars(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int scalars = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            if (++scalars >= 2) return true;
        }
        return false;
    }

    /// <summary>
    /// Shapes <paramref name="text"/> with <paramref name="typeface"/> at <paramref name="textSize"/>. Writes glyph ids and
    /// baseline-relative pen positions (y down) and returns the glyph count, or 0 when the face lacks a glyph
    /// (.notdef in the result) or the output does not fit the spans.
    /// </summary>
    public int Shape(string text, SKTypeface typeface, float textSize,
        Span<ushort> glyphs, Span<SKPoint> positions, out float advance)
    {
        advance = 0f;
        if (string.IsNullOrEmpty(text)) return 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = GetEntry(typeface);
            _buffer.ClearContents();
            _buffer.AddUtf16(text);
            _buffer.GuessSegmentProperties();
            unsafe
            {
                // HarfBuzzSharp's Font.Shape copies the feature list on every call; hb_shape directly does not allocate.
                fixed (Feature* features = _features)
                    hb_shape(entry.Font.Handle, _buffer.Handle, features, (uint)_features.Length);
            }
            var infos = _buffer.GetGlyphInfoSpan();
            var pos = _buffer.GetGlyphPositionSpan();
            int count = infos.Length;
            if (count == 0 || count > glyphs.Length || count > positions.Length) return 0;
            float k = textSize / HbScale;
            float pen = 0f;
            for (int i = 0; i < count; i++)
            {
                uint id = infos[i].Codepoint;
                if (id == 0 || id > ushort.MaxValue) return 0;
                glyphs[i] = (ushort)id;
                positions[i] = new SKPoint(pen + pos[i].XOffset * k, -pos[i].YOffset * k);
                pen += pos[i].XAdvance * k;
            }
            advance = pen;
            return count;
        }
    }

    /// <summary>
    /// A positioned text blob for <paramref name="text"/> drawn with <paramref name="font"/> (its typeface, size, skew and
    /// edging), or null when shaping produced .notdef so the caller keeps its per-scalar draw.
    /// </summary>
    public SKTextBlob? CreateBlob(string text, SKFont font)
    {
        Span<ushort> glyphs = stackalloc ushort[MaxGlyphs];
        Span<SKPoint> positions = stackalloc SKPoint[MaxGlyphs];
        int count = Shape(text, font.Typeface ?? SKTypeface.Default, font.Size, glyphs, positions, out _);
        if (count == 0) return null;
        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(font, count);
        glyphs[..count].CopyTo(run.Glyphs);
        positions[..count].CopyTo(run.Positions);
        return builder.Build();
    }

    private Entry GetEntry(SKTypeface typeface)
    {
        if (_entries.TryGetValue(typeface, out var hit))
        {
            if (hit.Node!.Previous != null)
            {
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node);
            }
            return hit;
        }

        while (_entries.Count >= _capacity && _lru.Last is { } oldest)
        {
            _lru.RemoveLast();
            _entries.Remove(oldest.Value.Typeface);
            oldest.Value.Dispose();
        }

        using var blob = typeface.OpenStream(out int index).ToHarfBuzzBlob();
        var face = new Face(blob, index) { Index = index, UnitsPerEm = typeface.UnitsPerEm };
        var font = new HbFont(face);
        font.SetScale(HbScale, HbScale);
        font.SetFunctionsOpenType();
        var entry = new Entry { Typeface = typeface, Face = face, Font = font };
        entry.Node = _lru.AddFirst(entry);
        _entries.Add(typeface, entry);
        return entry;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Dispose();
            _entries.Clear();
            _lru.Clear();
            _buffer.Dispose();
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libHarfBuzzSharp")]
    private static unsafe partial void hb_shape(nint font, nint buffer, Feature* features, uint numFeatures);
}
