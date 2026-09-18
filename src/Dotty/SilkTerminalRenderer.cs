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
    private uint _menuVao;

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
    private int _instanceBufferCapacityBytes;
    private int _stagedMenuInstanceStart = -1;
    private int _menuAttribStart = -1;

    private const int ChromeFloatsPerInstance = 14;
    private uint _chromeProgram;
    private uint _chromeCornerVbo;
    private uint _chromeEbo;
    private uint _chromeInstanceVbo;
    private uint _chromeVao;
    private uint _chromeMenuVao;
    private int _uChromeFramebufferPx;
    private float[] _chromeStaging = Array.Empty<float>();
    private ChromeQuadInstance[] _lastChromeQuads = Array.Empty<ChromeQuadInstance>();
    private int _lastChromeCount = -1;
    private int _stagedChromeMenuStart = -1;
    private float _lastFramebufferWidth;
    private float _lastFramebufferHeight;
    private int _chromeBufferCapacityBytes;
    private int _chromeMenuAttribStart = -1;

    // These caches are valid for the lifetime of this renderer/context. Every
    // operation that can change the cached state in this class updates them.
    private uint _boundProgram = uint.MaxValue;
    private uint _boundVao = uint.MaxValue;
    private uint _boundTexture = uint.MaxValue;
    private TextureUnit _activeTextureUnit = TextureUnit.Texture0;
    private bool _activeTextureSet;
    private bool _cellFramebufferSet;
    private float _cachedCellFramebufferW;
    private float _cachedCellFramebufferH;
    private bool _cellSizeSet;
    private float _cachedCellW;
    private float _cachedCellH;
    private bool _atlasSizeSet;
    private float _cachedAtlasW;
    private float _cachedAtlasH;
    private bool _underlineSet;
    private float _cachedUnderline;
    private bool _strikeSet;
    private float _cachedStrike;
    private bool _lineHalfSet;
    private float _cachedLineHalf;
    private bool _passSet;
    private int _cachedPass;
    private bool _atlasSamplerSet;
    private bool _chromeFramebufferSet;
    private float _cachedChromeFramebufferW;
    private float _cachedChromeFramebufferH;

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
        BindVertexArray(_vao);

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
        ConfigureInstanceAttribs(_vao, 0);

        // OpenGL 3.3 has no base-instance draw entry point. The menu VAO
        // provides the equivalent instance offset without changing attribute
        // pointers between draw calls.
        _menuVao = _gl.GenVertexArray();
        BindVertexArray(_menuVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _cornerVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        ConfigureInstanceAttribs(_menuVao, 0);
    }

    private void ConfigureInstanceAttribs(uint vao, uint baseInstance)
    {
        BindVertexArray(vao);
        // VertexAttribPointer captures ARRAY_BUFFER in the VAO. Keep this
        // explicit so neither setup path can accidentally capture chrome data.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
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

    private void EnsureMenuInstanceAttribs(int firstInstance)
    {
        if (firstInstance == _menuAttribStart)
            return;

        ConfigureInstanceAttribs(_menuVao, checked((uint)firstInstance));
        _menuAttribStart = firstInstance;
    }

    private void InitChromeBuffers()
    {
        _chromeVao = _gl.GenVertexArray();
        BindVertexArray(_chromeVao);

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
        ConfigureChromeInstanceAttribs(_chromeVao, 0);

        _chromeMenuVao = _gl.GenVertexArray();
        BindVertexArray(_chromeMenuVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _chromeCornerVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _chromeEbo);
        ConfigureChromeInstanceAttribs(_chromeMenuVao, 0);
    }

    private void ConfigureChromeInstanceAttribs(uint vao, uint baseInstance)
    {
        BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _chromeInstanceVbo);
        uint stride = ChromeFloatsPerInstance * sizeof(float);
        uint baseOffset = baseInstance * stride;

        void Attrib(uint loc, int size, uint offsetFloats)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, stride, (void*)(baseOffset + offsetFloats * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        Attrib(1, 4, 0);   // aRect (x, y, w, h)
        Attrib(2, 2, 4);   // aShape (radius, blur)
        Attrib(3, 4, 6);   // aColorTop (r, g, b, a)
        Attrib(4, 4, 10);  // aColorBottom (r, g, b, a)
    }

    private void EnsureMenuChromeAttribs(int firstInstance)
    {
        if (firstInstance == _chromeMenuAttribStart)
            return;

        ConfigureChromeInstanceAttribs(_chromeMenuVao, checked((uint)firstInstance));
        _chromeMenuAttribStart = firstInstance;
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
        if (menuInstanceStart != _stagedMenuInstanceStart)
        {
            _instanceBufferDirty = true;
        }

        _lastFramebufferWidth = framebufferWidth;
        _lastFramebufferHeight = framebufferHeight;
        _gl.Viewport(0, 0, (uint)framebufferWidth, (uint)framebufferHeight);
        _gl.ClearColor(clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        uint texId = TextureManager.UpdateTexture();
        UseProgram(_program);
        Uniform2CellFramebuffer((float)framebufferWidth, (float)framebufferHeight);
        Uniform2CellSize(cellW, cellH);
        Uniform2AtlasSize((float)atlasWidth, (float)atlasHeight);
        Uniform1Underline(underlineY);
        Uniform1Strike(strikeY);
        Uniform1LineHalf(lineHalf);
        BindAtlasTexture(texId);
        Uniform1AtlasSampler();

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
        if (cellCount > 0 && _instanceBufferDirty)
        {
            // Worst case: every cell has a decoration instance appended.
            int maxInstances = checked(cellCount * 2);
            int maxFloats = checked(maxInstances * FloatsPerInstance);
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

            int uploadBytes = checked(outputInstanceCount * FloatsPerInstance * sizeof(float));
            BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
            EnsureBufferCapacity(ref _instanceBufferCapacityBytes, uploadBytes);
            fixed (float* fp = stagingArr)
            {
                // Orphan the retained allocation, then transfer only the used
                // range. This avoids reallocating/copying unused capacity.
                _gl.BufferData(
                    BufferTargetARB.ArrayBuffer,
                    (nuint)_instanceBufferCapacityBytes,
                    (void*)0,
                    BufferUsageARB.DynamicDraw);
                if (uploadBytes > 0)
                {
                    _gl.BufferSubData(
                        BufferTargetARB.ArrayBuffer,
                        0,
                        (nuint)uploadBytes,
                        fp);
                }
            }

            _drawInstanceCount = outputInstanceCount;
            _drawMenuStart = menuOutputStart;
            _drawMenuCount = menuOutputStart >= 0 ? outputInstanceCount - menuOutputStart : 0;
            if (_drawMenuStart >= 0)
            {
                EnsureMenuInstanceAttribs(_drawMenuStart);
            }
            _stagedMenuInstanceStart = menuInstanceStart;
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

        UploadChrome(chromeQuads, menuChromeStart);

        DrawCellRange(0, baseInstanceCount, pass: 0, menuVao: false);
        if (baseChromeCount > 0)
        {
            DrawChromeRange(0, baseChromeCount, menuVao: false);
        }

        DrawCellRange(0, baseInstanceCount, pass: 1, menuVao: false);
        if (baseChromeCount < chromeQuads.Length)
        {
            EnsureMenuChromeAttribs(baseChromeCount);
            DrawChromeRange(baseChromeCount, chromeQuads.Length - baseChromeCount, menuVao: true);
        }

        if (hasMenuOverlay && _drawMenuCount > 0)
        {
            DrawCellRange(_drawMenuStart, _drawMenuCount, pass: 1, menuVao: true);
        }
    }

    private void DrawCellRange(int firstInstance, int instanceCount, int pass, bool menuVao)
    {
        if (instanceCount <= 0)
            return;

        UseProgram(_program);
        BindVertexArray(menuVao ? _menuVao : _vao);
        Uniform1Pass(pass);
        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedShort,
            null,
            (uint)instanceCount);
    }

    private void UploadChrome(ReadOnlySpan<ChromeQuadInstance> chromeQuads, int menuChromeStart)
    {
        if (menuChromeStart != _stagedChromeMenuStart)
        {
            // The menu VAO points at the first menu quad. Its attribute
            // pointers must be reconsidered even when the retained geometry
            // itself is unchanged.
            _chromeMenuAttribStart = -1;
            _stagedChromeMenuStart = menuChromeStart;
        }

        int count = chromeQuads.Length;
        bool changed = count != _lastChromeCount
            || !ChromeQuadsEqual(chromeQuads);
        if (!changed)
            return;

        _lastChromeCount = count;
        if (_lastChromeQuads.Length < count)
        {
            _lastChromeQuads = new ChromeQuadInstance[count];
        }

        if (count > 0)
        {
            chromeQuads.CopyTo(_lastChromeQuads);
        }
        else
        {
            // An empty frame intentionally leaves the old allocation in
            // place. No draw is issued, and a later non-empty frame will
            // detect the count transition and replace its retained data.
            return;
        }

        int floats = checked(count * ChromeFloatsPerInstance);
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

        int uploadBytes = checked(floats * sizeof(float));
        BindVertexArray(_chromeVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _chromeInstanceVbo);
        EnsureBufferCapacity(ref _chromeBufferCapacityBytes, uploadBytes);
        fixed (float* fp = staging)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)_chromeBufferCapacityBytes,
                (void*)0,
                BufferUsageARB.DynamicDraw);
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)uploadBytes,
                fp);
        }
    }

    private bool ChromeQuadsEqual(ReadOnlySpan<ChromeQuadInstance> chromeQuads)
    {
        if (_lastChromeCount != chromeQuads.Length)
            return false;

        for (int i = 0; i < chromeQuads.Length; i++)
        {
            ref readonly var current = ref chromeQuads[i];
            ref readonly var previous = ref _lastChromeQuads[i];
            if (!ChromeFloatEquals(current.X, previous.X)
                || !ChromeFloatEquals(current.Y, previous.Y)
                || !ChromeFloatEquals(current.W, previous.W)
                || !ChromeFloatEquals(current.H, previous.H)
                || !ChromeFloatEquals(current.Radius, previous.Radius)
                || !ChromeFloatEquals(current.Blur, previous.Blur)
                || !ChromeFloatEquals(current.TopR, previous.TopR)
                || !ChromeFloatEquals(current.TopG, previous.TopG)
                || !ChromeFloatEquals(current.TopB, previous.TopB)
                || !ChromeFloatEquals(current.TopA, previous.TopA)
                || !ChromeFloatEquals(current.BottomR, previous.BottomR)
                || !ChromeFloatEquals(current.BottomG, previous.BottomG)
                || !ChromeFloatEquals(current.BottomB, previous.BottomB)
                || !ChromeFloatEquals(current.BottomA, previous.BottomA))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ChromeFloatEquals(float left, float right)
        => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private void DrawChromeRange(int firstInstance, int instanceCount, bool menuVao)
    {
        if (instanceCount <= 0)
            return;

        UseProgram(_chromeProgram);
        Uniform2ChromeFramebuffer(_lastFramebufferWidth, _lastFramebufferHeight);
        BindVertexArray(menuVao ? _chromeMenuVao : _chromeVao);
        _gl.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedShort,
            null,
            (uint)instanceCount);
    }



    private static void EnsureBufferCapacity(ref int capacityBytes, int requiredBytes)
    {
        if (requiredBytes <= capacityBytes)
            return;

        int capacity = capacityBytes == 0 ? 4096 : capacityBytes;
        while (capacity < requiredBytes)
        {
            capacity = capacity <= int.MaxValue / 2
                ? capacity * 2
                : requiredBytes;
        }

        capacityBytes = capacity;
    }

    private void UseProgram(uint program)
    {
        if (_boundProgram != program)
        {
            _gl.UseProgram(program);
            _boundProgram = program;
        }
    }

    private void BindVertexArray(uint vao)
    {
        if (_boundVao != vao)
        {
            _gl.BindVertexArray(vao);
            _boundVao = vao;
        }
    }

    private void BindAtlasTexture(uint texture)
    {
        if (!_activeTextureSet || _activeTextureUnit != TextureUnit.Texture0)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _activeTextureUnit = TextureUnit.Texture0;
            _activeTextureSet = true;
        }

        if (_boundTexture != texture)
        {
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _boundTexture = texture;
        }
    }

    private void Uniform2CellFramebuffer(float width, float height)
    {
        if (_cellFramebufferSet && _cachedCellFramebufferW == width && _cachedCellFramebufferH == height)
            return;

        _gl.Uniform2(_uFramebufferPx, width, height);
        _cachedCellFramebufferW = width;
        _cachedCellFramebufferH = height;
        _cellFramebufferSet = true;
    }

    private void Uniform2CellSize(float width, float height)
    {
        if (_cellSizeSet && _cachedCellW == width && _cachedCellH == height)
            return;

        _gl.Uniform2(_uCellPx, width, height);
        _cachedCellW = width;
        _cachedCellH = height;
        _cellSizeSet = true;
    }

    private void Uniform2AtlasSize(float width, float height)
    {
        if (_atlasSizeSet && _cachedAtlasW == width && _cachedAtlasH == height)
            return;

        _gl.Uniform2(_uAtlasSize, width, height);
        _cachedAtlasW = width;
        _cachedAtlasH = height;

        _atlasSizeSet = true;
    }

    private void Uniform1Underline(float value)
    {
        if (_underlineSet && _cachedUnderline == value)
            return;

        _gl.Uniform1(_uUnderlineY, value);
        _cachedUnderline = value;
        _underlineSet = true;
    }

    private void Uniform1Strike(float value)
    {
        if (_strikeSet && _cachedStrike == value)
            return;

        _gl.Uniform1(_uStrikeY, value);
        _cachedStrike = value;
        _strikeSet = true;
    }

    private void Uniform1LineHalf(float value)
    {
        if (_lineHalfSet && _cachedLineHalf == value)
            return;

        _gl.Uniform1(_uLineHalf, value);
        _cachedLineHalf = value;
        _lineHalfSet = true;
    }

    private void Uniform1Pass(int pass)
    {
        if (_passSet && _cachedPass == pass)
            return;

        _gl.Uniform1(_uPass, pass);
        _cachedPass = pass;
        _passSet = true;
    }

    private void Uniform1AtlasSampler()
    {
        if (_atlasSamplerSet)
            return;

        _gl.Uniform1(_uAtlas, 0);
        _atlasSamplerSet = true;
    }

    private void Uniform2ChromeFramebuffer(float width, float height)
    {
        if (_chromeFramebufferSet && _cachedChromeFramebufferW == width && _cachedChromeFramebufferH == height)
            return;

        _gl.Uniform2(_uChromeFramebufferPx, width, height);
        _cachedChromeFramebufferW = width;
        _cachedChromeFramebufferH = height;
        _chromeFramebufferSet = true;
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void InvalidateGlStateCache()
    {
        _boundProgram = uint.MaxValue;
        _boundVao = uint.MaxValue;
        _boundTexture = uint.MaxValue;
        _activeTextureSet = false;
        _cellFramebufferSet = false;
        _cellSizeSet = false;
        _atlasSizeSet = false;
        _underlineSet = false;
        _strikeSet = false;
        _lineHalfSet = false;
        _passSet = false;
        _atlasSamplerSet = false;
        _chromeFramebufferSet = false;
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
            if (_menuVao != 0)
            {
                _gl.DeleteVertexArray(_menuVao);
                _menuVao = 0;
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
            if (_chromeMenuVao != 0)
            {
                _gl.DeleteVertexArray(_chromeMenuVao);
                _chromeMenuVao = 0;
            }


            InvalidateGlStateCache();

            _disposed = true;
        }
    }
}
