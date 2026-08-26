using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Manages uploading an A8 coverage atlas bitmap from <see cref="GlyphAtlas"/> to an OpenGL texture
/// using Avalonia's <see cref="GlInterface"/>.
/// </summary>
public sealed class GLTextureManager : IDisposable
{
    // OpenGL constants
    public const int GL_TEXTURE_2D = 0x0DE1;
    public const int GL_R8 = 0x8229;
    public const int GL_RED = 0x1903;
    public const int GL_UNSIGNED_BYTE = 0x1401;
    public const int GL_LINEAR = 0x2601;
    public const int GL_NEAREST = 0x2600;
    public const int GL_CLAMP_TO_EDGE = 0x812F;
    public const int GL_TEXTURE_MIN_FILTER = 0x2801;
    public const int GL_TEXTURE_MAG_FILTER = 0x2600;
    public const int GL_TEXTURE_WRAP_S = 0x2802;
    public const int GL_TEXTURE_WRAP_T = 0x2803;
    public const int GL_UNPACK_ALIGNMENT = 0x0CF5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void glPixelStoreiDelegate(int pname, int param);

    private readonly GlInterface _gl;
    private readonly GlyphAtlas _atlas;
    private readonly glPixelStoreiDelegate? _glPixelStorei;

    private int _textureId;
    private int _lastUploadedGeneration = -1;
    private int _lastUploadedWidth = -1;
    private int _lastUploadedHeight = -1;
    private bool _disposed;

    /// <summary>
    /// Gets the OpenGL texture ID.
    /// </summary>
    public int TextureId => _textureId;

    /// <summary>
    /// Gets the associated <see cref="GlyphAtlas"/>.
    /// </summary>
    public GlyphAtlas Atlas => _atlas;

    /// <summary>
    /// Initializes a new instance of <see cref="GLTextureManager"/>.
    /// </summary>
    /// <param name="gl">The Avalonia OpenGL interface.</param>
    /// <param name="atlas">The glyph atlas providing A8 coverage data.</param>
    public GLTextureManager(GlInterface gl, GlyphAtlas atlas)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));

        var pixelStoreiPtr = gl.GetProcAddress("glPixelStorei");
        if (pixelStoreiPtr != IntPtr.Zero)
        {
            _glPixelStorei = Marshal.GetDelegateForFunctionPointer<glPixelStoreiDelegate>(pixelStoreiPtr);
        }
    }

    /// <summary>
    /// Binds the OpenGL texture to GL_TEXTURE_2D.
    /// </summary>
    public void Bind()
    {
        EnsureNotDisposed();
        if (_textureId != 0)
        {
            _gl.BindTexture(GL_TEXTURE_2D, _textureId);
        }
    }

    /// <summary>
    /// Updates the OpenGL texture from the glyph atlas if necessary (on first call or when atlas generation changes).
    /// Safe to call from the render thread; assumes caller holds any necessary atlas locks or atlas is in a consistent state.
    /// </summary>
    /// <returns>The OpenGL texture handle.</returns>
    public int UpdateTexture()
    {
        EnsureNotDisposed();

        int currentGeneration = _atlas.Generation;
        if (_textureId != 0 && currentGeneration == _lastUploadedGeneration)
        {
            return _textureId;
        }

        SKBitmap bitmap = _atlas.AtlasBitmap;
        if (bitmap == null || bitmap.IsNull)
        {
            return _textureId;
        }

        if (_textureId == 0)
        {
            _textureId = _gl.GenTexture();
        }

        _gl.BindTexture(GL_TEXTURE_2D, _textureId);

        // Set texture parameters: linear filtering and clamp to edge
        _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        _gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        // Ensure 1-byte alignment for single-channel (Alpha8/R8) texture data upload
        _glPixelStorei?.Invoke(GL_UNPACK_ALIGNMENT, 1);

        int width = bitmap.Width;
        int height = bitmap.Height;
        IntPtr pixels = bitmap.GetPixels();

        _gl.TexImage2D(
            GL_TEXTURE_2D,
            0,
            GL_R8,
            width,
            height,
            0,
            GL_RED,
            GL_UNSIGNED_BYTE,
            pixels);

        // Restore default 4-byte unpack alignment if changed
        _glPixelStorei?.Invoke(GL_UNPACK_ALIGNMENT, 4);

        _lastUploadedGeneration = currentGeneration;
        _lastUploadedWidth = width;
        _lastUploadedHeight = height;

        return _textureId;
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Disposes the GL texture.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_textureId != 0)
        {
            _gl.DeleteTexture(_textureId);
            _textureId = 0;
        }
    }
}
