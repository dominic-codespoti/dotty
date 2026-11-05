using System;
using Dotty.Rendering.Gpu;
using Silk.NET.OpenGL;

namespace Dotty.Silk;

/// <summary>
/// Draws one <see cref="ChromeQuadInstance"/> over a frame that <see cref="SilkTerminalRenderer.Render"/> has
/// already drawn, before the swap: a separate pass for full-window effects such as the visual bell, so the
/// composer's frame and the renderer's retained chrome buffers stay untouched. Uses the chrome shader with its
/// own program, VAO and one-instance buffer. Program/VAO bindings go through the supplied
/// <see cref="IGlBindingCache"/> (the terminal renderer) so its binding cache stays valid; blending is NOT changed
/// here and must already be <c>Enable(Blend)</c> with <c>BlendFunc(One, OneMinusSrcAlpha)</c>, which the terminal
/// renderer sets (the chrome shader premultiplies its straight-alpha colours).
/// </summary>
public sealed unsafe class SilkOverlayRenderer : IDisposable
{
    private const int FloatsPerInstance = 14;
    private const uint InstanceStride = FloatsPerInstance * sizeof(float);

    private readonly GL _gl;
    private readonly IGlBindingCache _bindings;
    private uint _program;
    private uint _vao;
    private uint _cornerVbo;
    private uint _ebo;
    private uint _instanceVbo;
    private int _uFramebufferPx;
    private float _framebufferW = -1f;
    private float _framebufferH = -1f;
    private bool _disposed;

    public SilkOverlayRenderer(GL gl, IGlBindingCache bindings)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        CreateGlObjects();
    }

    /// <summary>Draws <paramref name="quad"/> (framebuffer pixels, straight alpha) over the current back buffer.</summary>
    public void Draw(in ChromeQuadInstance quad, int framebufferWidth, int framebufferHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
            return;

        float* instance = stackalloc float[FloatsPerInstance];
        instance[0] = quad.X;
        instance[1] = quad.Y;
        instance[2] = quad.W;
        instance[3] = quad.H;
        instance[4] = quad.Radius;
        instance[5] = quad.Blur;
        instance[6] = quad.TopR;
        instance[7] = quad.TopG;
        instance[8] = quad.TopB;
        instance[9] = quad.TopA;
        instance[10] = quad.BottomR;
        instance[11] = quad.BottomG;
        instance[12] = quad.BottomB;
        instance[13] = quad.BottomA;

        _bindings.UseProgram(_program);
        if (_framebufferW != framebufferWidth || _framebufferH != framebufferHeight)
        {
            _framebufferW = framebufferWidth;
            _framebufferH = framebufferHeight;
            _gl.Uniform2(_uFramebufferPx, _framebufferW, _framebufferH);
        }

        _bindings.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)InstanceStride, instance);
        _gl.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, (void*)0, 1);
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

    private void CreateGlObjects()
    {
        _program = SilkGlShaders.CreateProgram(_gl, SilkChromeShaders.VertexSource, SilkChromeShaders.FragmentSource);
        _uFramebufferPx = _gl.GetUniformLocation(_program, "uFramebufferPx");

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
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)InstanceStride, (void*)0, BufferUsageARB.DynamicDraw);

        void Attrib(uint loc, int size, uint offsetFloats)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, InstanceStride, (void*)(offsetFloats * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        Attrib(1, 4, 0);   // aRect (x, y, w, h)
        Attrib(2, 2, 4);   // aShape (radius, blur)
        Attrib(3, 4, 6);   // aColorTop (r, g, b, a)
        Attrib(4, 4, 10);  // aColorBottom (r, g, b, a)
    }
}
