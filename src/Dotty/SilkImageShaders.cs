namespace Dotty.Silk;

/// <summary>
/// Shader pair for Kitty-graphics image quads: one instanced textured rectangle per
/// <c>ImageQuadInstance</c>, positioned in framebuffer pixels (top-left origin, same NDC
/// conversion as <see cref="SilkChromeShaders"/>). Textures hold premultiplied RGBA or opaque
/// RGB, so the fragment output is premultiplied and blends with <c>One, OneMinusSrcAlpha</c>.
/// </summary>
public static class SilkImageShaders
{
    public const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;
        layout(location = 1) in vec4 aRect;   // x, y, w, h (framebuffer px)
        layout(location = 2) in vec4 aUv;     // u0, v0, u1, v1 (v0 = top edge)
        layout(location = 3) in float aAlpha;
        uniform vec2 uFramebufferPx;
        out vec2 vUv;
        out float vAlpha;

        void main()
        {
            vec2 p = aRect.xy + aCorner * aRect.zw;
            vUv = mix(aUv.xy, aUv.zw, aCorner);
            vAlpha = aAlpha;
            vec2 clip = vec2(2.0 * p.x / uFramebufferPx.x - 1.0,
                             1.0 - 2.0 * p.y / uFramebufferPx.y);
            gl_Position = vec4(clip, 0.0, 1.0);
        }
        """;

    public const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in float vAlpha;
        uniform sampler2D uImage;
        out vec4 fragColor;

        void main()
        {
            // Premultiplied texel scaled by the instance alpha stays premultiplied.
            // RGB8 textures sample alpha = 1.
            fragColor = texture(uImage, vUv) * vAlpha;
        }
        """;
}
