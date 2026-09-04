namespace Dotty.Silk;

/// <summary>
/// Shader pair for pixel-precise, rounded-rect "chrome" quads (tab pills,
/// buttons, hover highlights, soft shadows). Rendered as a separate instanced
/// pass, positioned in framebuffer pixels rather than snapped to the
/// character grid used by <see cref="SilkGlShaders"/>, with corner rounding
/// and edge softness computed analytically via a signed-distance field.
/// </summary>
public static class SilkChromeShaders
{
    public const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aCorner;
        layout(location = 1) in vec4 aRect;      // x, y, w, h (framebuffer px, nominal rect)
        layout(location = 2) in vec2 aShape;     // radius, blur (px)
        layout(location = 3) in vec4 aColorTop;
        layout(location = 4) in vec4 aColorBottom;
        uniform vec2 uFramebufferPx;
        out vec2 vLocalPx;
        flat out vec2 vSize;
        flat out vec2 vShape;
        flat out vec4 vColorTop;
        flat out vec4 vColorBottom;

        void main()
        {
            vec2 size = aRect.zw;
            // Expand the drawn quad by the blur margin on every side so a
            // soft shadow's falloff isn't clipped at the nominal rect edge.
            float margin = aShape.y;
            vec2 expandedOrigin = aRect.xy - vec2(margin);
            vec2 expandedSize = size + vec2(margin * 2.0);

            vec2 p = expandedOrigin + aCorner * expandedSize;
            vLocalPx = p - aRect.xy;
            vSize = size;
            vShape = aShape;
            vColorTop = aColorTop;
            vColorBottom = aColorBottom;

            vec2 clip = vec2(2.0 * p.x / uFramebufferPx.x - 1.0,
                             1.0 - 2.0 * p.y / uFramebufferPx.y);
            gl_Position = vec4(clip, 0.0, 1.0);
        }
        """;

    public const string FragmentSource = """
        #version 330 core
        in vec2 vLocalPx;
        flat in vec2 vSize;
        flat in vec2 vShape;
        flat in vec4 vColorTop;
        flat in vec4 vColorBottom;
        out vec4 fragColor;

        void main()
        {
            vec2 halfSize = vSize * 0.5;
            vec2 p = vLocalPx - halfSize;
            vec2 q = abs(p) - halfSize + vShape.x;
            float dist = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - vShape.x;

            float edge = max(vShape.y, 0.75);
            float alpha = 1.0 - smoothstep(-edge, edge, dist);
            if (alpha <= 0.001) discard;

            float t = clamp(vLocalPx.y / max(vSize.y, 1.0), 0.0, 1.0);
            vec4 color = mix(vColorTop, vColorBottom, t);
            fragColor = vec4(color.rgb * color.a * alpha, color.a * alpha);
        }
        """;
}
