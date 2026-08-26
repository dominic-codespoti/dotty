using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Dotty.App.Rendering;

/// <summary>
/// Wraps OpenGL shader compilation, linking, and uniform management using Avalonia's <see cref="GlInterface"/>.
/// </summary>
public sealed class GLShaderProgram : IDisposable
{
    public const int GL_VERTEX_SHADER = 0x8B31;
    public const int GL_FRAGMENT_SHADER = 0x8B30;
    public const int GL_COMPILE_STATUS = 0x8B81;
    public const int GL_LINK_STATUS = 0x8B82;

    public const string VERTEX_SHADER = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;
        layout(location = 1) in vec2 aGridPx;
        layout(location = 2) in vec4 aAtlasPx;
        layout(location = 3) in vec4 aMetrics;
        layout(location = 4) in vec3 aFg;
        layout(location = 5) in vec3 aBg;
        layout(location = 6) in uint aFlags;
        uniform vec2 uFramebufferPx;
        uniform vec2 uCellPx;
        uniform vec2 uAtlasSize;
        uniform int uPass;
        flat out vec3 vFg;
        flat out vec3 vBg;
        out vec2 vUv;
        out float vCornerY;
        flat out uint vFlags;
        const uint FLAG_WIDE = 2u;      // CellFlags.WideCell
        const uint FLAG_DECOR = 128u;   // CellFlags.DecorOnly

        void main()
        {
            bool wide = (aFlags & FLAG_WIDE) != 0u;
            vCornerY = aCorner.y;
            vec2 cell = vec2(uCellPx.x * (wide ? 2.0 : 1.0), uCellPx.y);
            vec2 origin = aGridPx;
            vec2 size = cell;
            if (uPass != 0)
            {
                origin += vec2(aMetrics.z, aMetrics.y - aMetrics.w);
                size = aAtlasPx.zw;
                vUv = (aAtlasPx.xy + aCorner * size) / uAtlasSize;
            }
            else { vUv = vec2(0); }
            vec2 p = origin + aCorner * size;
            vec2 clip = vec2(2.0 * p.x / uFramebufferPx.x - 1.0,
                             1.0 - 2.0 * p.y / uFramebufferPx.y);
            gl_Position = vec4(clip, 0.0, 1.0);
            vFg = aFg;
            vBg = aBg;
            vFlags = aFlags;
        }
        """;

    public const string FRAGMENT_SHADER = """
        #version 330 core
        uniform sampler2D uAtlas;
        uniform int uPass;
        uniform float uUnderlineY;   // fraction of cell height
        uniform float uStrikeY;
        uniform float uLineHalf;     // half thickness, fraction
        flat in vec3 vFg;
        flat in vec3 vBg;
        in vec2 vUv;
        in float vCornerY;
        flat in uint vFlags;
        out vec4 fragColor;
        const uint FLAG_DECOR = 128u;   // CellFlags.DecorOnly
        const uint FLAG_UNDERLINE = 8u;
        const uint FLAG_STRIKE = 16u;
        const uint FLAG_OVERLINE = 32u;

        void main()
        {
            if (uPass == 0) { fragColor = vec4(vBg, 1.0); return; }
            if ((vFlags & FLAG_DECOR) != 0u)
            {
                // Decoration-only instance: the quad covers the cell; draw
                // the bars at their metric fractions, discard elsewhere.
                if ((vFlags & FLAG_UNDERLINE) != 0u &&
                    abs(vCornerY - uUnderlineY) <= uLineHalf)
                { fragColor = vec4(vFg.rgb, 1.0); return; }
                if ((vFlags & FLAG_STRIKE) != 0u &&
                    abs(vCornerY - uStrikeY) <= uLineHalf)
                { fragColor = vec4(vFg.rgb, 1.0); return; }
                discard;
            }
            float coverage = texture(uAtlas, vUv).r;
            if (coverage <= 0.001) discard;
            fragColor = vec4(vFg.rgb * coverage, coverage);
        }
        """;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void glUniform2fDelegate(int location, float v0, float v1);

    private readonly GlInterface _gl;
    private readonly glUniform2fDelegate? _glUniform2f;
    private int _program;
    private bool _disposed;

    /// <summary>
    /// Gets the OpenGL program handle.
    /// </summary>
    public int ProgramHandle => _program;

    /// <summary>
    /// Compiles vertex and fragment shaders and links them into an OpenGL shader program.
    /// </summary>
    /// <param name="gl">The Avalonia OpenGL interface.</param>
    /// <param name="vertexSource">The vertex shader GLSL source code.</param>
    /// <param name="fragmentSource">The fragment shader GLSL source code.</param>
    public GLShaderProgram(GlInterface gl, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));

        var uniform2fPtr = gl.GetProcAddress("glUniform2f");
        if (uniform2fPtr != IntPtr.Zero)
        {
            _glUniform2f = Marshal.GetDelegateForFunctionPointer<glUniform2fDelegate>(uniform2fPtr);
        }

        int vertexShader = CompileShader(gl, GL_VERTEX_SHADER, vertexSource);
        int fragmentShader = 0;
        try
        {
            fragmentShader = CompileShader(gl, GL_FRAGMENT_SHADER, fragmentSource);
            _program = LinkProgram(gl, vertexShader, fragmentShader);
        }
        finally
        {
            if (vertexShader != 0)
            {
                gl.DeleteShader(vertexShader);
            }

            if (fragmentShader != 0)
            {
                gl.DeleteShader(fragmentShader);
            }
        }
    }

    /// <summary>
    /// Activates the shader program for rendering.
    /// </summary>
    public void Use()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.UseProgram(_program);
    }

    /// <summary>
    /// Retrieves the location of a uniform variable within the shader program.
    /// </summary>
    public int GetUniformLocation(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _gl.GetUniformLocationString(_program, name);
    }

    /// <summary>
    /// Sets an integer uniform value.
    /// </summary>
    public void SetUniform1i(int location, int value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.Uniform1i(location, value);
    }

    /// <summary>
    /// Sets a 2-component float uniform value.
    /// </summary>
    public void SetUniform2f(int location, float x, float y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_glUniform2f != null)
        {
            _glUniform2f(location, x, y);
        }
    }

    private static int CompileShader(GlInterface gl, int shaderType, string source)
    {
        int shader = gl.CreateShader(shaderType);
        if (shader == 0)
        {
            throw new InvalidOperationException($"glCreateShader failed for shader type 0x{shaderType:X}.");
        }

        string? error = gl.CompileShaderAndGetError(shader, source);
        if (!string.IsNullOrEmpty(error))
        {
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile shader (type 0x{shaderType:X}): {error}");
        }

        return shader;
    }

    private static int LinkProgram(GlInterface gl, int vertexShader, int fragmentShader)
    {
        int program = gl.CreateProgram();
        if (program == 0)
        {
            throw new InvalidOperationException("glCreateProgram failed.");
        }

        gl.AttachShader(program, vertexShader);
        gl.AttachShader(program, fragmentShader);

        string? error = gl.LinkProgramAndGetError(program);
        if (!string.IsNullOrEmpty(error))
        {
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"Failed to link shader program: {error}");
        }

        return program;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_program != 0)
            {
                _gl.DeleteProgram(_program);
                _program = 0;
            }

            _disposed = true;
        }
    }
}
