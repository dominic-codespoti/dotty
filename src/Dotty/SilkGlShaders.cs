using Silk.NET.OpenGL;

namespace Dotty.Silk;

public static class SilkGlShaders
{
    public const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;
        layout(location = 1) in vec2 aGridPx;
        layout(location = 2) in vec4 aAtlasPx;
        layout(location = 3) in vec4 aMetrics;
        layout(location = 4) in vec4 aFg;
        layout(location = 5) in vec4 aBg;
        layout(location = 6) in uint aFlags;
        uniform vec2 uFramebufferPx;
        uniform vec2 uCellPx;
        uniform vec2 uAtlasSize;
        uniform int uPass;
        flat out vec4 vFg;
        flat out vec4 vBg;
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
            else
            {
                // Snap background-cell boundaries to device pixels. Rounding
                // both edges from the same absolute coordinates keeps
                // adjacent cells contiguous at fractional DPI scales.
                vec2 leftTop = floor(origin + vec2(0.5));
                vec2 rightBottom = floor(origin + size + vec2(0.5));
                origin = leftTop;
                size = max(rightBottom - leftTop, vec2(0.0));
                vUv = vec2(0);
            }
            vec2 p = origin + aCorner * size;
            vec2 clip = vec2(2.0 * p.x / uFramebufferPx.x - 1.0,
                             1.0 - 2.0 * p.y / uFramebufferPx.y);
            gl_Position = vec4(clip, 0.0, 1.0);
            bool inverse = (aFlags & 4u) != 0u;
            vFg = inverse ? aBg : aFg;
            vBg = inverse ? aFg : aBg;
            vFlags = aFlags;
        }
        """;

    public const string FragmentSource = """
        #version 330 core
        uniform sampler2D uAtlas;
        uniform int uPass;
        uniform float uUnderlineY;   // fraction of cell height
        uniform float uStrikeY;
        uniform float uLineHalf;     // half thickness, fraction
        flat in vec4 vFg;
        flat in vec4 vBg;
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
            if (uPass == 0) {
                if (vBg.a <= 0.001) discard;
                fragColor = vec4(vBg.rgb * vBg.a, vBg.a);
                return;
            }
            if ((vFlags & FLAG_DECOR) != 0u)
            {
                // Decoration-only instance: the quad covers the cell; draw
                // the bars at their metric fractions, discard elsewhere.
                if ((vFlags & FLAG_UNDERLINE) != 0u &&
                    abs(vCornerY - uUnderlineY) <= uLineHalf)
                { fragColor = vec4(vFg.rgb * vFg.a, vFg.a); return; }
                if ((vFlags & FLAG_STRIKE) != 0u &&
                    abs(vCornerY - uStrikeY) <= uLineHalf)
                { fragColor = vec4(vFg.rgb * vFg.a, vFg.a); return; }
                discard;
            }
            float coverage = texture(uAtlas, vUv).r;
            if (coverage <= 0.001) discard;
            fragColor = vec4(vFg.rgb * coverage * vFg.a, coverage * vFg.a);
        }
        """;
    public static uint CreateProgram(GL gl, string vertexSource, string fragmentSource)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(fragmentSource);

        uint vs = gl.CreateShader(ShaderType.VertexShader);
        gl.ShaderSource(vs, vertexSource);
        gl.CompileShader(vs);
        gl.GetShader(vs, ShaderParameterName.CompileStatus, out int vStatus);
        if (vStatus != (int)GLEnum.True)
        {
            string log = gl.GetShaderInfoLog(vs);
            gl.DeleteShader(vs);
            throw new InvalidOperationException("Vertex shader compile failed: " + log);
        }

        uint fs = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(fs, fragmentSource);
        gl.CompileShader(fs);
        gl.GetShader(fs, ShaderParameterName.CompileStatus, out int fStatus);
        if (fStatus != (int)GLEnum.True)
        {
            string log = gl.GetShaderInfoLog(fs);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException("Fragment shader compile failed: " + log);
        }

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus != (int)GLEnum.True)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DetachShader(program, vs);
            gl.DetachShader(program, fs);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            gl.DeleteProgram(program);
            throw new InvalidOperationException("Program link failed: " + log);
        }

        gl.DetachShader(program, vs);
        gl.DetachShader(program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }
}
