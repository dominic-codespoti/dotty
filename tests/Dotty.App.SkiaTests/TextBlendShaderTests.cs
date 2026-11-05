using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Silk.NET.OpenGL;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// Readback proof that the glyph-pass coverage correction in <see cref="SilkGlShaders.FragmentSource"/> matches the
/// CPU reference <see cref="TextBlend.Blend"/> (W4-C5) over a colour/coverage table, in every <c>text.blending</c>
/// mode. The real fragment shader runs behind a test vertex shader that feeds one full-viewport glyph quad; the atlas
/// is a 256×1 R8 texture holding every coverage value. Skipped without a GL 3.3 context.
/// </summary>
[Collection(nameof(NativeGlCollection))]
public sealed class TextBlendShaderTests
{
    private const string TestVertexSource = """
        #version 330 core
        uniform vec4 uTestFg;
        uniform vec4 uTestBg;
        uniform float uTestU;
        flat out vec4 vFg;
        flat out vec4 vBg;
        out vec2 vUv;
        out vec2 vColorUv;
        out float vCornerY;
        flat out uint vFlags;
        void main()
        {
            vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
            vFg = uTestFg;
            vBg = uTestBg;
            vUv = vec2(uTestU, 0.5);
            vColorUv = vec2(0.0);
            vCornerY = corner.y;
            vFlags = 0u;
        }
        """;

    private static readonly (SgrColorArgb Fg, SgrColorArgb Bg)[] Pairs =
    [
        (SgrColorArgb.FromRgb(255, 255, 255), SgrColorArgb.FromRgb(0, 0, 0)),
        (SgrColorArgb.FromRgb(0, 0, 0), SgrColorArgb.FromRgb(255, 255, 255)),
        (SgrColorArgb.FromRgb(230, 200, 40), SgrColorArgb.FromRgb(20, 30, 60)),
        (SgrColorArgb.FromRgb(40, 90, 200), SgrColorArgb.FromRgb(240, 240, 230)),
        (SgrColorArgb.FromRgb(200, 60, 60), SgrColorArgb.FromRgb(60, 200, 60)),
        (SgrColorArgb.FromRgb(128, 128, 128), SgrColorArgb.FromRgb(128, 128, 128)),
    ];

    private static readonly byte[] Coverages = [0, 1, 32, 96, 128, 200, 254, 255];

    [Theory]
    [InlineData(TextBlendMode.Native, 0.5f)]
    [InlineData(TextBlendMode.Linear, 0.5f)]
    [InlineData(TextBlendMode.LinearCorrected, 0f)]
    [InlineData(TextBlendMode.LinearCorrected, 0.5f)]
    [InlineData(TextBlendMode.LinearCorrected, 1f)]
    public unsafe void ShaderMatchesCpuReference(TextBlendMode mode, float contrast)
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        GL gl = host.Gl;

        uint program = SilkGlShaders.CreateProgram(gl, TestVertexSource, SilkGlShaders.FragmentSource);
        uint vao = gl.GenVertexArray();
        uint texture = gl.GenTexture();
        try
        {
            byte* ramp = stackalloc byte[256];
            for (int i = 0; i < 256; i++)
                ramp[i] = (byte)i;
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, 256, 1, 0, PixelFormat.Red, PixelType.UnsignedByte, ramp);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            gl.UseProgram(program);
            gl.BindVertexArray(vao);
            gl.Uniform1(gl.GetUniformLocation(program, "uAtlas"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "uColorAtlas"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "uPass"), 1);
            gl.Uniform1(gl.GetUniformLocation(program, "uTextBlend"), (int)mode);
            gl.Uniform1(gl.GetUniformLocation(program, "uTextContrast"), contrast);
            int uFg = gl.GetUniformLocation(program, "uTestFg");
            int uBg = gl.GetUniformLocation(program, "uTestBg");
            int uU = gl.GetUniformLocation(program, "uTestU");

            foreach (var (fg, bg) in Pairs)
            {
                foreach (byte coverage in Coverages)
                {
                    host.Clear(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
                    gl.Uniform4(uFg, fg.R / 255f, fg.G / 255f, fg.B / 255f, 1f);
                    gl.Uniform4(uBg, bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
                    gl.Uniform1(uU, (coverage + 0.5f) / 256f);
                    gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

                    var actual = host.ReadTopLeft(host.Width / 2, host.Height / 2);
                    SgrColorArgb expected = TextBlend.Blend(mode, fg, bg, coverage, contrast);
                    string where = $"{mode} c={contrast} fg={fg.R},{fg.G},{fg.B} bg={bg.R},{bg.G},{bg.B} coverage={coverage}: " +
                        $"gpu {actual.R},{actual.G},{actual.B} cpu {expected.R},{expected.G},{expected.B}";
                    Assert.True(Math.Abs(actual.R - expected.R) <= 2, where);
                    Assert.True(Math.Abs(actual.G - expected.G) <= 2, where);
                    Assert.True(Math.Abs(actual.B - expected.B) <= 2, where);
                }
            }
        }
        finally
        {
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.DeleteVertexArray(vao);
            gl.DeleteTexture(texture);
            gl.DeleteProgram(program);
        }
    }
}
