using Silk.NET.OpenGL;
using SkiaSharp;
using Dotty.Rendering.Gpu;

namespace Dotty.Silk;

public sealed unsafe class SilkGlTextureManager : IDisposable
{
    private readonly GL _gl;
    private GlyphAtlas _atlas;
    private uint _textureId;
    private int _lastUploadedVersion = -1;
    private bool _disposed;

    public uint TextureId => _textureId;
    public GlyphAtlas Atlas => _atlas;

    public SilkGlTextureManager(GL gl, GlyphAtlas atlas)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _textureId = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _textureId);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }
 
    public void SetAtlas(GlyphAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (ReferenceEquals(_atlas, atlas))
            return;

        _atlas = atlas;
        _lastUploadedVersion = -1;
    }

    public void Bind()
    {
        EnsureNotDisposed();
        if (_textureId != 0)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _textureId);
        }
    }

    // Re-uploads only when the atlas content changed (new glyph rasterized or grow).
    public uint UpdateTexture()
    {
        EnsureNotDisposed();

        int currentVersion = _atlas.ContentVersion;
        if (_textureId != 0 && currentVersion == _lastUploadedVersion)
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

        Bind();
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.R8,
            (uint)bitmap.Width,
            (uint)bitmap.Height,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            (void*)bitmap.GetPixels());
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

        _lastUploadedVersion = currentVersion;
        return _textureId;
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_textureId != 0)
            {
                _gl.DeleteTexture(_textureId);
                _textureId = 0;
            }
            _disposed = true;
        }
    }
}
