namespace Dotty.Abstractions.Config;

/// <summary>
/// Window transparency level options for acrylic/glass effects.
/// </summary>
public enum TransparencyLevel
{
    /// <summary>
    /// Solid background, no transparency.
    /// </summary>
    None,

    /// <summary>
    /// Simple transparency without blur effects.
    /// </summary>
    Transparent,

    /// <summary>
    /// Blurred background (acrylic/glass effect) with platform-native blur.
    /// </summary>
    Blur,

    /// <summary>
    /// Full acrylic effect with noise texture and blur (platform-dependent).
    /// </summary>
    Acrylic
}
