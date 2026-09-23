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
    private int _uploadedWidth;
    private int _uploadedHeight;
    private bool _disposed;

    public uint TextureId => _textureId;
    public GlyphAtlas Atlas => _atlas;

    public SilkGlTextureManager(GL gl, GlyphAtlas atlas)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _textureId = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _textureId);
        // The atlas is single-channel A8 coverage rasterized at device
        // pixels. Nearest keeps hinted 1:1 coverage from being re-blurred.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
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
        _uploadedWidth = 0;
        _uploadedHeight = 0;
    }

    public void Bind()
    {
        EnsureNotDisposed();
        if (_textureId != 0)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _textureId);
        }
    }
    // Allocates only when the atlas dimensions changed; ordinary glyphs update
    // their own A8 rectangles with TexSubImage2D.
    public uint UpdateTexture()
    {
        EnsureNotDisposed();

        int currentVersion = _atlas.ContentVersion;
        if (_textureId != 0
            && currentVersion == _lastUploadedVersion
            && _uploadedWidth == _atlas.Width
            && _uploadedHeight == _atlas.Height)
        {
            return _textureId;
        }

        if (_textureId == 0)
        {
            _textureId = _gl.GenTexture();
        }

        Bind();
        int uploadedVersion = _atlas.WithAtlasUpdates((bitmap, regions, fullUpload) =>
        {
            if (bitmap.IsNull)
                return;
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            try
            {
                bool dimensionsChanged = _uploadedWidth != bitmap.Width || _uploadedHeight != bitmap.Height;
                if (dimensionsChanged)
                {
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
                    _uploadedWidth = bitmap.Width;
                    _uploadedHeight = bitmap.Height;
                }
                else if (fullUpload)
                {
                    UploadRegion(bitmap, new AtlasDirtyRegion(0, 0, bitmap.Width, bitmap.Height));
                }
                else
                {
                    for (int i = 0; i < regions.Count; i++)
                    {
                        UploadRegion(bitmap, regions[i]);
                    }
                }
            }
            finally
            {
                _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            }
        });

        _lastUploadedVersion = uploadedVersion;
        return _textureId;
    }

    private void UploadRegion(SkiaSharp.SKBitmap bitmap, AtlasDirtyRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return;

        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, bitmap.RowBytes);
        byte* pixels = (byte*)bitmap.GetPixels();
        pixels += region.Y * bitmap.RowBytes + region.X;
        _gl.TexSubImage2D(
            TextureTarget.Texture2D,
            0,
            region.X,
            region.Y,
            (uint)region.Width,
            (uint)region.Height,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            pixels);
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
