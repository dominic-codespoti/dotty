using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>Text blending and contrast; the <c>text</c> config object. Read every frame.</summary>
public sealed class TextUserConfig
{
    /// <summary>
    /// Glyph coverage blending: <c>native</c> (default, gamma-space as before), <c>linear</c> (linear-light luminance)
    /// or <c>linear-corrected</c> (linear plus <see cref="Contrast"/>). <c>native</c> stays the default until an A/B
    /// on real displays picks another.
    /// </summary>
    [JsonPropertyName("blending")]
    public string Blending { get; set; } = "native";

    /// <summary>Coverage boost for <c>linear-corrected</c> blending, 0-1; ignored by the other modes.</summary>
    [JsonPropertyName("contrast")]
    public double Contrast { get; set; } = 0.5;

    /// <summary>Minimum WCAG contrast ratio of text against its background, 1-21; 1 is off.</summary>
    [JsonPropertyName("minimumContrast")]
    public double MinimumContrast { get; set; } = 1.0;
}
