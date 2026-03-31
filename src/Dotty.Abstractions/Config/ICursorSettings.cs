namespace Dotty.Abstractions.Config;

/// <summary>
/// Cursor shape options.
/// </summary>
public enum CursorShape
{
    /// <summary>Block cursor (fills the entire cell).</summary>
    Block,

    /// <summary>Beam/I-beam cursor (vertical line).</summary>
    Beam,

    /// <summary>Underline cursor (horizontal line at bottom).</summary>
    Underline,
}

/// <summary>
/// Cursor settings configuration.
/// </summary>
public interface ICursorSettings
{
    /// <summary>
    /// Cursor shape.
    /// </summary>
    CursorShape Shape { get; }

    /// <summary>
    /// Whether the cursor should blink.
    /// </summary>
    bool Blink { get; }

    /// <summary>
    /// Blink interval in milliseconds.
    /// </summary>
    int BlinkIntervalMs { get; }

    /// <summary>
    /// Cursor color (ARGB format). Null to use foreground color.
    /// </summary>
    uint? Color { get; }

    /// <summary>
    /// Whether to show the cursor when the terminal is not focused.
    /// </summary>
    bool ShowUnfocused { get; }
}
