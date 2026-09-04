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
    private int _drawMenuStart;
    private int _drawMenuCount;

    private const int ChromeFloatsPerInstance = 14;
    private uint _chromeProgram;
    private uint _chromeCornerVbo;
    private uint _chromeEbo;
    private uint _chromeInstanceVbo;
    private uint _chromeVao;
    private int _uChromeFramebufferPx;
    private float[] _chromeStaging = Array.Empty<float>();
    private float _lastFramebufferWidth;
    private float _lastFramebufferHeight;


    public SilkGlTextureManager TextureManager { get; }

    public SilkTerminalRenderer(GL gl, GlyphAtlas atlas)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        TextureManager = new SilkGlTextureManager(gl, atlas);

        _program = SilkGlShaders.CreateProgram(gl, SilkGlShaders.VertexSource, SilkGlShaders.FragmentSource);
        _chromeProgram = SilkGlShaders.CreateProgram(gl, SilkChromeShaders.VertexSource, SilkChromeShaders.FragmentSource);
        _uChromeFramebufferPx = _gl.GetUniformLocation(_chromeProgram, "uFramebufferPx");
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
        InitChromeBuffers();
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

    private void SetupInstanceAttribs(uint baseInstance = 0)
    {
        uint stride = FloatsPerInstance * sizeof(float);
        uint baseOffset = baseInstance * stride;

        void Attrib(uint loc, int size, uint offsetFloats)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(
                loc,
                size,
                VertexAttribPointerType.Float,
                false,
                stride,
                (void*)(baseOffset + offsetFloats * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        Attrib(1, 2, 0);   // aGridPx (x, y)
        Attrib(2, 4, 2);   // aAtlasPx (x, y, w, h)
        Attrib(3, 4, 6);   // aMetrics (0, offY, offX, 0)
        Attrib(4, 4, 10);  // aFg (r, g, b, a)
        Attrib(5, 4, 14);  // aBg (r, g, b, a)

        _gl.EnableVertexAttribArray(6);
        _gl.VertexAttribIPointer(
            6,
            1,
            VertexAttribIType.UnsignedInt,
            stride,
            (void*)(baseOffset + 18 * sizeof(float)));
        _gl.VertexAttribDivisor(6, 1);
    }

    private void InitChromeBuffers()
    {
        _chromeVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_chromeVao);

        float[] corners = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
        _chromeCornerVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _chromeCornerVbo);
        fixed (float* p = corners)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(corners.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        ushort[] indices = { 0, 1, 2, 0, 2, 3 };
        _chromeEbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _chromeEbo);
        fixed (ushort* p = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        }

        _chromeInstanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _chromeInstanceVbo);
        SetupChromeInstanceAttribs();
    }

    private void SetupChromeInstanceAttribs()
    {
        uint stride = ChromeFloatsPerInstance * sizeof(float);

        void Attrib(uint loc, int size, uint offsetFloats)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, stride, (void*)(offsetFloats * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        Attrib(1, 4, 0);   // aRect (x, y, w, h)
        Attrib(2, 2, 4);   // aShape (radius, blur)
        Attrib(3, 4, 6);   // aColorTop (r, g, b, a)
        Attrib(4, 4, 10);  // aColorBottom (r, g, b, a)
    }

    public void Render(
        ReadOnlySpan<CellInstance> instances,
        ReadOnlySpan<ChromeQuadInstance> chromeQuads,
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
        int barRows = 0,
        int menuInstanceStart = -1,
        int menuChromeStart = -1)
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

        _lastFramebufferWidth = framebufferWidth;
        _lastFramebufferHeight = framebufferHeight;
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

        UploadAndDraw(
            cellW,
            cellH,
            paddingLeft,
            paddingTop,
            barRows,
            chromeQuads,
            menuInstanceStart,
            menuChromeStart);
    }

    private void UploadAndDraw(
        float cellW,
        float cellH,
        float paddingLeft,
        float paddingTop,
        int barRows,
        ReadOnlySpan<ChromeQuadInstance> chromeQuads,
        int menuInstanceStart,
        int menuChromeStart)
    {
        int cellCount = _lastInstanceCount;
        if (cellCount == 0)
        {
            DrawChrome(chromeQuads);
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
            int clampedMenuStart = menuInstanceStart < 0
                ? -1
                : Math.Clamp(menuInstanceStart, 0, cellCount);
            int menuOutputStart = -1;

            for (int i = 0; i < cellCount; i++)
            {
                if (i == clampedMenuStart)
                {
                    menuOutputStart = outputInstanceCount;
                }

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
                stagingArr[o + 18] = BitConverter.UInt32BitsToSingle(c.Flags);
                outputInstanceCount++;

                // Decorated cell: extra decor-only instance (bar quad over the full cell)
                if ((c.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) != 0)
                {
                    int d = outputInstanceCount * FloatsPerInstance;
                    float decorWidth = (c.Flags & CellFlags.WideCell) != 0 ? cellW * 2f : cellW;
                    stagingArr[d] = x;
                    stagingArr[d + 1] = y;
                    stagingArr[d + 2] = 0f;
                    stagingArr[d + 3] = 0f;
                    stagingArr[d + 4] = decorWidth;
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
                    stagingArr[d + 18] = BitConverter.UInt32BitsToSingle((uint)(c.Flags | CellFlags.DecorOnly));
                    outputInstanceCount++;
                }
            }

            if (clampedMenuStart == cellCount)
            {
                menuOutputStart = outputInstanceCount;
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
            _drawMenuStart = menuOutputStart;
            _drawMenuCount = menuOutputStart >= 0 ? outputInstanceCount - menuOutputStart : 0;
            _stagedCellW = cellW;
            _stagedCellH = cellH;
            _instanceBufferDirty = false;
        }

        bool hasMenuOverlay = menuInstanceStart >= 0
            && menuChromeStart >= 0
            && menuInstanceStart <= cellCount
            && _drawMenuStart >= 0
            && _drawMenuStart <= _drawInstanceCount;
        int baseInstanceCount = hasMenuOverlay ? _drawMenuStart : _drawInstanceCount;
        int baseChromeCount = hasMenuOverlay
            ? Math.Clamp(menuChromeStart, 0, chromeQuads.Length)
            : chromeQuads.Length;

        DrawCellRange(0, baseInstanceCount, pass: 0);
        if (baseChromeCount > 0)
        {
            DrawChrome(chromeQuads[..baseChromeCount]);
        }

        DrawCellRange(0, baseInstanceCount, pass: 1);
        if (baseChromeCount < chromeQuads.Length)
        {
            DrawChrome(chromeQuads[baseChromeCount..]);
        }

        if (hasMenuOverlay && _drawMenuCount > 0)
        {
            DrawCellRange(_drawMenuStart, _drawMenuCount, pass: 1);
        }
    }
    private void DrawCellRange(int firstInstance, int instanceCount, int pass)
    {
        if (instanceCount <= 0)
            return;

        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
        SetupInstanceAttribs((uint)firstInstance);
        _gl.Uniform1(_uPass, pass);
        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedShort,
            null,
            (uint)instanceCount);
    }

    private void DrawChrome(ReadOnlySpan<ChromeQuadInstance> chromeQuads)
    {
        if (chromeQuads.IsEmpty)
        {
            return;
        }

        int count = chromeQuads.Length;
        int floats = count * ChromeFloatsPerInstance;
        if (_chromeStaging.Length < floats)
        {
            _chromeStaging = new float[floats];
        }

        float[] staging = _chromeStaging;
        for (int i = 0; i < count; i++)
        {
            ref readonly var q = ref chromeQuads[i];
            int o = i * ChromeFloatsPerInstance;
            staging[o] = q.X;
            staging[o + 1] = q.Y;
            staging[o + 2] = q.W;
            staging[o + 3] = q.H;
            staging[o + 4] = q.Radius;
            staging[o + 5] = q.Blur;
            staging[o + 6] = q.TopR;
            staging[o + 7] = q.TopG;
            staging[o + 8] = q.TopB;
            staging[o + 9] = q.TopA;
            staging[o + 10] = q.BottomR;
            staging[o + 11] = q.BottomG;
            staging[o + 12] = q.BottomB;
            staging[o + 13] = q.BottomA;
        }

        _gl.UseProgram(_chromeProgram);
        _gl.Uniform2(_uChromeFramebufferPx, _lastFramebufferWidth, _lastFramebufferHeight);
        _gl.BindVertexArray(_chromeVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _chromeInstanceVbo);
        fixed (float* fp = staging)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(floats * sizeof(float)),
                fp,
                BufferUsageARB.DynamicDraw);
        }
        SetupChromeInstanceAttribs();

        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedShort,
            null,
            (uint)count);
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

            if (_chromeProgram != 0)
            {
                _gl.DeleteProgram(_chromeProgram);
                _chromeProgram = 0;
            }

            if (_chromeCornerVbo != 0)
            {
                _gl.DeleteBuffer(_chromeCornerVbo);
                _chromeCornerVbo = 0;
            }

            if (_chromeEbo != 0)
            {
                _gl.DeleteBuffer(_chromeEbo);
                _chromeEbo = 0;
            }

            if (_chromeInstanceVbo != 0)
            {
                _gl.DeleteBuffer(_chromeInstanceVbo);
                _chromeInstanceVbo = 0;
            }

            if (_chromeVao != 0)
            {
                _gl.DeleteVertexArray(_chromeVao);
                _chromeVao = 0;
            }

            _disposed = true;
        }
    }
}
