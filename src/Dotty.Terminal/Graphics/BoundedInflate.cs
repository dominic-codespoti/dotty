using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace Dotty.Terminal.Graphics;

internal enum BoundedInflateResult : byte
{
    /// <summary>The stream inflated to exactly the destination length.</summary>
    Ok,
    /// <summary>The stream ended (or was truncated) before the destination was filled (kitty: ENODATA).</summary>
    SizeMismatchSmaller,
    /// <summary>The stream holds more than the destination length (kitty: EFBIG).</summary>
    TooLarge,
    /// <summary>The data is not a valid zlib stream (header, deflate blocks or Adler-32 trailer).</summary>
    InvalidData,
}

/// <summary>
/// Zlib (RFC 1950) decompression into a caller-owned destination of an exactly known size. Decompression never
/// produces more than <c>destination.Length + 1</c> bytes, so a decompression bomb cannot allocate or write beyond
/// the destination: the one extra byte is only probed (into a stack slot) to detect <see cref="BoundedInflateResult.TooLarge"/>.
/// <para>A stream that is truncated right after producing exactly the expected bytes is accepted (the trailer cannot be checked then).</para>
/// </summary>
internal static class BoundedInflate
{
    public static unsafe BoundedInflateResult Inflate(ReadOnlySpan<byte> compressed, Span<byte> destination, out int written)
    {
        written = 0;
        // No zlib header at all: nothing to inflate (and UnmanagedMemoryStream rejects a null pointer).
        if (compressed.IsEmpty)
            return destination.IsEmpty ? BoundedInflateResult.InvalidData : BoundedInflateResult.SizeMismatchSmaller;
        fixed (byte* p = compressed)
        {
            using var src = new UnmanagedMemoryStream(p, compressed.Length);
            return InflateCore(src, destination, out written);
        }
    }

    /// <summary>Same as <see cref="Inflate(ReadOnlySpan{byte}, Span{byte}, out int)"/> for payloads held in segments.</summary>
    public static BoundedInflateResult Inflate(in ReadOnlySequence<byte> compressed, Span<byte> destination, out int written)
    {
        using var src = new SequenceReadStream(compressed);
        return InflateCore(src, destination, out written);
    }

    private static BoundedInflateResult InflateCore(Stream source, Span<byte> destination, out int written)
    {
        written = 0;
        try
        {
            using var z = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true);
            int total = 0;
            while (total < destination.Length)
            {
                int n = z.Read(destination.Slice(total));
                if (n == 0) break;
                total += n;
            }
            written = total;
            if (total < destination.Length) return BoundedInflateResult.SizeMismatchSmaller;

            Span<byte> probe = stackalloc byte[1];
            return z.Read(probe) > 0 ? BoundedInflateResult.TooLarge : BoundedInflateResult.Ok;
        }
        catch (InvalidDataException)
        {
            return BoundedInflateResult.InvalidData;
        }
        catch (IOException)
        {
            return BoundedInflateResult.InvalidData;
        }
    }

    private sealed class SequenceReadStream : Stream
    {
        private ReadOnlySequence<byte> _rest;
        private readonly long _length;

        public SequenceReadStream(in ReadOnlySequence<byte> seq)
        {
            _rest = seq;
            _length = seq.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _rest.Length;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, _rest.Length);
            if (n == 0) return 0;
            _rest.Slice(0, n).CopyTo(buffer);
            _rest = _rest.Slice(n);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
