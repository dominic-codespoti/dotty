using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Manages compressed scrollback segments using LZ4 compression.
/// Stores lines in compressed blocks of ~100 lines, with a hot cache
/// of the most recent ~50 lines for fast access.
/// </summary>
public class CompressedScrollback
{
    // Configuration
    private const int SegmentSize = 100;     // Lines per compressed segment
    private const int HotCacheSize = 50;     // Recent lines kept uncompressed
    private const int MaxCompressedSize = 64 * 1024; // 64KB max per segment (safety limit)

    // Internal line storage - mirrors TerminalBuffer.ScrollbackLine but internal
    private readonly struct LineEntry
    {
        public readonly char[] Buffer;
        public readonly int Length;
        public LineEntry(char[] buffer, int length) { Buffer = buffer; Length = length; }
        public string ToStringValue() => Buffer == null ? string.Empty : new string(Buffer, 0, Length);
    }

    // A compressed segment containing multiple lines
    private class CompressedSegment
    {
        public readonly byte[] Data;
        public readonly int LineCount;
        public readonly int UncompressedSize;

        public CompressedSegment(byte[] data, int lineCount, int uncompressedSize)
        {
            Data = data;
            LineCount = lineCount;
            UncompressedSize = uncompressedSize;
        }
    }

    // Hot cache - most recent lines uncompressed for fast access
    private readonly LinkedList<LineEntry> _hotCache = new();
    private int _hotCacheChars = 0;
    private const int MaxHotCacheChars = 8 * 1024; // ~8KB of hot cache

    // Compressed storage - segments are stored oldest first
    private readonly List<CompressedSegment> _segments = new();

    // Pending lines to be compressed into next segment
    private readonly List<LineEntry> _pendingLines = new();
    private int _pendingChars = 0;

    // Total counts
    private int _totalLineCount = 0;
    private int _maxScrollback = 10000;

    // Encoding for serialization
    private static readonly UTF8Encoding Utf8Encoding = new(false, true);

    public CompressedScrollback(int maxScrollback = 10000)
    {
        _maxScrollback = Math.Max(0, maxScrollback);
    }

    /// <summary>
    /// Total number of lines in scrollback (compressed + hot cache + pending)
    /// </summary>
    public int Count => _totalLineCount;

    /// <summary>
    /// Maximum scrollback size
    /// </summary>
    public int MaxScrollback
    {
        get => _maxScrollback;
        set
        {
            _maxScrollback = Math.Max(0, value);
            TrimIfNeeded();
        }
    }

    /// <summary>
    /// Add a line to scrollback
    /// </summary>
    public void AddLine(char[] buffer, int length)
    {
        if (_maxScrollback <= 0) return;

        // Create the line entry - always copy to properly sized buffer
        char[] lineBuffer = new char[length];
        Array.Copy(buffer, 0, lineBuffer, 0, length);

        var line = new LineEntry(lineBuffer, length);

        // Add to hot cache first
        _hotCache.AddLast(line);
        _hotCacheChars += length;
        _totalLineCount++;
        _pendingLines.Add(line);
        _pendingChars += length;

        // Trim hot cache if it gets too large
        TrimHotCache();

        // Flush pending to compressed segment if we've accumulated enough
        if (_pendingLines.Count >= SegmentSize || _pendingChars >= 4096)
        {
            FlushPendingToSegment();
        }

        // Trim total if over max
        TrimIfNeeded();
    }

    /// <summary>
    /// Get a line by index (0 = oldest, Count-1 = newest)
    /// </summary>
    public (char[] buffer, int length) GetLine(int index)
    {
        if (index < 0 || index >= _totalLineCount)
        {
            return (Array.Empty<char>(), 0);
        }

        // Calculate indices:
        // - index 0 is the oldest line
        // - oldest lines are in segments[0], then segments[1], etc.
        // - newest lines are in hot cache

        int segmentLines = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            if (index < segmentLines + seg.LineCount)
            {
                // Line is in this segment - decompress and return
                int lineInSegment = index - segmentLines;
                return GetLineFromSegment(seg, lineInSegment);
            }
            segmentLines += seg.LineCount;
        }

        // Line is in hot cache
        int hotIndex = index - segmentLines;
        if (hotIndex >= 0 && hotIndex < _hotCache.Count)
        {
            // Get from hot cache (skip through linked list)
            var node = _hotCache.First;
            for (int i = 0; i < hotIndex && node != null; i++)
            {
                node = node.Next;
            }
            if (node != null)
            {
                return (node.Value.Buffer, node.Value.Length);
            }
        }

        return (Array.Empty<char>(), 0);
    }

    /// <summary>
    /// Clear all scrollback
    /// </summary>
    public void Clear()
    {
        _hotCache.Clear();
        _hotCacheChars = 0;
        _segments.Clear();
        _pendingLines.Clear();
        _pendingChars = 0;
        _totalLineCount = 0;
    }

    /// <summary>
    /// Get all scrollback lines as strings (for compatibility)
    /// </summary>
    public string[] GetAllLines()
    {
        string[] result = new string[_totalLineCount];
        int idx = 0;

        // First, decompress all segments
        foreach (var segment in _segments)
        {
            var lines = DecompressSegment(segment);
            foreach (var line in lines)
            {
                result[idx++] = line.ToStringValue();
            }
        }

        // Then add hot cache lines
        foreach (var line in _hotCache)
        {
            result[idx++] = line.ToStringValue();
        }

        return result;
    }

    /// <summary>
    /// Compress pending lines into a new segment
    /// </summary>
    private void FlushPendingToSegment()
    {
        if (_pendingLines.Count == 0) return;

        // Serialize lines to a memory stream
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Utf8Encoding, true))
        {
            // Write count
            writer.Write(_pendingLines.Count);

            // Write each line
            foreach (var line in _pendingLines)
            {
                writer.Write(line.Length);
                // Write as UTF8 bytes for compression efficiency
                var bytes = Utf8Encoding.GetBytes(line.Buffer, 0, line.Length);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        var uncompressedData = ms.ToArray();
        int uncompressedSize = uncompressedData.Length;

        // Compress with LZ4
        var compressedData = new byte[LZ4Codec.MaximumOutputSize(uncompressedData.Length)];
        int compressedLength = LZ4Codec.Encode(
            uncompressedData, 0, uncompressedData.Length,
            compressedData, 0, compressedData.Length,
            LZ4Level.L00_FAST);

        // Resize to actual compressed size
        if (compressedLength > 0 && compressedLength < compressedData.Length)
        {
            Array.Resize(ref compressedData, compressedLength);
        }

        // Store the segment
        _segments.Add(new CompressedSegment(
            compressedData,
            _pendingLines.Count,
            uncompressedSize));

        // Clear pending
        _pendingLines.Clear();
        _pendingChars = 0;
    }

    /// <summary>
    /// Get a specific line from a compressed segment
    /// </summary>
    private (char[] buffer, int length) GetLineFromSegment(CompressedSegment segment, int lineIndex)
    {
        // Decompress the entire segment
        var lines = DecompressSegment(segment);

        if (lineIndex >= 0 && lineIndex < lines.Count)
        {
            var line = lines[lineIndex];
            return (line.Buffer, line.Length);
        }

        return (Array.Empty<char>(), 0);
    }

    /// <summary>
    /// Decompress a segment into LineEntry list
    /// </summary>
    private List<LineEntry> DecompressSegment(CompressedSegment segment)
    {
        var result = new List<LineEntry>(segment.LineCount);

        // Decompress
        var decompressed = new byte[segment.UncompressedSize];
        int decodedLength = LZ4Codec.Decode(
            segment.Data, 0, segment.Data.Length,
            decompressed, 0, decompressed.Length);

        if (decodedLength <= 0)
        {
            // Decompression failed
            return result;
        }

        // Parse lines
        using var ms = new MemoryStream(decompressed, 0, decodedLength);
        using (var reader = new BinaryReader(ms, Utf8Encoding, true))
        {
            int lineCount = reader.ReadInt32();

            for (int i = 0; i < lineCount; i++)
            {
                int charLength = reader.ReadInt32();
                int byteLength = reader.ReadInt32();
                var bytes = reader.ReadBytes(byteLength);

                // Decode UTF8 to chars
                var chars = Utf8Encoding.GetChars(bytes);
                result.Add(new LineEntry(chars, charLength));
            }
        }

        return result;
    }

    /// <summary>
    /// Trim the hot cache if it exceeds size limits
    /// </summary>
    private void TrimHotCache()
    {
        while (_hotCache.Count > HotCacheSize || _hotCacheChars > MaxHotCacheChars)
        {
            if (_hotCache.Count == 0) break;

            var first = _hotCache.First;
            if (first != null)
            {
                _hotCacheChars -= first.Value.Length;
                _hotCache.RemoveFirst();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Trim scrollback if over max capacity
    /// </summary>
    private void TrimIfNeeded()
    {
        while (_totalLineCount > _maxScrollback)
        {
            // Need to remove oldest lines
            if (_segments.Count > 0)
            {
                // Remove oldest segment
                var oldestSegment = _segments[0];
                _totalLineCount -= oldestSegment.LineCount;
                _segments.RemoveAt(0);
            }
            else if (_hotCache.Count > 0)
            {
                // Remove from hot cache
                var first = _hotCache.First;
                if (first != null)
                {
                    _totalLineCount--;
                    _hotCacheChars -= first.Value.Length;
                    _hotCache.RemoveFirst();
                }
            }
            else
            {
                // Nothing left to remove
                break;
            }
        }
    }

    /// <summary>
    /// Get compression statistics for debugging
    /// </summary>
    public (int totalLines, int segments, int hotCacheLines, long compressedBytes, long uncompressedBytes) GetStats()
    {
        long compressed = 0;
        long uncompressed = 0;
        foreach (var seg in _segments)
        {
            compressed += seg.Data.Length;
            uncompressed += seg.UncompressedSize;
        }

        return (_totalLineCount, _segments.Count, _hotCache.Count, compressed, uncompressed);
    }
}
