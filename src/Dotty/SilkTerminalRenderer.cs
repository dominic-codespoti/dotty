using System;
using Silk.NET.OpenGL;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk;

public sealed unsafe class SilkTerminalRenderer : IDisposable
{
    private const int FloatsPerInstance = 19;

    private readonly GL _gl;
    private uint _program;
    private uint _cornerVbo;
    private uint _ebo;
    private uint _instanceVbo;
    private uint _vao;

    private int _uFramebufferPx;
    private int _uCellPx;
    private int _uAtlasSize;
    private int _uPass;
    private int _uAtlas;
    private int _uUnderlineY;
    private int _uStrikeY;
    private int _uLineHalf;

    private float[] _staging = Array.Empty<float>();
    private CellInstance[] _lastInstances = Array.Empty<CellInstance>();
    private int _lastInstanceCount;
    private bool _disposed;
    private bool _instanceBufferDirty = true;
    private float _stagedCellW = float.NaN;
    private float _stagedCellH = float.NaN;
    private int _drawInstanceCount;


    public SilkGlTextureManager TextureManager { get; }

    public SilkTerminalRenderer(GL gl, GlyphAtlas atlas)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        TextureManager = new SilkGlTextureManager(gl, atlas);

        _program = SilkGlShaders.CreateProgram(gl, SilkGlShaders.VertexSource, SilkGlShaders.FragmentSource);
        _uFramebufferPx = _gl.GetUniformLocation(_program, "uFramebufferPx");
        _uCellPx = _gl.GetUniformLocation(_program, "uCellPx");
        _uAtlasSize = _gl.GetUniformLocation(_program, "uAtlasSize");
        _uPass = _gl.GetUniformLocation(_program, "uPass");
        _uAtlas = _gl.GetUniformLocation(_program, "uAtlas");
        _uUnderlineY = _gl.GetUniformLocation(_program, "uUnderlineY");
        _uStrikeY = _gl.GetUniformLocation(_program, "uStrikeY");
        _uLineHalf = _gl.GetUniformLocation(_program, "uLineHalf");

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        InitBuffers();
    }
 
    public void SetAtlas(GlyphAtlas atlas) => TextureManager.SetAtlas(atlas);

    private void InitBuffers()
    {
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

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
        SetupInstanceAttribs();
    }

    private void SetupInstanceAttribs()
    {
        uint stride = FloatsPerInstance * sizeof(float);

        void Attrib(uint loc, int size, uint offsetFloats)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, stride, (void*)(offsetFloats * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        Attrib(1, 2, 0);   // aGridPx (x, y)
        Attrib(2, 4, 2);   // aAtlasPx (x, y, w, h)
        Attrib(3, 4, 6);   // aMetrics (0, offY, offX, 0)
        Attrib(4, 4, 10);  // aFg (r, g, b, a)
        Attrib(5, 4, 14);  // aBg (r, g, b, a)
        Attrib(6, 1, 18);  // aFlags
    }

    public void Render(
        ReadOnlySpan<CellInstance> instances,
        int atlasWidth,
        int atlasHeight,
        int framebufferWidth,
        int framebufferHeight,
        float cellW,
        float cellH,
        float underlineY,
        float strikeY,
        float lineHalf,
        SgrColorArgb clearColor,
        bool frameCaptured = false,
        float paddingLeft = 0f,
        float paddingTop = 0f,
        int barRows = 0)
    {
        EnsureNotDisposed();

        if (frameCaptured)
        {
            if (_lastInstances.Length < instances.Length)
            {
                _lastInstances = new CellInstance[instances.Length];
            }

            if (instances.Length > 0)
            {
                instances.CopyTo(_lastInstances);
            }

            _lastInstanceCount = instances.Length;
            _instanceBufferDirty = true;
        }

        if (cellW != _stagedCellW || cellH != _stagedCellH)
        {
            _instanceBufferDirty = true;
        }

        _gl.Viewport(0, 0, (uint)framebufferWidth, (uint)framebufferHeight);
        _gl.ClearColor(clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        uint texId = TextureManager.UpdateTexture();
        _gl.UseProgram(_program);
        _gl.Uniform2(_uFramebufferPx, (float)framebufferWidth, (float)framebufferHeight);
        _gl.Uniform2(_uCellPx, cellW, cellH);
        _gl.Uniform2(_uAtlasSize, (float)atlasWidth, (float)atlasHeight);
        _gl.Uniform1(_uUnderlineY, underlineY);
        _gl.Uniform1(_uStrikeY, strikeY);
        _gl.Uniform1(_uLineHalf, lineHalf);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texId);
        _gl.Uniform1(_uAtlas, 0);

        UploadAndDraw(cellW, cellH, paddingLeft, paddingTop, barRows);
    }

    private void UploadAndDraw(float cellW, float cellH, float paddingLeft, float paddingTop, int barRows)
    {
        int cellCount = _lastInstanceCount;
        if (cellCount == 0)
        {
            return;
        }

        if (_instanceBufferDirty)
        {
            // Worst case: every cell has a decoration instance appended.
            int maxInstances = cellCount * 2;
            int maxFloats = maxInstances * FloatsPerInstance;
            if (_staging.Length < maxFloats)
            {
                _staging = new float[maxFloats];
            }

            float[] stagingArr = _staging;
            int outputInstanceCount = 0;

            for (int i = 0; i < cellCount; i++)
            {
                ref readonly var c = ref _lastInstances[i];
                float x = (c.Row >= barRows) ? (paddingLeft + c.Col * cellW) : (c.Col * cellW);
                float y = (c.Row >= barRows) ? (paddingTop + c.Row * cellH) : (c.Row * cellH);
                int o = outputInstanceCount * FloatsPerInstance;
                stagingArr[o] = x;
                stagingArr[o + 1] = y;
                stagingArr[o + 2] = c.GlyphX;
                stagingArr[o + 3] = c.GlyphY;
                stagingArr[o + 4] = c.GlyphW;
                stagingArr[o + 5] = c.GlyphH;
                stagingArr[o + 6] = 0f;
                stagingArr[o + 7] = c.OffY;
                stagingArr[o + 8] = c.OffX;
                stagingArr[o + 9] = 0f;
                stagingArr[o + 10] = c.FgR / 255f;
                stagingArr[o + 11] = c.FgG / 255f;
                stagingArr[o + 12] = c.FgB / 255f;
                stagingArr[o + 13] = 1f; // FgA
                stagingArr[o + 14] = c.BgR / 255f;
                stagingArr[o + 15] = c.BgG / 255f;
                stagingArr[o + 16] = c.BgB / 255f;
                stagingArr[o + 17] = c.BgA / 255f;
                stagingArr[o + 18] = c.Flags;
                outputInstanceCount++;

                // Decorated cell: extra decor-only instance (bar quad over the full cell)
                if ((c.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) != 0)
                {
                    int d = outputInstanceCount * FloatsPerInstance;
                    stagingArr[d] = x;
                    stagingArr[d + 1] = y;
                    stagingArr[d + 2] = 0f;
                    stagingArr[d + 3] = 0f;
                    stagingArr[d + 4] = cellW;
                    stagingArr[d + 5] = cellH;
                    stagingArr[d + 6] = 0f;
                    stagingArr[d + 7] = 0f;
                    stagingArr[d + 8] = 0f;
                    stagingArr[d + 9] = 0f;
                    stagingArr[d + 10] = c.FgR / 255f;
                    stagingArr[d + 11] = c.FgG / 255f;
                    stagingArr[d + 12] = c.FgB / 255f;
                    stagingArr[d + 13] = 1f;
                    stagingArr[d + 14] = 0f;
                    stagingArr[d + 15] = 0f;
                    stagingArr[d + 16] = 0f;
                    stagingArr[d + 17] = 0f;
                    stagingArr[d + 18] = (byte)(c.Flags | CellFlags.DecorOnly);
                    outputInstanceCount++;
                }
            }

            int uploadFloats = outputInstanceCount * FloatsPerInstance;
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
            fixed (float* fp = stagingArr)
            {
                _gl.BufferData(
                    BufferTargetARB.ArrayBuffer,
                    (nuint)(uploadFloats * sizeof(float)),
                    fp,
                    BufferUsageARB.DynamicDraw);
            }

            SetupInstanceAttribs();
            _drawInstanceCount = outputInstanceCount;
            _stagedCellW = cellW;
            _stagedCellH = cellH;
            _instanceBufferDirty = false;
        }

        _gl.Uniform1(_uPass, 0);
        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedShort,
            null,
            (uint)_drawInstanceCount);

        _gl.Uniform1(_uPass, 1);
        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedShort,
            null,
            (uint)_drawInstanceCount);
    }



    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            TextureManager.Dispose();

            if (_program != 0)
            {
                _gl.DeleteProgram(_program);
                _program = 0;
            }

            if (_cornerVbo != 0)
            {
                _gl.DeleteBuffer(_cornerVbo);
                _cornerVbo = 0;
            }

            if (_ebo != 0)
            {
                _gl.DeleteBuffer(_ebo);
                _ebo = 0;
            }

            if (_instanceVbo != 0)
            {
                _gl.DeleteBuffer(_instanceVbo);
                _instanceVbo = 0;
            }

            if (_vao != 0)
            {
                _gl.DeleteVertexArray(_vao);
                _vao = 0;
            }

            _disposed = true;
        }
    }
}
