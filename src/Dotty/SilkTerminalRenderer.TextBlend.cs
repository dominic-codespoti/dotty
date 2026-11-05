using Dotty.Rendering.Gpu;

namespace Dotty.Silk;

/// <summary>Glyph-pass coverage correction (<c>text.blending</c>, <c>text.contrast</c>); see <see cref="CoverageCorrection"/>.</summary>
public sealed unsafe partial class SilkTerminalRenderer
{
    private int _uTextBlend;
    private int _uTextContrast;
    private bool _textBlendSet;
    private TextBlendMode _cachedTextBlend;
    private float _cachedTextContrast;

    /// <summary>Coverage blending mode of the glyph pass; <see cref="TextBlendMode.Native"/> leaves coverage unchanged.</summary>
    public TextBlendMode TextBlend { get; set; }

    /// <summary>Contrast boost of <see cref="TextBlendMode.LinearCorrected"/>, 0-1.</summary>
    public float TextContrast { get; set; } = CoverageCorrection.DefaultContrast;

    private void InitTextBlendUniforms()
    {
        _uTextBlend = _gl.GetUniformLocation(_program, "uTextBlend");
        _uTextContrast = _gl.GetUniformLocation(_program, "uTextContrast");
    }

    /// <summary>Uploads the blend uniforms when they changed; the cell program must be current.</summary>
    private void ApplyTextBlendUniforms()
    {
        TextBlendMode mode = TextBlend;
        float contrast = TextContrast;
        if (_textBlendSet && _cachedTextBlend == mode && _cachedTextContrast == contrast)
            return;
        _gl.Uniform1(_uTextBlend, (int)mode);
        _gl.Uniform1(_uTextContrast, contrast);
        _cachedTextBlend = mode;
        _cachedTextContrast = contrast;
        _textBlendSet = true;
    }
}
