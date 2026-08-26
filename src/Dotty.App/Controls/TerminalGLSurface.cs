using System;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.Controls;
using Avalonia.OpenGL.Controls;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// GPU terminal surface on <see cref="OpenGlControlBase"/>: owns the GL
/// program, atlas texture, and instance buffers; draws the latest snapshot
/// with two instanced passes (cell backgrounds, then glyphs).
///
/// Unlike the lease/custom-draw-op path (which fights the compositor and its
/// render thread stalls under sparse updates — see TerminalCanvas), this
/// control is driven by Avalonia's render loop directly:
/// Present(snapshot) → RequestNextFrameRendering → OnOpenGlRender.
///
/// Render-thread confined except Present/RequestRender which hand off via a
/// volatile field. Falls back gracefully: if GL init fails, Failed=true and
/// the hosting canvas keeps the bitmap path.
/// </summary>
public class TerminalGLSurface : OpenGlControlBase
{
    private GlInterface? _gl;
    private GLShaderProgram? _program;
    private GLTextureManager? _textureManager;

    private int _cornerVbo = -1;
    private int _instanceVbo = -1;
    private int _ebo = -1;
    private int _instanceCapacity;

    // Uniform locations (resolved at init)
    private int _uFramebufferPx = -1;
    private int _uCellPx = -1;
    private int _uAtlasSize = -1;
    private int _uPass = -1;
    private int _uAtlas = -1;
    private int _uUnderlineY = -1;
    private int _uStrikeY = -1;
    private int _uLineHalf = -1;

    private float[] _staging = Array.Empty<float>();

    // UI → render thread handoff
    private volatile RenderSnapshot? _pendingSnapshot;
    private SKTypeface? _pendingTypeface;
    private float _pendingTextSize;
    private volatile bool _geometryDirty;

    /// <summary>True when GL initialization failed — host must not use this surface.</summary>
    public bool Failed { get; private set; }

    /// <summary>Frame counter for diagnostics.</summary>
    public long FramesRendered { get; private set; }
    public long InitFailures { get; private set; }

    private float _cellW = 10f;
    private float _cellH = 20f;
    private double _underlineY = 0.8;
    private double _strikeY = 0.55;
    private double _lineHalf = 0.04;

    /// <summary>Sets decoration bar positions as fractions of cell height
    /// (computed from font metrics by the host).</summary>
    public void SetLineMetrics(double underlineY, double strikeY, double lineHalf)
    {
        _underlineY = underlineY;
        _strikeY = strikeY;
        _lineHalf = lineHalf;
    }
    private float _offsetX;
    private float _offsetY;
    private SgrColorArgb _defaultFg = SgrColorArgb.FromRgb(255, 255, 255);
    private SgrColorArgb _defaultBg = SgrColorArgb.FromRgb(0, 0, 0);

    /// <summary>
    /// Presents a snapshot: hands it to the render thread and schedules
    /// OnOpenGlRender. Called from the UI thread's presentation frame.
    /// </summary>
    public void Present(
        RenderSnapshot snapshot,
        SKTypeface typeface,
        float textSize,
        float cellW,
        float cellH,
        float offsetX,
        float offsetY,
        SgrColorArgb defaultFg,
        SgrColorArgb defaultBg)
    {
        _pendingSnapshot = snapshot;
        _pendingTypeface = typeface;
        _pendingTextSize = textSize;
        _cellW = cellW;
        _cellH = cellH;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _defaultFg = defaultFg;
        _defaultBg = defaultBg;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        Console.Error.WriteLine($"[GL] OnOpenGlInit: version={gl.Version}");
        try
        {
            _gl = gl;
            _program = new GLShaderProgram(gl, GLShaderProgram.VERTEX_SHADER, GLShaderProgram.FRAGMENT_SHADER);
            _textureManager = new GLTextureManager(gl, _atlas ?? throw new InvalidOperationException("atlas not set"));

            _uFramebufferPx = _program.GetUniformLocation("uFramebufferPx");
            _uCellPx = _program.GetUniformLocation("uCellPx");
            _uAtlasSize = _program.GetUniformLocation("uAtlasSize");
            _uPass = _program.GetUniformLocation("uPass");
            _uAtlas = _program.GetUniformLocation("uAtlas");
            _uUnderlineY = _program.GetUniformLocation("uUnderlineY");
            _uStrikeY = _program.GetUniformLocation("uStrikeY");
            _uLineHalf = _program.GetUniformLocation("uLineHalf");

            gl.Enable(GL_BLEND);
            var blendFuncPtr = gl.GetProcAddress("glBlendFunc");
            if (blendFuncPtr != IntPtr.Zero)
            {
                var blendFunc = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<BlendFuncDelegate>(blendFuncPtr);
                blendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA); // premultiplied-over
            }

            var drawInstPtr = gl.GetProcAddress("glDrawElementsInstanced");
            if (drawInstPtr != IntPtr.Zero)
                _drawInstanced = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DrawElementsInstancedDelegate>(drawInstPtr);

            InitBuffers(gl);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GL] init failed: {ex.Message}");
            Failed = true;
        }
    }

    private GlyphAtlas? _atlas;

    /// <summary>Atlas handed in before attach (the canvas's acquired atlas).</summary>
    public void SetAtlas(GlyphAtlas atlas) => _atlas = atlas;

    private unsafe void InitBuffers(GlInterface gl)
    {
        // Corner VBO: (0,0)(1,0)(1,1)(0,1)
        _cornerVbo = gl.GenBuffer();
        gl.BindBuffer(GLConsts.GL_ARRAY_BUFFER, _cornerVbo);
        var corners = stackalloc float[8] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
        gl.BufferData(GLConsts.GL_ARRAY_BUFFER, new IntPtr(8 * sizeof(float)), new IntPtr(corners), GLConsts.GL_STATIC_DRAW);

        // Index buffer: two triangles per corner-quad
        _ebo = gl.GenBuffer();
        gl.BindBuffer(GLConsts.GL_ELEMENT_ARRAY_BUFFER, _ebo);
        var indices = stackalloc ushort[6] { 0, 1, 2, 0, 2, 3 };
        gl.BufferData(GLConsts.GL_ELEMENT_ARRAY_BUFFER, new IntPtr(6 * sizeof(ushort)), new IntPtr(indices), GLConsts.GL_STATIC_DRAW);

        // Instance VBO (data uploaded per frame)
        _instanceVbo = gl.GenBuffer();
        gl.BindBuffer(GLConsts.GL_ARRAY_BUFFER, _instanceVbo);

        _program!.Use();

        // loc 0: aCorner vec2 — per-vertex
        gl.VertexAttribPointer(0, 2, GLConsts.GL_FLOAT, 0, 8, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);

    }

    private const int GL_ARRAY_BUFFER = 0x8892;
    private const int GL_ELEMENT_ARRAY_BUFFER = 0x8089;
    private const int GL_STATIC_DRAW = 0x88E4;
    private const int GL_DYNAMIC_DRAW = 0x88E8;
    private const int GL_FLOAT = 0x1406;
    private const int GL_UNSIGNED_INT_2_10_10_10_REV = 0x8368;

    protected override void OnOpenGlRender(GlInterface gl, int deltaTime)
    {
        FramesRendered++;
        var snapshot = _pendingSnapshot;
        var typeface = _pendingTypeface;
        if (snapshot == null || typeface == null || _program == null || _textureManager == null || _gl == null)
        {
            // Nothing to draw: clear to background so the surface never shows garbage
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int fbW = Math.Max(1, (int)(Bounds.Width * scaling));
        int fbH = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, fbW, fbH);

        float bgR = _defaultBg.R / 255f;
        float bgG = _defaultBg.G / 255f;
        float bgB = _defaultBg.B / 255f;
        _gl.ClearColor(bgR, bgG, bgB, 1f);
        _gl.Clear(GLConsts.GL_COLOR_BUFFER_BIT);

        // Atlas texture (ContentVersion-keyed update inside)
        int texId = _textureManager.UpdateTexture();
        float atlasW = _atlas!.Width;
        float atlasH = _atlas.Height;

        // Build instances from the snapshot
        int rows = snapshot.Rows;
        int cols = snapshot.Columns;
        var result = QuadFrameBuilder.Build(
            snapshot, _atlas!, typeface, _pendingTextSize,
            new FrameGeometry(_cellW, _cellH, rows, cols, _offsetX, _offsetY),
            _defaultFg, _defaultBg);

        _program.Use();
        _program.SetUniform2f(_uFramebufferPx, fbW, fbH);
        _program.SetUniform2f(_uCellPx, _cellW, _cellH);
        _program.SetUniform2f(_uAtlasSize, atlasW, atlasH);
        _gl.Uniform1f(_uUnderlineY, (float)_underlineY);
        _gl.Uniform1f(_uStrikeY, (float)_strikeY);
        _gl.Uniform1f(_uLineHalf, (float)_lineHalf);
        _gl.Uniform1i(_uAtlas, 0);
        _gl.ActiveTexture(GLConsts.GL_TEXTURE0);
        _gl.BindTexture(GLTextureManager.GL_TEXTURE_2D, texId);

        _gl.BindBuffer(GL_ARRAY_BUFFER, _instanceVbo);
        UploadAndDrawInstances(gl, result.AsSpan(), rows, cols);
    }

    private unsafe void UploadAndDrawInstances(GlInterface gl, ReadOnlySpan<CellInstance> instances, int rows, int cols)
    {
        // Stage interleaved float vertex data per instance:
        // [0..1] grid px origin (x, y)
        // [2..5] atlas rect (x, y, w, h)
        // [6..9] metrics (0, offY, offX, 0)
        // [10..12] fg rgb
        // [13..15] bg rgb
        // [16] flags (wide=2 → wide; inverse handled CPU-side already)
        int floatsPerInstance = 17;
        int count = instances.Length;
        int needed = count * floatsPerInstance;
        if (_staging.Length < needed)
            _staging = new float[Math.Max(needed, _staging.Length * 2)];
        float[] stagingArr = _staging;

        for (int i = 0; i < count; i++)
        {
            var c = instances[i];
                float x = c.Col * _cellW + _offsetX;
                float y = c.Row * _cellH + _offsetY;
                int o = i * floatsPerInstance;
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
                stagingArr[o + 13] = c.BgR / 255f;
                stagingArr[o + 14] = c.BgG / 255f;
                stagingArr[o + 15] = c.BgB / 255f;
                stagingArr[o + 16] = c.Flags;

                // Decorated cell: extra decor-only instance (bar quad over
                // the full cell; the glyph instance above draws the glyph).
                if ((c.Flags & (CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline)) != 0
                    && _staging.Length >= needed + floatsPerInstance)
                {
                    int d = needed;
                    stagingArr[d] = x;
                    stagingArr[d + 1] = y;
                    stagingArr[d + 2] = 0f;
                    stagingArr[d + 3] = 0f;
                    stagingArr[d + 4] = _cellW;
                    stagingArr[d + 5] = _cellH;
                    stagingArr[d + 6] = 0f;
                    stagingArr[d + 7] = 0f;
                    stagingArr[d + 8] = 0f;
                    stagingArr[d + 9] = 0f;
                    stagingArr[d + 10] = c.FgR / 255f;
                    stagingArr[d + 11] = c.FgG / 255f;
                    stagingArr[d + 12] = c.FgB / 255f;
                    stagingArr[d + 13] = c.BgR / 255f;
                    stagingArr[d + 14] = c.BgG / 255f;
                    stagingArr[d + 15] = c.BgB / 255f;
                    stagingArr[d + 16] = c.Flags | CellFlags.DecorOnly;
                    needed += floatsPerInstance;
                }
            }

            gl.BindBuffer(GLConsts.GL_ARRAY_BUFFER, _instanceVbo);
            unsafe
            {
                fixed (float* fp = stagingArr)
                {
                    gl.BufferData(GLConsts.GL_ARRAY_BUFFER, new IntPtr(needed * sizeof(float)), new IntPtr(fp), GLConsts.GL_DYNAMIC_DRAW);
                }
            }

            SetupInstanceAttribs(gl, floatsPerInstance);

            _gl.Uniform1i(_uPass, 0);
            _drawInstanced?.Invoke(GLConsts.GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, null, count);
            _gl.Uniform1i(_uPass, 1);
            _drawInstanced?.Invoke(GLConsts.GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, null, count);
    }

    private unsafe void SetupInstanceAttribs(GlInterface gl, int strideFloats)
    {
        int strideBytes = strideFloats * sizeof(float);
        // loc 1: aGridPx ← staging[0..1]
        gl.VertexAttribPointer(1, 2, GLConsts.GL_FLOAT, 0, strideBytes, IntPtr.Zero);
        gl.EnableVertexAttribArray(1);
        // loc 2: aAtlasPx ← staging[2..5]
        gl.VertexAttribPointer(2, 4, GLConsts.GL_FLOAT, 0, strideBytes, new IntPtr(2 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        // loc 3: aMetrics ← staging[6..9]
        gl.VertexAttribPointer(3, 4, GLConsts.GL_FLOAT, 0, strideBytes, new IntPtr(6 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        // loc 4: aFg ← staging[10..12]
        gl.VertexAttribPointer(4, 3, GLConsts.GL_FLOAT, 0, strideBytes, new IntPtr(10 * sizeof(float)));
        gl.EnableVertexAttribArray(4);
        // loc 5: aBg ← staging[13..15]
        gl.VertexAttribPointer(5, 3, GLConsts.GL_FLOAT, 0, strideBytes, new IntPtr(13 * sizeof(float)));
        gl.EnableVertexAttribArray(5);
        // loc 6: aFlags ← staging[16]
        gl.VertexAttribPointer(6, 1, GLConsts.GL_FLOAT, 0, strideBytes, new IntPtr(16 * sizeof(float)));
        gl.EnableVertexAttribArray(6);

        // Per-instance divisor for locs 1-6 (via GetProcAddress — GlInterface
        // does not expose glVertexAttribDivisor directly).
        var divisorPtr = _gl!.GetProcAddress("glVertexAttribDivisor");
        if (divisorPtr != IntPtr.Zero)
        {
            var divisor = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<VertexAttribDivisorDelegate>(divisorPtr);
            for (int loc = 1; loc <= 6; loc++) divisor(loc, 1);
        }
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void VertexAttribDivisorDelegate(int index, uint divisor);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private unsafe delegate void DrawElementsInstancedDelegate(uint mode, int count, uint type, void* indices, int instanceCount);

    private DrawElementsInstancedDelegate? _drawInstanced;

    private const int GL_TRIANGLES = 0x0004;
    private const int GL_UNSIGNED_SHORT = 0x1403;
    private const int GL_BLEND = 0x0BE2;
    private const int GL_ONE = 1;
    private const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate void BlendFuncDelegate(uint sfactor, uint dfactor);

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _textureManager?.Dispose();
        _textureManager = null;
        _program?.Dispose();
        _program = null;
        if (_cornerVbo != -1) gl.DeleteBuffer(_cornerVbo);
        if (_instanceVbo != -1) gl.DeleteBuffer(_instanceVbo);
        if (_ebo != -1) gl.DeleteBuffer(_ebo);
    }
}
