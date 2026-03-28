using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Dotty.Terminal.Adapter;
using Dotty.App.Rendering;
using SkiaSharp;
using Avalonia.Threading;

namespace Dotty.App.Controls.Canvas.Rendering;

public unsafe class TerminalGlCanvas : OpenGlControlBase
{

    private float[]? _gridData;
    private float[]? _uvData;
    private int _lastCols = -1;
    private int _lastRows = -1;

    private Dotty.Terminal.Adapter.Cell[]? _cellsSnapshot;
    private int[]? _rowMapSnapshot;


    private string _vertexShaderSource = @"
        #version 330 core
        layout(location = 0) in vec2 position;
        out vec2 TexCoord;
        void main() {
            TexCoord = position * 0.5 + 0.5;
            TexCoord.y = 1.0 - TexCoord.y;
            gl_Position = vec4(position, 0.0, 1.0);
        }";

    private string _fragmentShaderSource = @"
        #version 330 core
        in vec2 TexCoord;
        out vec4 FragColor;
        
        uniform vec2 u_ScreenSize;
        uniform vec2 u_CellSize;
        uniform vec2 u_GridSize;
        uniform vec2 u_AtlasSize;
        
        uniform sampler2D u_GridData; // RGBA32F texture (color)
        uniform sampler2D u_GridUVs;  // RGBA32F texture (UVs)
        uniform sampler2D u_AtlasTex; // RGBA texture (font atlas)

        void main() {
            vec2 pixelPos = TexCoord * u_ScreenSize;
            float col = floor(pixelPos.x / u_CellSize.x);
            float row = floor(pixelPos.y / u_CellSize.y);
            
            if (col >= u_GridSize.x || row >= u_GridSize.y) {
                // Out of terminal grid bounds
                FragColor = vec4(0.0, 0.0, 0.0, 1.0); 
                return;
            }

            vec2 dataUv = vec2((col + 0.5) / u_GridSize.x, (row + 0.5) / u_GridSize.y);
            vec4 cellData = texture(u_GridData, dataUv);
            vec4 uvData = texture(u_GridUVs, dataUv);
            
            uint fgColor = floatBitsToUint(cellData.g);
            uint bgColor = floatBitsToUint(cellData.b);

            // Defaults mapped from typical Terminal defaults
            if (bgColor == 0u) bgColor = 0xFF000000u; // Black
            if (fgColor == 0u) fgColor = 0xFFCCCCCCu; // Light Grey

            vec3 bgRGB = vec3(
                float((bgColor >> 16u) & 0xFFu) / 255.0,
                float((bgColor >> 8u) & 0xFFu) / 255.0,
                float(bgColor & 0xFFu) / 255.0
            );

            vec3 fgRGB = vec3(
                float((fgColor >> 16u) & 0xFFu) / 255.0,
                float((fgColor >> 8u) & 0xFFu) / 255.0,
                float(fgColor & 0xFFu) / 255.0
            );

            vec2 glyphPos = uvData.xy;  // X, Y inside the Bitmap Atlas
            vec2 glyphSize = uvData.zw; // Width, Height in pixels

            // Center text within the local Cell Bounds
            vec2 inCellPos = mod(pixelPos, u_CellSize);
            vec2 offset = (u_CellSize - glyphSize) * 0.5;
            vec2 samplePos = inCellPos - offset;

            vec4 finalColor = vec4(bgRGB, 1.0);

            // Rasterize HarfBuzz mapped atlas if coords are valid and within bounds!
            if (glyphSize.x > 0.0 && glyphSize.y > 0.0 &&
                samplePos.x >= 0.0 && samplePos.x < glyphSize.x &&
                samplePos.y >= 0.0 && samplePos.y < glyphSize.y) 
            {
                vec2 atlasUv = (glyphPos + samplePos) / u_AtlasSize;
                vec4 texColor = texture(u_AtlasTex, atlasUv);
                
                // SKColorType.Rgba8888 exposes glyph mask directly along its alpha components
                float alpha = texColor.a;
                finalColor = vec4(mix(bgRGB, fgRGB, alpha), 1.0);
            }

            FragColor = finalColor;
        }";

    private int _shaderProgram;
    private int _vbo;
    private int _vao;
    private int _gridTexture;
    private int _uvTexture;
    private int _atlasTexture;

    public TerminalBuffer? Buffer { get; set; }
    public double CellWidth { get; set; } = 9.0;
    public double CellHeight { get; set; } = 18.0;

    private delegate void glUniform2fDelegate(int location, float v0, float v1);
    private delegate void glUniform1iDelegate(int location, int v0);
    private glUniform2fDelegate? _glUniform2f;
    private glUniform1iDelegate? _glUniform1i;

    private GlyphAtlas _atlas;

    public TerminalGlCanvas()
    {
        // Hydrate Harfbuzz mapping abstraction immediately 
        _atlas = new GlyphAtlas(SKTypeface.Default, 14f);
        _atlas.PreloadCommonGlyphs();
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        nint unif2fPtr = gl.GetProcAddress("glUniform2f");
        if (unif2fPtr != IntPtr.Zero) _glUniform2f = Marshal.GetDelegateForFunctionPointer<glUniform2fDelegate>(unif2fPtr);
        
        nint unif1iPtr = gl.GetProcAddress("glUniform1i");
        if (unif1iPtr != IntPtr.Zero) _glUniform1i = Marshal.GetDelegateForFunctionPointer<glUniform1iDelegate>(unif1iPtr);

        int vShader = gl.CreateShader(GlConsts.GL_VERTEX_SHADER);
        CompileShader(gl, vShader, _vertexShaderSource);
        int fShader = gl.CreateShader(GlConsts.GL_FRAGMENT_SHADER);
        CompileShader(gl, fShader, _fragmentShaderSource);

        _shaderProgram = gl.CreateProgram();
        gl.AttachShader(_shaderProgram, vShader);
        gl.AttachShader(_shaderProgram, fShader);
        gl.LinkProgram(_shaderProgram);

        gl.DeleteShader(vShader);
        gl.DeleteShader(fShader);

        // Map Quad
        float[] vertices = {
            -1.0f, -1.0f,  1.0f, -1.0f, -1.0f,  1.0f,
             1.0f, -1.0f,  1.0f,  1.0f, -1.0f,  1.0f
        };

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);

        fixed (float* pVerts = vertices) {
            gl.BufferData(GlConsts.GL_ARRAY_BUFFER, new IntPtr(vertices.Length * sizeof(float)), new IntPtr(pVerts), GlConsts.GL_STATIC_DRAW);
        }

        gl.VertexAttribPointer(0, 2, GlConsts.GL_FLOAT, 0, 2 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);

        _gridTexture = gl.GenTexture();
        _uvTexture = gl.GenTexture();
        _atlasTexture = gl.GenTexture();
    }

    private void CompileShader(GlInterface gl, int shader, string source)
    {
        gl.ShaderSourceString(shader, source);
        gl.CompileShader(shader);
        int status;
        gl.GetShaderiv(shader, GlConsts.GL_COMPILE_STATUS, &status);
        if (status == 0)
        {
            var logBuffer = stackalloc byte[1024];
            gl.GetShaderInfoLog(shader, 1024, out int outLen, logBuffer);
            Console.WriteLine("Shader Error: " + Encoding.UTF8.GetString(logBuffer, outLen));
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

        gl.UseProgram(_shaderProgram);
        gl.BindVertexArray(_vao);

        int screenSizeLoc = gl.GetUniformLocationString(_shaderProgram, "u_ScreenSize");
        int cellSizeLoc = gl.GetUniformLocationString(_shaderProgram, "u_CellSize");
        int gridSizeLoc = gl.GetUniformLocationString(_shaderProgram, "u_GridSize");
        int atlasSizeLoc = gl.GetUniformLocationString(_shaderProgram, "u_AtlasSize");
        
        int gridDataLoc = gl.GetUniformLocationString(_shaderProgram, "u_GridData");
        int gridUVsLoc = gl.GetUniformLocationString(_shaderProgram, "u_GridUVs");
        int atlasTexLoc = gl.GetUniformLocationString(_shaderProgram, "u_AtlasTex");

        
        
        
        int cols = Buffer != null ? Buffer.Columns : 80;
        int rows = Buffer != null ? Buffer.Rows : 24;

        if (_glUniform2f != null)
        {
            _glUniform2f(screenSizeLoc, (float)Bounds.Width, (float)Bounds.Height);
            _glUniform2f(cellSizeLoc, (float)CellWidth, (float)CellHeight);
            _glUniform2f(gridSizeLoc, cols, rows);
        }

        
        int requiredSize = cols * rows * 4;
        if (_gridData == null || _gridData.Length < requiredSize || cols != _lastCols || rows != _lastRows)
        {
            _gridData = new float[requiredSize];
            _uvData = new float[requiredSize];
            _lastCols = cols;
            _lastRows = rows;
        }

        Array.Clear(_gridData, 0, requiredSize);
        Array.Clear(_uvData, 0, requiredSize);

        if (Buffer != null)
        {
            var activeScreen = Buffer.ActiveBuffer;
            bool lockTaken = false;
            try 
            {
                System.Threading.Monitor.TryEnter(Buffer.SyncRoot, 0, ref lockTaken);
                if (lockTaken)
                {
                    activeScreen.ReadSnapshot(ref _cellsSnapshot, ref _rowMapSnapshot);
                }
            } 
            finally 
            {
                if (lockTaken) System.Threading.Monitor.Exit(Buffer.SyncRoot);
            }

            if (_cellsSnapshot == null || _rowMapSnapshot == null) return;
            for (int r = 0; r < rows; r++)
            {
                int mappedRow = _rowMapSnapshot[r];
                for (int c = 0; c < cols; c++)
                {
                    var cell = _cellsSnapshot[mappedRow * cols + c];
                    
                    int idx = (r * cols + c) * 4;
                    
                    _gridData[idx] = BitConverter.Int32BitsToSingle(unchecked((int)cell.Rune));
                    _gridData[idx + 1] = BitConverter.Int32BitsToSingle(unchecked((int)cell.Foreground));
                    _gridData[idx + 2] = BitConverter.Int32BitsToSingle(unchecked((int)cell.Background));
                    _gridData[idx + 3] = BitConverter.Int32BitsToSingle(unchecked((int)cell.Flags));

                    string t = cell.Grapheme ?? (cell.Rune > 0 ? char.ConvertFromUtf32((int)cell.Rune) : string.Empty);
                    if (!string.IsNullOrEmpty(t) && t != " " && !cell.IsContinuation)
                    {
                        var key = new GlyphKey(t, null, cell.Bold);
                        _atlas.EnsureGlyph(key);
                        if (_atlas.TryGetGlyph(key, out var info))
                        {
                            _uvData[idx] = info.X;
                            _uvData[idx + 1] = info.Y;
                            _uvData[idx + 2] = info.Width;
                            _uvData[idx + 3] = info.Height;
                        }
                    }
                }
            }
        }

        var bmp = _atlas.AtlasBitmap;
        if (_glUniform2f != null) {
            _glUniform2f(atlasSizeLoc, bmp.Width, bmp.Height);
        }

        int GL_RGBA32F = 0x8814;
        int GL_RGBA = 0x1908;
        int GL_FLOAT = 0x1406;
        int GL_UNSIGNED_BYTE = 0x1401;
        int GL_TEXTURE0 = 0x84C0;

        // 1. Upload Color / Grid Texture
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _gridTexture);
        SetTexParams(gl);
        fixed (float* pData = _gridData) { gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GL_RGBA32F, cols, rows, 0, GL_RGBA, GL_FLOAT, new IntPtr(pData)); }
        if (_glUniform1i != null) _glUniform1i(gridDataLoc, 0);

        // 2. Upload UV Texture
        gl.ActiveTexture(GL_TEXTURE0 + 1);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _uvTexture);
        SetTexParams(gl);
        fixed (float* pUV = _uvData) { gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GL_RGBA32F, cols, rows, 0, GL_RGBA, GL_FLOAT, new IntPtr(pUV)); }
        if (_glUniform1i != null) _glUniform1i(gridUVsLoc, 1);

        // 3. Upload Skia Atlas Bitmap 
        gl.ActiveTexture(GL_TEXTURE0 + 2);
        gl.BindTexture(GlConsts.GL_TEXTURE_2D, _atlasTexture);
        SetTexParams(gl, GlConsts.GL_LINEAR);
        gl.TexImage2D(GlConsts.GL_TEXTURE_2D, 0, GL_RGBA, bmp.Width, bmp.Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, bmp.GetPixels());
        if (_glUniform1i != null) _glUniform1i(atlasTexLoc, 2);

        // Issue Draw Call
        gl.DrawArrays(GlConsts.GL_TRIANGLES, 0, 6);
        
        // Loop render sequence exactly like Skia ImmediateContext does (this ensures continuous scrolling fluidity)
        RequestNextFrameRendering();
    }

    private void SetTexParams(GlInterface gl, int filter = GlConsts.GL_NEAREST) {
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MIN_FILTER, filter);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, GlConsts.GL_TEXTURE_MAG_FILTER, filter);
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, 0x2802, 0x812F); // WRAP_S CLAMP
        gl.TexParameteri(GlConsts.GL_TEXTURE_2D, 0x2803, 0x812F); // WRAP_T CLAMP
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        gl.DeleteProgram(_shaderProgram);
        gl.DeleteBuffer(_vbo);
        gl.DeleteVertexArray(_vao);
        base.OnOpenGlDeinit(gl);
    }
}
