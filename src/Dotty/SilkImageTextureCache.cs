using System;
using System.Buffers;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Silk.NET.OpenGL;

namespace Dotty.Silk;

/// <summary>
/// Minimal seam over the renderer's cached GL binding state so image code never desynchronises
/// it. <see cref="SilkTerminalRenderer"/> should implement this by forwarding to its
/// <c>UseProgram</c>/<c>BindVertexArray</c>/<c>BindAtlasTexture</c> wrappers.
/// </summary>
public interface IGlBindingCache
{
    void UseProgram(uint program);
    void BindVertexArray(uint vao);
    /// <summary>Makes texture unit 0 active and binds <paramref name="texture"/> to TEXTURE_2D.</summary>
    void BindTexture2D(uint texture);
}

/// <summary>Self-contained <see cref="IGlBindingCache"/> that tracks its own state (tests, standalone use).</summary>
public sealed class DirectGlBindingCache : IGlBindingCache
{
    private readonly GL _gl;
    private uint _program = uint.MaxValue;
    private uint _vao = uint.MaxValue;
    private uint _texture = uint.MaxValue;
    private bool _unitSet;

    public DirectGlBindingCache(GL gl) => _gl = gl ?? throw new ArgumentNullException(nameof(gl));

    public void UseProgram(uint program)
    {
        if (_program == program) return;
        _gl.UseProgram(program);
        _program = program;
    }

    public void BindVertexArray(uint vao)
    {
        if (_vao == vao) return;
        _gl.BindVertexArray(vao);
        _vao = vao;
    }

    public void BindTexture2D(uint texture)
    {
        if (!_unitSet)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _unitSet = true;
        }

        if (_texture == texture) return;
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _texture = texture;
    }

    /// <summary>Forget everything (context loss, or foreign code changed GL state).</summary>
    public void Invalidate()
    {
        _program = uint.MaxValue;
        _vao = uint.MaxValue;
        _texture = uint.MaxValue;
        _unitSet = false;
    }
}

/// <summary>
/// GL-thread-only cache of image textures keyed by <see cref="ImagePixels.Serial"/>. CPU pixels
/// are owned elsewhere, so an evicted texture is simply re-uploaded from the pixels the next time
/// a draw list references it. Call <see cref="BeginFrame"/>, <see cref="TryGetTexture"/> per draw
/// run, then <see cref="EndFrame"/>.
/// </summary>
public sealed unsafe class SilkImageTextureCache : IDisposable
{
    public const long DefaultUploadBudgetBytes = 16L * 1024 * 1024;
    public const long DefaultGpuBudgetBytes = 256L * 1024 * 1024;
    public const int DefaultMaxIdleFrames = 300;

    private sealed class Entry
    {
        public uint Texture;
        public int Width, Height;          // uploaded size (after any downscale)
        public ImagePixelFormat Format;
        public long Bytes;
        public long LastUsedFrame;
        public bool Failed;
    }

    private readonly GL _gl;
    private readonly IGlBindingCache _bindings;
    private readonly long _uploadBudgetBytes;
    private readonly long _gpuBudgetBytes;
    private readonly int _maxIdleFrames;
    private readonly Dictionary<long, Entry> _entries = new();
    private int _maxTextureSize;
    private long _frame;
    private UploadBudgetTracker _budget;
    private long _textureBytes;
    private bool _disposed;

    public SilkImageTextureCache(
        GL gl,
        IGlBindingCache bindings,
        long uploadBudgetBytes = DefaultUploadBudgetBytes,
        long gpuBudgetBytes = DefaultGpuBudgetBytes,
        int maxIdleFrames = DefaultMaxIdleFrames)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _uploadBudgetBytes = uploadBudgetBytes;
        _gpuBudgetBytes = gpuBudgetBytes;
        _maxIdleFrames = maxIdleFrames;
        _budget = new UploadBudgetTracker(uploadBudgetBytes);
        ImageRetirementQueue.Enabled = true;
    }

    /// <summary>Estimated GPU bytes of resident image textures (mip chain included); not driver RSS.</summary>
    public long EstimatedTextureBytes => _textureBytes;
    public int TextureCount => _entries.Count;
    /// <summary>True when the current frame skipped at least one upload because of the per-frame budget.</summary>
    public bool UploadsDeferred => _budget.Deferred;
    /// <summary>Bytes uploaded during the current frame.</summary>
    public long UploadedBytesThisFrame => _budget.Used;
    /// <summary>GL_MAX_TEXTURE_SIZE (queried on first use).</summary>
    public int MaxTextureSize => EnsureMaxTextureSize();

    public bool Contains(long serial) => _entries.TryGetValue(serial, out Entry? e) && !e.Failed;
    public bool HasFailed(long serial) => _entries.TryGetValue(serial, out Entry? e) && e.Failed;

    public void BeginFrame()
    {
        _frame++;
        _budget = new UploadBudgetTracker(_uploadBudgetBytes);
        DrainRetired();
    }

    /// <summary>
    /// Returns the texture for <paramref name="pixels"/>, uploading it when absent and within the
    /// frame budget. False when the upload was deferred (see <see cref="UploadsDeferred"/>) or failed.
    /// </summary>
    public bool TryGetTexture(ImagePixels pixels, out uint texture)
    {
        texture = 0;
        if (_disposed)
            return false;

        if (_entries.TryGetValue(pixels.Serial, out Entry? entry))
        {
            if (entry.Failed)
                return false;
            entry.LastUsedFrame = _frame;
            texture = entry.Texture;
            return true;
        }

        int bpp = (int)pixels.Format;
        ImageUploadSize size = ImageUploadPlanner.ComputeUploadSize(pixels.Width, pixels.Height, EnsureMaxTextureSize());
        long uploadBytes = (long)size.Width * size.Height * bpp;
        if (!_budget.TryAdmit(uploadBytes))
            return false;

        entry = Upload(pixels, size);
        _entries[pixels.Serial] = entry;
        if (entry.Failed)
            return false;
        texture = entry.Texture;
        return true;
    }

    /// <summary>Evicts idle / over-budget textures. Call once per frame after all draws.</summary>
    public void EndFrame()
    {
        if (_entries.Count == 0)
            return;

        // Cheap precheck: nothing can be idle-evicted yet and the budget is met.
        if (_textureBytes <= _gpuBudgetBytes && _frame <= _maxIdleFrames)
            return;

        var rented = ArrayPool<ResidentTexture>.Shared.Rent(_entries.Count);
        var evicted = ArrayPool<long>.Shared.Rent(_entries.Count);
        try
        {
            int n = 0;
            foreach (KeyValuePair<long, Entry> kv in _entries)
                rented[n++] = new ResidentTexture(kv.Key, kv.Value.Bytes, kv.Value.LastUsedFrame);

            int count = ImageUploadPlanner.PlanEviction(
                rented.AsSpan(0, n), _frame, _maxIdleFrames, _gpuBudgetBytes, evicted);
            for (int i = 0; i < count; i++)
                Remove(evicted[i]);
        }
        finally
        {
            ArrayPool<ResidentTexture>.Shared.Return(rented);
            ArrayPool<long>.Shared.Return(evicted);
        }
    }

    /// <summary>Deletes the texture of one image (its pixels were freed).</summary>
    public void Retire(long serial) => Remove(serial);

    /// <summary>
    /// The GL context was lost or replaced: every texture name is invalid. Forget them without
    /// issuing GL calls; images re-upload on demand.
    /// </summary>
    public void ResetAfterContextLoss()
    {
        _entries.Clear();
        _textureBytes = 0;
        _maxTextureSize = 0;
        _budget = new UploadBudgetTracker(_uploadBudgetBytes);
        if (_bindings is DirectGlBindingCache direct)
            direct.Invalidate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Entry e in _entries.Values)
        {
            if (e.Texture != 0)
                _gl.DeleteTexture(e.Texture);
        }

        _entries.Clear();
        _textureBytes = 0;
        ImageRetirementQueue.Enabled = false;
    }

    private void DrainRetired()
    {
        while (ImageRetirementQueue.TryDequeue(out long serial))
            Remove(serial);
    }

    private void Remove(long serial)
    {
        if (!_entries.Remove(serial, out Entry? e))
            return;
        if (e.Texture != 0)
        {
            _gl.DeleteTexture(e.Texture);
            // Deleting a bound texture resets the binding to 0 in GL; a binding cache that still believes the
            // deleted name is bound would skip the next bind of a re-generated (recycled) name.
            _bindings.BindTexture2D(0);
        }
        _textureBytes -= e.Bytes;
    }

    private int EnsureMaxTextureSize()
    {
        if (_maxTextureSize == 0)
        {
            _gl.GetInteger(GetPName.MaxTextureSize, out int max);
            _maxTextureSize = max > 0 ? max : 2048;
        }

        return _maxTextureSize;
    }

    private Entry Upload(ImagePixels pixels, ImageUploadSize size)
    {
        int bpp = (int)pixels.Format;
        var entry = new Entry
        {
            Width = size.Width,
            Height = size.Height,
            Format = pixels.Format,
            LastUsedFrame = _frame,
        };

        // Drop stale errors so the check below only sees this upload.
        DrainGlErrors();

        bool rgb = pixels.Format == ImagePixelFormat.Rgb8;
        uint tex = _gl.GenTexture();
        _bindings.BindTexture2D(tex);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, rgb ? 1 : 4);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);

        bool mipmaps = ImageUploadPlanner.WantsMipmaps(size.Width, size.Height);
        byte[]? scaled = null;
        try
        {
            ReadOnlySpan<byte> src = pixels.AsSpan();
            if (size.Width != pixels.Width || size.Height != pixels.Height)
            {
                scaled = ArrayPool<byte>.Shared.Rent(checked(size.Width * size.Height * bpp));
                ImageUploadPlanner.BoxDownscale(
                    src, pixels.Width, pixels.Height, pixels.Stride, bpp, scaled, size.Width, size.Height);
                src = scaled.AsSpan(0, size.Width * size.Height * bpp);
            }

            fixed (byte* p = src)
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D, 0,
                    rgb ? InternalFormat.Rgb8 : InternalFormat.Rgba8,
                    (uint)size.Width, (uint)size.Height, 0,
                    rgb ? PixelFormat.Rgb : PixelFormat.Rgba,
                    PixelType.UnsignedByte, p);
            }
        }
        finally
        {
            if (scaled != null)
                ArrayPool<byte>.Shared.Return(scaled);
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            mipmaps ? (int)GLEnum.LinearMipmapLinear : (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        if (mipmaps)
            _gl.GenerateMipmap(TextureTarget.Texture2D);

        GLEnum error = DrainGlErrors();
        if (error != GLEnum.NoError)
        {
            _gl.DeleteTexture(tex);
            // The deleted texture may still be what the binding cache believes is bound.
            _bindings.BindTexture2D(0);
            entry.Failed = true;
            DebugLog.Log($"[kitty-gl] texture upload failed for image serial {pixels.Serial} ({size.Width}x{size.Height}, {pixels.Format}): {error}");
            return entry;
        }

        entry.Texture = tex;
        entry.Bytes = ImageUploadPlanner.EstimateTextureBytes(size.Width, size.Height, bpp);
        _textureBytes += entry.Bytes;
        return entry;
    }

    /// <summary>Clears the GL error queue; returns the first error seen.</summary>
    private GLEnum DrainGlErrors()
    {
        GLEnum first = GLEnum.NoError;
        for (int i = 0; i < 16; i++)
        {
            GLEnum e = _gl.GetError();
            if (e == GLEnum.NoError)
                break;
            if (first == GLEnum.NoError)
                first = e;
        }

        return first;
    }
}
