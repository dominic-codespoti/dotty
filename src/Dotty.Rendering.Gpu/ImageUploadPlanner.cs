using System;

namespace Dotty.Rendering.Gpu;

/// <summary>Size of the texture actually uploaded for an image.</summary>
public readonly record struct ImageUploadSize(int Width, int Height);

/// <summary>A texture currently resident on the GPU, as seen by the eviction planner.</summary>
public readonly record struct ResidentTexture(long Serial, long Bytes, long LastUsedFrame);

/// <summary>An image wanted this frame that has no texture yet (<paramref name="Bytes"/> = upload size).</summary>
public readonly record struct UploadCandidate(long Serial, long Bytes);

/// <summary>
/// Per-frame upload budget. A candidate is admitted when nothing has been uploaded yet
/// this frame (guarantees progress for an image larger than the whole budget) or when it
/// still fits. After the first refusal every later candidate is refused too, so deferred
/// images keep their draw order and are retried first on the next frame.
/// </summary>
public struct UploadBudgetTracker
{
    private readonly long _budget;

    public UploadBudgetTracker(long budgetBytes)
    {
        _budget = Math.Max(0, budgetBytes);
        Used = 0;
        Deferred = false;
    }

    public long Used { get; private set; }
    public bool Deferred { get; private set; }

    public bool TryAdmit(long bytes)
    {
        if (Deferred)
            return false;
        if (Used != 0 && Used + bytes > _budget)
        {
            Deferred = true;
            return false;
        }

        Used += bytes;
        return true;
    }
}

/// <summary>Pure decision logic behind the GL image texture cache.</summary>
public static class ImageUploadPlanner
{
    public const int MipmapMinDimension = 256;

    /// <summary>
    /// Largest-side-clamped size: unchanged when both sides fit; otherwise the longer side
    /// becomes <paramref name="maxTextureSize"/> and the other scales by the same factor,
    /// rounded to nearest, never below 1.
    /// </summary>
    public static ImageUploadSize ComputeUploadSize(int width, int height, int maxTextureSize)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (maxTextureSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxTextureSize));

        if (width <= maxTextureSize && height <= maxTextureSize)
            return new ImageUploadSize(width, height);

        if (width >= height)
        {
            long h = ((long)height * maxTextureSize + width / 2) / width;
            return new ImageUploadSize(maxTextureSize, (int)Math.Max(1, h));
        }
        else
        {
            long w = ((long)width * maxTextureSize + height / 2) / height;
            return new ImageUploadSize((int)Math.Max(1, w), maxTextureSize);
        }
    }

    /// <summary>Whether a texture of this size gets a mip chain.</summary>
    public static bool WantsMipmaps(int width, int height)
        => width >= MipmapMinDimension && height >= MipmapMinDimension;

    /// <summary>Estimated GPU bytes of a texture (base level plus a 4/3 mip chain when mipmapped).</summary>
    public static long EstimateTextureBytes(int width, int height, int bytesPerPixel)
    {
        long baseBytes = (long)width * height * bytesPerPixel;
        return WantsMipmaps(width, height) ? baseBytes + baseBytes / 3 : baseBytes;
    }

    /// <summary>
    /// Decides which candidates (in draw order) upload this frame. <paramref name="admit"/>[i] is set
    /// for each candidate; returns the number admitted.
    /// </summary>
    public static int PlanUploads(ReadOnlySpan<UploadCandidate> candidates, long budgetBytes, Span<bool> admit)
    {
        if (admit.Length < candidates.Length)
            throw new ArgumentException("admit span too short", nameof(admit));

        var tracker = new UploadBudgetTracker(budgetBytes);
        int count = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            bool ok = tracker.TryAdmit(candidates[i].Bytes);
            admit[i] = ok;
            if (ok) count++;
        }

        return count;
    }

    /// <summary>
    /// Chooses textures to delete. Textures unused for more than <paramref name="maxIdleFrames"/>
    /// frames always go; then, while the total exceeds <paramref name="gpuBudgetBytes"/>, the
    /// least recently used go (ties: larger first, then lower serial). Textures used in
    /// <paramref name="currentFrame"/> are never evicted. <paramref name="resident"/> is sorted in place
    /// (caller passes a scratch copy). Evicted serials are written to <paramref name="evicted"/>
    /// (must hold at least <c>resident.Length</c>) in eviction order; returns their count.
    /// </summary>
    public static int PlanEviction(
        Span<ResidentTexture> resident,
        long currentFrame,
        int maxIdleFrames,
        long gpuBudgetBytes,
        Span<long> evicted)
    {
        if (evicted.Length < resident.Length)
            throw new ArgumentException("evicted span too short", nameof(evicted));

        long total = 0;
        foreach (ResidentTexture t in resident)
            total += t.Bytes;

        resident.Sort(static (a, b) =>
        {
            int c = a.LastUsedFrame.CompareTo(b.LastUsedFrame);
            if (c != 0) return c;
            c = b.Bytes.CompareTo(a.Bytes);
            return c != 0 ? c : a.Serial.CompareTo(b.Serial);
        });

        int count = 0;
        foreach (ResidentTexture t in resident)
        {
            if (t.LastUsedFrame >= currentFrame)
                break;
            bool idle = currentFrame - t.LastUsedFrame > maxIdleFrames;
            if (!idle && total <= gpuBudgetBytes)
                break;
            evicted[count++] = t.Serial;
            total -= t.Bytes;
        }

        return count;
    }

    /// <summary>
    /// Box-filter downscale of tightly or loosely packed 8-bit-per-channel pixels. Each destination
    /// pixel is the rounded mean of the source pixels covering it (source ranges are
    /// <c>[floor(d*s/D), max(floor+1, ceil((d+1)*s/D)))</c>), so premultiplied data stays premultiplied.
    /// Same partition and rounding as <c>PixelOps.BoxDownscale</c> (which allocates an <c>ImagePixels</c>); this span
    /// overload exists so the GL thread can downscale straight into a pooled buffer without an extra allocation.
    /// </summary>
    public static void BoxDownscale(
        ReadOnlySpan<byte> src, int srcWidth, int srcHeight, int srcStrideBytes, int channels,
        Span<byte> dst, int dstWidth, int dstHeight)
    {
        if (channels is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(channels));
        if (dstWidth <= 0 || dstHeight <= 0 || dstWidth > srcWidth || dstHeight > srcHeight)
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "destination must be non-empty and not larger than the source");
        if (src.Length < (long)(srcHeight - 1) * srcStrideBytes + (long)srcWidth * channels)
            throw new ArgumentException("source too short", nameof(src));
        if (dst.Length < (long)dstWidth * dstHeight * channels)
            throw new ArgumentException("destination too short", nameof(dst));

        Span<long> sums = stackalloc long[4];
        for (int dy = 0; dy < dstHeight; dy++)
        {
            int y0 = (int)((long)dy * srcHeight / dstHeight);
            int y1 = (int)(((long)(dy + 1) * srcHeight + dstHeight - 1) / dstHeight);
            if (y1 <= y0) y1 = y0 + 1;
            for (int dx = 0; dx < dstWidth; dx++)
            {
                int x0 = (int)((long)dx * srcWidth / dstWidth);
                int x1 = (int)(((long)(dx + 1) * srcWidth + dstWidth - 1) / dstWidth);
                if (x1 <= x0) x1 = x0 + 1;

                sums.Clear();
                for (int y = y0; y < y1; y++)
                {
                    int rowOffset = y * srcStrideBytes;
                    for (int x = x0; x < x1; x++)
                    {
                        int p = rowOffset + x * channels;
                        for (int c = 0; c < channels; c++)
                            sums[c] += src[p + c];
                    }
                }

                long n = (long)(y1 - y0) * (x1 - x0);
                int o = (dy * dstWidth + dx) * channels;
                for (int c = 0; c < channels; c++)
                    dst[o + c] = (byte)((sums[c] + n / 2) / n);
            }
        }
    }
}
