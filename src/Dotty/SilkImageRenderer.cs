using System;
using Dotty.Rendering.Gpu;
using Silk.NET.OpenGL;

namespace Dotty.Silk;

/// <summary>
/// Instanced textured-quad pass for Kitty graphics. Per frame:
/// <see cref="BeginFrame"/>, then <see cref="DrawLayer"/> for each layer at its place in the scene
/// (the first non-empty call uploads textures and instance data once for the whole list),
/// then <see cref="EndFrame"/>. Every program/VAO/texture binding goes through the supplied
/// <see cref="IGlBindingCache"/>; blending is NOT changed here and must already be
/// <c>Enable(Blend)</c> with <c>BlendFunc(One, OneMinusSrcAlpha)</c> (premultiplied).
/// </summary>
public sealed unsafe class SilkImageRenderer : IDisposable
{
    private const int FloatsPerInstance = 9;
    private const uint InstanceStride = FloatsPerInstance * sizeof(float);

    private readonly GL _gl;
    private readonly IGlBindingCache _bindings;
    private readonly SilkImageTextureCache _textures;
    private uint _program;
    private uint _vao;
    private uint _cornerVbo;
    private uint _ebo;
    private uint _instanceVbo;
    private int _uFramebufferPx;
    private int _uImage;
    private int _instanceCapacityBytes;
    private uint[] _drawTextures = Array.Empty<uint>();
    private bool _prepared;
    private bool _anyDeferred;
    private bool _disposed;
    private int _skipped;

    public SilkImageRenderer(GL gl, SilkImageTextureCache textures, IGlBindingCache bindings)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        CreateGlObjects();
    }

    public SilkImageTextureCache Textures => _textures;

    /// <summary>
    /// Last completed frame skipped at least one texture upload (per-frame budget); the host should
    /// schedule another frame (<c>WindowFrameReason.ImageUpload</c>).
    /// </summary>
    public bool UploadsDeferred => _anyDeferred;

    /// <summary>Number of draw runs skipped last frame because their texture was deferred or failed.</summary>
    public int SkippedDrawsLastFrame { get; private set; }

    public long EstimatedTextureBytes => _textures.EstimatedTextureBytes;

    public void BeginFrame()
    {
        _prepared = false;
        _skipped = 0;
        _anyDeferred = false;
        _textures.BeginFrame();
    }

    /// <summary>
    /// Draws every run of <paramref name="layer"/> from <paramref name="list"/>. No-op (no GL calls)
    /// when the list has no runs in that layer.
    /// </summary>
    public void DrawLayer(ImageLayer layer, ImageDrawList? list, int framebufferWidth, int framebufferHeight)
    {
        if (_disposed || list is null || list.DrawCount == 0)
            return;

        int layerIndex = (int)layer;
        int start = list.LayerDrawStart[layerIndex];
        int end = list.LayerDrawStart[layerIndex + 1];
        if (end <= start)
            return;

        if (!_prepared)
            Prepare(list);

        _bindings.UseProgram(_program);
        _bindings.BindVertexArray(_vao);
        _gl.Uniform2(_uFramebufferPx, (float)framebufferWidth, (float)framebufferHeight);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);

        for (int i = start; i < end; i++)
        {
            ImageDraw draw = list.Draws[i];
            uint texture = _drawTextures[i];
            if (texture == 0 || draw.QuadCount <= 0)
                continue;

            _bindings.BindTexture2D(texture);
            ConfigureInstanceAttribs((uint)draw.FirstQuad * InstanceStride);
            _gl.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, (void*)0, (uint)draw.QuadCount);
        }
    }

    public void EndFrame()
    {
        if (_disposed)
            return;
        _anyDeferred = _textures.UploadsDeferred;
        SkippedDrawsLastFrame = _skipped;
        _textures.EndFrame();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gl.DeleteBuffer(_instanceVbo);
        _gl.DeleteBuffer(_cornerVbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }

    private void Prepare(ImageDrawList list)
    {
        _prepared = true;
        int drawCount = list.DrawCount;
        if (_drawTextures.Length < drawCount)
            _drawTextures = new uint[Math.Max(drawCount, _drawTextures.Length * 2)];

        for (int i = 0; i < drawCount; i++)
        {
            if (_textures.TryGetTexture(list.Draws[i].Pixels, out uint tex))
            {
                _drawTextures[i] = tex;
            }
            else
            {
                _drawTextures[i] = 0;
                _skipped++;
            }
        }

        int bytes = checked(list.QuadCount * (int)InstanceStride);
        if (bytes == 0)
            return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        int capacity = Math.Max(_instanceCapacityBytes, 1024);
        while (capacity < bytes)
            capacity = checked(capacity * 2);
        _instanceCapacityBytes = capacity;
        // Orphan the previous storage, then fill the used prefix.
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)capacity, (void*)0, BufferUsageARB.DynamicDraw);
        fixed (ImageQuadInstance* q = list.Quads)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)bytes, q);
        }
    }

    private void ConfigureInstanceAttribs(uint byteOffset)
    {
        void Attrib(uint loc, int size, uint offsetFloats)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, InstanceStride,
                (void*)(byteOffset + offsetFloats * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        Attrib(1, 4, 0);  // aRect
        Attrib(2, 4, 4);  // aUv
        Attrib(3, 1, 8);  // aAlpha
    }

    private void CreateGlObjects()
    {
        _program = SilkGlShaders.CreateProgram(_gl, SilkImageShaders.VertexSource, SilkImageShaders.FragmentSource);
        _uFramebufferPx = _gl.GetUniformLocation(_program, "uFramebufferPx");
        _uImage = _gl.GetUniformLocation(_program, "uImage");

        _bindings.UseProgram(_program);
        _gl.Uniform1(_uImage, 0);

        _vao = _gl.GenVertexArray();
        _bindings.BindVertexArray(_vao);

        float[] corners = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
        _cornerVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cornerVbo);
        fixed (float* p = corners)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(corners.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        ushort[] indices = { 0, 1, 2, 0, 2, 3 };
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (ushort* p = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        }

        _instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        ConfigureInstanceAttribs(0);
    }
}
