using Silk.NET.OpenGL;
using SkiaSharp;
using Dotty.Rendering.Gpu;

namespace Dotty.Silk;

public sealed unsafe class SilkGlTextureManager : IDisposable
{
    private readonly GL _gl;
    private GlyphAtlas _atlas;
    private uint _textureId;
    private uint _colorTextureId;
    private int _lastUploadedVersion = -1;
    private int _lastUploadedColorVersion = -1;
    private int _uploadedWidth;
    private int _uploadedHeight;
    private int _uploadedColorWidth;
    private int _uploadedColorHeight;
    private bool _disposed;

    public uint TextureId => _textureId;
    public uint ColorTextureId => _colorTextureId;
    public GlyphAtlas Atlas => _atlas;
    /// <summary>Estimated R8 texture pixel payload from the currently uploaded atlas dimensions; not driver RSS.</summary>
    public long R8TexturePayloadBytes => (long)_uploadedWidth * _uploadedHeight;
    /// <summary>Estimated RGBA8 texture pixel payload from the currently uploaded atlas dimensions; not driver RSS.</summary>
    public long Rgba8TexturePayloadBytes => (long)_uploadedColorWidth * _uploadedColorHeight * 4;

    public SilkGlTextureManager(GL gl, GlyphAtlas atlas)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _textureId = CreateTexture();
    }

    private uint CreateTexture()
    {
        uint texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }

    public void SetAtlas(GlyphAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (ReferenceEquals(_atlas, atlas)) return;
        _atlas = atlas;
        _lastUploadedVersion = _lastUploadedColorVersion = -1;
        _uploadedWidth = _uploadedHeight = _uploadedColorWidth = _uploadedColorHeight = 0;
    }

    public void Bind()
    {
        EnsureNotDisposed();
        if (_textureId != 0) _gl.BindTexture(TextureTarget.Texture2D, _textureId);
    }

    public void BindColor()
    {
        EnsureNotDisposed();
        if (_colorTextureId != 0) _gl.BindTexture(TextureTarget.Texture2D, _colorTextureId);
    }

    public uint UpdateTexture()
    {
        EnsureNotDisposed();
        int currentVersion = _atlas.ContentVersion;
        if (_textureId != 0 && currentVersion == _lastUploadedVersion
            && _uploadedWidth == _atlas.Width && _uploadedHeight == _atlas.Height)
        {
            UpdateColorTexture();
            return _textureId;
        }

        Bind();
        int uploadedVersion = _atlas.WithAtlasUpdates((bitmap, regions, fullUpload) =>
        {
            if (bitmap.IsNull) return;
            SetUnpackState(bitmap, 1);
            try
            {
                bool dimensionsChanged = _uploadedWidth != bitmap.Width || _uploadedHeight != bitmap.Height;
                if (dimensionsChanged)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8,
                        (uint)bitmap.Width, (uint)bitmap.Height, 0, PixelFormat.Red, PixelType.UnsignedByte, (void*)bitmap.GetPixels());
                    _uploadedWidth = bitmap.Width; _uploadedHeight = bitmap.Height;
                }
                else if (fullUpload) UploadRegion(bitmap, new AtlasDirtyRegion(0, 0, bitmap.Width, bitmap.Height), false);
                else for (int i = 0; i < regions.Count; i++) UploadRegion(bitmap, regions[i], false);
            }
            finally { ResetUnpackState(); }
        });
        _lastUploadedVersion = uploadedVersion;
        UpdateColorTexture();
        return _textureId;
    }

    private void UpdateColorTexture()
    {
        if (_atlas.ColorWidth == 0) return;
        _gl.ActiveTexture(TextureUnit.Texture1);
        try
        {
            if (_colorTextureId == 0) _colorTextureId = CreateTexture();
            if (_atlas.ColorContentVersion == _lastUploadedColorVersion
                && _uploadedColorWidth == _atlas.ColorWidth && _uploadedColorHeight == _atlas.ColorHeight) return;
            BindColor();
            _lastUploadedColorVersion = _atlas.WithColorAtlasUpdates((bitmap, regions, fullUpload) =>
            {
                if (bitmap.IsNull) return;
                SetUnpackState(bitmap, 4);
                try
                {
                    bool dimensionsChanged = _uploadedColorWidth != bitmap.Width || _uploadedColorHeight != bitmap.Height;
                    if (dimensionsChanged)
                    {
                        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                            (uint)bitmap.Width, (uint)bitmap.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)bitmap.GetPixels());
                        _uploadedColorWidth = bitmap.Width; _uploadedColorHeight = bitmap.Height;
                    }
                    else if (fullUpload) UploadRegion(bitmap, new AtlasDirtyRegion(0, 0, bitmap.Width, bitmap.Height), true);
                    else for (int i = 0; i < regions.Count; i++) UploadRegion(bitmap, regions[i], true);
                }
                finally { ResetUnpackState(); }
            });
        }
        finally { _gl.ActiveTexture(TextureUnit.Texture0); }
    }

    private void SetUnpackState(SKBitmap bitmap, int bytesPerPixel)
    {
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, bitmap.RowBytes / bytesPerPixel);
    }

    private void ResetUnpackState()
    {
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    }

    private void UploadRegion(SKBitmap bitmap, AtlasDirtyRegion region, bool rgba)
    {
        if (region.Width <= 0 || region.Height <= 0) return;
        int bytesPerPixel = rgba ? 4 : 1;
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, bitmap.RowBytes / bytesPerPixel);
        byte* pixels = (byte*)bitmap.GetPixels() + region.Y * bitmap.RowBytes + region.X * bytesPerPixel;
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, region.X, region.Y,
            (uint)region.Width, (uint)region.Height,
            rgba ? PixelFormat.Rgba : PixelFormat.Red, PixelType.UnsignedByte, pixels);
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        if (_textureId != 0) { _gl.DeleteTexture(_textureId); _textureId = 0; }
        if (_colorTextureId != 0) { _gl.DeleteTexture(_colorTextureId); _colorTextureId = 0; }
        _disposed = true;
    }
}
