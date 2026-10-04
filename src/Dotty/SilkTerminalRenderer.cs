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
    private int _uColorAtlasSize;
    private int _uColorAtlas;
    private bool _colorAtlasSizeSet;
    private float _cachedColorAtlasW;
    private float _cachedColorAtlasH;
    private bool _colorAtlasSamplerSet;
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
    private int[] _sourceOutputStarts = Array.Empty<int>();
    private int[] _sourceOutputCounts = Array.Empty<int>();
    private int[] _dirtyInstanceRanges = Array.Empty<int>();
    private int _dirtyInstanceRangeCount;
    private int _stagedOutputInstanceCount;
    private bool _fullInstanceUpload = true;
    private float _stagedPaddingLeft = float.NaN;
    private float _stagedPaddingTop = float.NaN;
    private int _stagedBarRows = -1;
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
    private uint _boundColorTexture = uint.MaxValue;
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
    /// <summary>Estimated uploaded R8 pixel payload; not actual driver RSS.</summary>
    public long R8TexturePayloadBytes => TextureManager.R8TexturePayloadBytes;
    /// <summary>Estimated uploaded RGBA8 pixel payload; not actual driver RSS.</summary>
    public long Rgba8TexturePayloadBytes => TextureManager.Rgba8TexturePayloadBytes;
    /// <summary>Process-wide retained native pixel storage for color-glyph probes.</summary>
    public static long IntrinsicColorProbeScratchBytes => GlyphAtlas.IntrinsicColorProbeScratchBytes;

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
        _uColorAtlasSize = _gl.GetUniformLocation(_program, "uColorAtlasSize");
        _uColorAtlas = _gl.GetUniformLocation(_program, "uColorAtlas");
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
        int scrollbarChromeStart = -1,
        int menuInstanceStart = -1,
        int menuChromeStart = -1)
    {
        EnsureNotDisposed();

        if (frameCaptured)
            CaptureInstances(instances, cellW, cellH, paddingLeft, paddingTop, barRows, menuInstanceStart);

        if (cellW != _stagedCellW || cellH != _stagedCellH
            || paddingLeft != _stagedPaddingLeft || paddingTop != _stagedPaddingTop
            || barRows != _stagedBarRows)
        {
            _instanceBufferDirty = true;
            _fullInstanceUpload = true;
        }
        if (menuInstanceStart != _stagedMenuInstanceStart)
        {
            _instanceBufferDirty = true;
            _fullInstanceUpload = true;
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
        Uniform2ColorAtlasSize(Math.Max(1, TextureManager.Atlas.ColorWidth), Math.Max(1, TextureManager.Atlas.ColorHeight));
        Uniform1Underline(underlineY);
        Uniform1Strike(strikeY);
        Uniform1LineHalf(lineHalf);
        BindAtlasTexture(texId);
        Uniform1AtlasSampler();
        BindColorAtlasTexture(TextureManager.ColorTextureId);
        if (!_colorAtlasSamplerSet)
        {
            _gl.Uniform1(_uColorAtlas, 1);
            _colorAtlasSamplerSet = true;
        }
        UploadAndDraw(
            cellW,
            cellH,
            paddingLeft,
            paddingTop,
            barRows,
            chromeQuads,
            scrollbarChromeStart,
            menuInstanceStart,
            menuChromeStart);
    }
    private void CaptureInstances(
        ReadOnlySpan<CellInstance> instances,
        float cellW,
        float cellH,
        float paddingLeft,
        float paddingTop,
        int barRows,
        int menuInstanceStart)
    {
        bool compatible = !_instanceBufferDirty
            && _lastInstanceCount == instances.Length
            && _lastInstances.Length >= instances.Length
            && cellW == _stagedCellW
            && cellH == _stagedCellH
            && paddingLeft == _stagedPaddingLeft
            && paddingTop == _stagedPaddingTop
            && barRows == _stagedBarRows
            && menuInstanceStart == _stagedMenuInstanceStart;
        EnsureLastInstanceCapacity(instances.Length);
        if (!compatible)
        {
            instances.CopyTo(_lastInstances);
            _lastInstanceCount = instances.Length;
            _dirtyInstanceRangeCount = 0;
            _instanceBufferDirty = true;
            _fullInstanceUpload = true;
            return;
        }

        EnsureOutputMapCapacity(instances.Length);
        _dirtyInstanceRangeCount = 0;
        int outputInstance = 0;
        int dirtyStart = -1;
        bool layoutChanged = false;
        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly var current = ref instances[i];
            ref readonly var previous = ref _lastInstances[i];
            bool hasDecoration = (current.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) != 0;
            bool hadDecoration = (previous.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) != 0;
            if (hasDecoration != hadDecoration)
            {
                layoutChanged = true;
                break;
            }

            _sourceOutputStarts[i] = outputInstance;
            int outputCount = hasDecoration ? 2 : 1;
            _sourceOutputCounts[i] = outputCount;
            outputInstance += outputCount;

            if (!CellInstancesEqual(current, previous))
            {
                if (dirtyStart < 0)
                    dirtyStart = i;
            }
            else if (dirtyStart >= 0)
            {
                AddDirtyInstanceRange(dirtyStart, i);
                dirtyStart = -1;
            }
        }

        if (layoutChanged)
        {
            instances.CopyTo(_lastInstances);
            _lastInstanceCount = instances.Length;
            _dirtyInstanceRangeCount = 0;
            _instanceBufferDirty = true;
            _fullInstanceUpload = true;
            return;
        }

        if (dirtyStart >= 0)
            AddDirtyInstanceRange(dirtyStart, instances.Length);

        _stagedOutputInstanceCount = outputInstance;
        _lastInstanceCount = instances.Length;
        if (_dirtyInstanceRangeCount == 0)
        {
            _instanceBufferDirty = false;
            _fullInstanceUpload = false;
            return;
        }

        for (int range = 0; range < _dirtyInstanceRangeCount; range++)
        {
            int start = _dirtyInstanceRanges[range * 2];
            int end = _dirtyInstanceRanges[range * 2 + 1];
            instances.Slice(start, end - start).CopyTo(_lastInstances.AsSpan(start));
        }
        _instanceBufferDirty = true;
        _fullInstanceUpload = false;
    }

    private void EnsureLastInstanceCapacity(int required)
    {
        if (_lastInstances.Length >= required)
            return;
        int capacity = Math.Max(required, _lastInstances.Length == 0 ? 4096 : _lastInstances.Length * 2);
        _lastInstances = new CellInstance[capacity];
    }

    private void EnsureOutputMapCapacity(int required)
    {
        if (_sourceOutputStarts.Length < required)
        {
            int capacity = Math.Max(required, _sourceOutputStarts.Length == 0 ? 4096 : _sourceOutputStarts.Length * 2);
            Array.Resize(ref _sourceOutputStarts, capacity);
            Array.Resize(ref _sourceOutputCounts, capacity);
        }
    }

    private void AddDirtyInstanceRange(int start, int end)
    {
        int needed = checked((_dirtyInstanceRangeCount + 1) * 2);
        if (_dirtyInstanceRanges.Length < needed)
        {
            int capacity = Math.Max(needed, _dirtyInstanceRanges.Length == 0 ? 16 : _dirtyInstanceRanges.Length * 2);
            Array.Resize(ref _dirtyInstanceRanges, capacity);
        }
        _dirtyInstanceRanges[_dirtyInstanceRangeCount * 2] = start;
        _dirtyInstanceRanges[_dirtyInstanceRangeCount * 2 + 1] = end;
        _dirtyInstanceRangeCount++;
    }

    private static bool CellInstancesEqual(in CellInstance left, in CellInstance right) =>
        left.Col == right.Col
        && left.Row == right.Row
        && left.GlyphX == right.GlyphX
        && left.GlyphY == right.GlyphY
        && left.GlyphW == right.GlyphW
        && left.GlyphH == right.GlyphH
        && left.OffX == right.OffX
        && left.OffY == right.OffY
        && left.FgR == right.FgR
        && left.FgG == right.FgG
        && left.FgB == right.FgB
        && left.FgA == right.FgA
        && left.Flags == right.Flags
        && left.BgR == right.BgR
        && left.BgG == right.BgG
        && left.BgB == right.BgB
        && left.BgA == right.BgA;

    private void UploadAndDraw(
        float cellW,
        float cellH,
        float paddingLeft,
        float paddingTop,
        int barRows,
        ReadOnlySpan<ChromeQuadInstance> chromeQuads,
        int scrollbarChromeStart,
        int menuInstanceStart,
        int menuChromeStart)
    {
        int cellCount = _lastInstanceCount;
        if (cellCount > 0 && _instanceBufferDirty)
        {
            int maxInstances = checked(cellCount * 2);
            int maxFloats = checked(maxInstances * FloatsPerInstance);
            if (_staging.Length < maxFloats)
            {
                int capacity = Math.Max(maxFloats, _staging.Length == 0 ? 4096 : _staging.Length * 2);
                _staging = new float[capacity];
            }

            int outputInstanceCount = _stagedOutputInstanceCount;
            if (_fullInstanceUpload)
            {
                EnsureOutputMapCapacity(cellCount);
                outputInstanceCount = 0;
                for (int i = 0; i < cellCount; i++)
                {
                    _sourceOutputStarts[i] = outputInstanceCount;
                    ref readonly var instance = ref _lastInstances[i];
                    bool decorated = (instance.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) != 0;
                    _sourceOutputCounts[i] = decorated ? 2 : 1;
                    outputInstanceCount += _sourceOutputCounts[i];
                }
            }

            int uploadBytes = checked(outputInstanceCount * FloatsPerInstance * sizeof(float));
            if (!_fullInstanceUpload && uploadBytes > _instanceBufferCapacityBytes)
                _fullInstanceUpload = true;

            float[] stagingArr = _staging;
            if (_fullInstanceUpload)
            {
                for (int i = 0; i < cellCount; i++)
                {
                    ref readonly var instance = ref _lastInstances[i];
                    WriteCellInstance(stagingArr, _sourceOutputStarts[i], instance,
                        cellW, cellH, paddingLeft, paddingTop, barRows);
                }
            }
            else
            {
                for (int range = 0; range < _dirtyInstanceRangeCount; range++)
                {
                    int sourceStart = _dirtyInstanceRanges[range * 2];
                    int sourceEnd = _dirtyInstanceRanges[range * 2 + 1];
                    for (int i = sourceStart; i < sourceEnd; i++)
                    {
                        ref readonly var instance = ref _lastInstances[i];
                        WriteCellInstance(stagingArr, _sourceOutputStarts[i], instance,
                            cellW, cellH, paddingLeft, paddingTop, barRows);
                    }
                }
            }

            int clampedMenuStart = menuInstanceStart < 0
                ? -1
                : Math.Clamp(menuInstanceStart, 0, cellCount);
            int menuOutputStart = clampedMenuStart < 0
                ? -1
                : clampedMenuStart == cellCount
                    ? outputInstanceCount
                    : _sourceOutputStarts[clampedMenuStart];

            BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
            EnsureBufferCapacity(ref _instanceBufferCapacityBytes, uploadBytes);
            fixed (float* fp = stagingArr)
            {
                if (_fullInstanceUpload)
                {
                    // Orphaning is only valid with a complete replacement upload.
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
                else
                {
                    for (int range = 0; range < _dirtyInstanceRangeCount; range++)
                    {
                        int sourceStart = _dirtyInstanceRanges[range * 2];
                        int sourceEnd = _dirtyInstanceRanges[range * 2 + 1];
                        int outputStart = _sourceOutputStarts[sourceStart];
                        int outputEnd = _sourceOutputStarts[sourceEnd - 1] + _sourceOutputCounts[sourceEnd - 1];
                        int outputCount = outputEnd - outputStart;
                        int floatOffset = checked(outputStart * FloatsPerInstance);
                        int byteCount = checked(outputCount * FloatsPerInstance * sizeof(float));
                        _gl.BufferSubData(
                            BufferTargetARB.ArrayBuffer,
                            checked(floatOffset * sizeof(float)),
                            (nuint)byteCount,
                            fp + floatOffset);
                    }
                }
            }

            _stagedOutputInstanceCount = outputInstanceCount;
            _drawInstanceCount = outputInstanceCount;
            _drawMenuStart = menuOutputStart;
            _drawMenuCount = menuOutputStart >= 0 ? outputInstanceCount - menuOutputStart : 0;
            if (_drawMenuStart >= 0)
                EnsureMenuInstanceAttribs(_drawMenuStart);
            _stagedMenuInstanceStart = menuInstanceStart;
            _stagedCellW = cellW;
            _stagedCellH = cellH;
            _stagedPaddingLeft = paddingLeft;
            _stagedPaddingTop = paddingTop;
            _stagedBarRows = barRows;
            _instanceBufferDirty = false;
            _fullInstanceUpload = false;
            _dirtyInstanceRangeCount = 0;
        }
        else if (cellCount == 0 && _instanceBufferDirty)
        {
            _drawInstanceCount = 0;
            _drawMenuStart = menuInstanceStart < 0 ? -1 : 0;
            _drawMenuCount = 0;
            _stagedOutputInstanceCount = 0;
            _stagedMenuInstanceStart = menuInstanceStart;
            _stagedCellW = cellW;
            _stagedCellH = cellH;
            _stagedPaddingLeft = paddingLeft;
            _stagedPaddingTop = paddingTop;
            _stagedBarRows = barRows;
            _instanceBufferDirty = false;
            _fullInstanceUpload = true;
            _dirtyInstanceRangeCount = 0;
        }

        bool hasMenuOverlay = menuInstanceStart >= 0
            && menuChromeStart >= 0
            && menuInstanceStart <= cellCount
            && _drawMenuStart >= 0
            && _drawMenuStart <= _drawInstanceCount;
        int menuChromeFirst = hasMenuOverlay
            ? Math.Clamp(menuChromeStart, 0, chromeQuads.Length)
            : chromeQuads.Length;
        int scrollbarChromeFirst = scrollbarChromeStart >= 0
            ? Math.Clamp(scrollbarChromeStart, 0, menuChromeFirst)
            : menuChromeFirst;
        int baseInstanceCount = hasMenuOverlay ? _drawMenuStart : _drawInstanceCount;

        UploadChrome(chromeQuads, menuChromeStart);

        // Base backgrounds/chrome/glyphs are drawn first. Scrollbar chrome is
        // a separate overlay so rightmost glyphs can never cover the thumb.
        DrawCellRange(0, baseInstanceCount, pass: 0, menuVao: false);
        int baseChromeCount = scrollbarChromeFirst;
        if (baseChromeCount > 0)
        {
            DrawChromeRange(0, baseChromeCount, menuVao: false);
        }

        DrawCellRange(0, baseInstanceCount, pass: 1, menuVao: false);
        int scrollbarCount = menuChromeFirst - scrollbarChromeFirst;
        if (scrollbarCount > 0)
        {
            EnsureMenuChromeAttribs(scrollbarChromeFirst);
            DrawChromeRange(scrollbarChromeFirst, scrollbarCount, menuVao: true);
        }

        int menuChromeCount = chromeQuads.Length - menuChromeFirst;
        if (menuChromeCount > 0)
        {
            EnsureMenuChromeAttribs(menuChromeFirst);
            DrawChromeRange(menuChromeFirst, menuChromeCount, menuVao: true);
        }

        if (hasMenuOverlay && _drawMenuCount > 0)
        {
            DrawCellRange(_drawMenuStart, _drawMenuCount, pass: 1, menuVao: true);
        }
    }

    private static void WriteCellInstance(
        float[] staging,
        int outputIndex,
        in CellInstance cell,
        float cellW,
        float cellH,
        float paddingLeft,
        float paddingTop,
        int barRows)
    {
        float x = cell.Row >= barRows ? paddingLeft + cell.Col * cellW : cell.Col * cellW;
        float y = cell.Row >= barRows ? paddingTop + cell.Row * cellH : cell.Row * cellH;
        int offset = outputIndex * FloatsPerInstance;
        staging[offset] = x;
        staging[offset + 1] = y;
        staging[offset + 2] = cell.GlyphX;
        staging[offset + 3] = cell.GlyphY;
        staging[offset + 4] = cell.GlyphW;
        staging[offset + 5] = cell.GlyphH;
        staging[offset + 6] = 0f;
        staging[offset + 7] = cell.OffY;
        staging[offset + 8] = cell.OffX;
        staging[offset + 9] = 0f;
        staging[offset + 10] = cell.FgR / 255f;
        staging[offset + 11] = cell.FgG / 255f;
        staging[offset + 12] = cell.FgB / 255f;
        staging[offset + 13] = cell.FgA / 255f;
        staging[offset + 14] = cell.BgR / 255f;
        staging[offset + 15] = cell.BgG / 255f;
        staging[offset + 16] = cell.BgB / 255f;
        staging[offset + 17] = cell.BgA / 255f;
        staging[offset + 18] = BitConverter.UInt32BitsToSingle(cell.Flags);

        if ((cell.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) == 0)
            return;

        int decoration = (outputIndex + 1) * FloatsPerInstance;
        float decorationWidth = (cell.Flags & CellFlags.WideCell) != 0 ? cellW * 2f : cellW;
        staging[decoration] = x;
        staging[decoration + 1] = y;
        staging[decoration + 2] = 0f;
        staging[decoration + 3] = 0f;
        staging[decoration + 4] = decorationWidth;
        staging[decoration + 5] = cellH;
        staging[decoration + 6] = 0f;
        staging[decoration + 7] = 0f;
        staging[decoration + 8] = 0f;
        staging[decoration + 9] = 0f;
        staging[decoration + 10] = cell.FgR / 255f;
        staging[decoration + 11] = cell.FgG / 255f;
        staging[decoration + 12] = cell.FgB / 255f;
        staging[decoration + 13] = cell.FgA / 255f;
        staging[decoration + 14] = 0f;
        staging[decoration + 15] = 0f;
        staging[decoration + 16] = 0f;
        staging[decoration + 17] = 0f;
        staging[decoration + 18] = BitConverter.UInt32BitsToSingle((uint)(cell.Flags | CellFlags.DecorOnly));
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
    private void BindColorAtlasTexture(uint texture)
    {
        if (!_activeTextureSet || _activeTextureUnit != TextureUnit.Texture1)
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _activeTextureUnit = TextureUnit.Texture1;
            _activeTextureSet = true;
        }
        if (_boundColorTexture != texture)
        {
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _boundColorTexture = texture;
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
        _activeTextureUnit = TextureUnit.Texture0;
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
    private void Uniform2ColorAtlasSize(float width, float height)
    {
        if (_colorAtlasSizeSet && _cachedColorAtlasW == width && _cachedColorAtlasH == height)
            return;

        _gl.Uniform2(_uColorAtlasSize, width, height);
        _cachedColorAtlasW = width;
        _cachedColorAtlasH = height;
        _colorAtlasSizeSet = true;
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
        _boundColorTexture = uint.MaxValue;
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
