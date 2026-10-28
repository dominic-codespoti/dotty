using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dotty.Terminal.Graphics;

/// <summary>Layout of an <see cref="ImagePixels"/> buffer; the value is the bytes per pixel.</summary>
public enum ImagePixelFormat : byte
{
    /// <summary>Opaque 8-bit RGB.</summary>
    Rgb8 = 3,
    /// <summary>8-bit RGBA with colour channels premultiplied by alpha.</summary>
    Rgba8Premultiplied = 4,
}

/// <summary>
/// Reference-counted native pixel buffer. The count starts at 1 (the creator's reference); <see cref="Retain"/> adds
/// one, <see cref="Release"/> removes one and the last release frees the memory. Content is written once (through
/// <see cref="AsWritableSpan"/>, before the buffer is shared) and is immutable afterwards.
/// Releasing more often than retained throws <see cref="ObjectDisposedException"/> rather than risking a double free.
/// </summary>
public sealed unsafe class ImagePixels : IDisposable
{
    private static long s_nextSerial;

    private byte* _ptr;
    private int _refCount = 1;
    private int _disposedByDispose;

    public long Serial { get; }
    public int Width { get; }
    public int Height { get; }
    public ImagePixelFormat Format { get; }
    public int Stride { get; }
    public long ByteLength { get; }

    /// <summary>Start of the pixel data. Only valid while the caller holds a reference.</summary>
    public byte* Pointer => _ptr;

    private ImagePixels(int width, int height, ImagePixelFormat format, int stride, long byteLength, byte* ptr)
    {
        Width = width;
        Height = height;
        Format = format;
        Stride = stride;
        ByteLength = byteLength;
        _ptr = ptr;
        Serial = Interlocked.Increment(ref s_nextSerial);
    }

    /// <summary>
    /// Allocates a zero-filled buffer. Throws <see cref="ArgumentOutOfRangeException"/> for non-positive dimensions,
    /// an undefined format, or a size that does not fit a <see cref="Span{T}"/> / <c>int</c> stride.
    /// </summary>
    public static ImagePixels Allocate(int width, int height, ImagePixelFormat format)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (format != ImagePixelFormat.Rgb8 && format != ImagePixelFormat.Rgba8Premultiplied)
            throw new ArgumentOutOfRangeException(nameof(format));

        long stride = checked((long)width * (int)format);
        if (stride > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), "Row does not fit an int stride.");
        long total = checked(stride * height);
        // Spans (AsWritableSpan/AsSpan) are int-indexed.
        if (total > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(height), "Image exceeds 2 GiB.");

        var ptr = (byte*)NativeMemory.AllocZeroed((nuint)total);
        GraphicsMemoryMetrics.ChangeLiveImagePixels(total, 1);
        return new ImagePixels(width, height, format, (int)stride, total, ptr);
    }

    /// <summary>Writable view; only valid before the buffer is shared with another owner.</summary>
    public Span<byte> AsWritableSpan()
    {
        ThrowIfFreed();
        return new Span<byte>(_ptr, (int)ByteLength);
    }

    public ReadOnlySpan<byte> AsSpan()
    {
        ThrowIfFreed();
        return new ReadOnlySpan<byte>(_ptr, (int)ByteLength);
    }

    /// <summary>Takes an additional reference and returns this instance. Throws if already fully released.</summary>
    public ImagePixels Retain()
    {
        int current = Volatile.Read(ref _refCount);
        while (true)
        {
            if (current <= 0) throw new ObjectDisposedException(nameof(ImagePixels));
            int seen = Interlocked.CompareExchange(ref _refCount, current + 1, current);
            if (seen == current) return this;
            current = seen;
        }
    }

    /// <summary>Drops one reference; the last one frees the memory. Throws when no reference is left.</summary>
    public void Release()
    {
        int current = Volatile.Read(ref _refCount);
        while (true)
        {
            if (current <= 0) throw new ObjectDisposedException(nameof(ImagePixels));
            int seen = Interlocked.CompareExchange(ref _refCount, current - 1, current);
            if (seen == current)
            {
                if (current == 1) Free();
                return;
            }
            current = seen;
        }
    }

    /// <summary>Releases the creator's reference once; further calls are ignored (idempotent, unlike <see cref="Release"/>).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedByDispose, 1) == 0) Release();
    }

    private void Free()
    {
        byte* p = _ptr;
        _ptr = null;
        NativeMemory.Free(p);
        GraphicsMemoryMetrics.ChangeLiveImagePixels(-ByteLength, -1);
        if (ImageRetirementQueue.Enabled) ImageRetirementQueue.Retire(Serial);
    }

    private void ThrowIfFreed()
    {
        if (_ptr == null) throw new ObjectDisposedException(nameof(ImagePixels));
    }
}
