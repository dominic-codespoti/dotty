namespace Dotty.Abstractions.Config;

/// <summary>
/// Main configuration interface for Dotty terminal emulator.
/// Implement this interface to customize terminal settings.
/// </summary>
public interface IDottyConfig
{
    /// <summary>
    /// Font family name(s) to use for the terminal.
    /// Can be a comma-separated list for fallback fonts.
    /// </summary>
    string? FontFamily { get; }

    /// <summary>
    /// Font size in points.
    /// </summary>
    double? FontSize { get; }

    /// <summary>
    /// Cell padding in pixels.
    /// </summary>
    double? CellPadding { get; }

    /// <summary>
    /// Content padding around the terminal area.
    /// </summary>
    Thickness? ContentPadding { get; }

    /// <summary>
    /// Color scheme for the terminal.
    /// </summary>
    IColorScheme? Colors { get; }

    /// <summary>
    /// Key bindings configuration.
    /// </summary>
    IKeyBindings? KeyBindings { get; }

    /// <summary>
    /// Number of scrollback lines to keep in memory.
    /// </summary>
    int? ScrollbackLines { get; }

    /// <summary>
    /// Initial window dimensions in columns and rows.
    /// </summary>
    IWindowDimensions? InitialDimensions { get; }

    /// <summary>
    /// Cursor appearance settings.
    /// </summary>
    ICursorSettings? Cursor { get; }

    /// <summary>
    /// Selection highlight brush color (ARGB format).
    /// </summary>
    uint? SelectionColor { get; }

    /// <summary>
    /// Tab bar background color (ARGB format).
    /// </summary>
    uint? TabBarBackgroundColor { get; }

    /// <summary>
    /// Window transparency level. None, Transparent, Blur, or Acrylic.
    /// </summary>
    TransparencyLevel? Transparency { get; }

    /// <summary>
    /// Window opacity (0-100, where 100 is fully opaque, 0 is fully transparent).
    /// Use this to make the entire terminal window semi-transparent.
    /// Works independently of Transparency level.
    /// </summary>
    byte? WindowOpacity { get; }

    /// <summary>
    /// Inactive tab destroy delay in milliseconds.
    /// Controls how quickly inactive tab visuals are destroyed to save memory.
    /// </summary>
    int? InactiveTabDestroyDelayMs { get; }
}

/// <summary>
/// Represents thickness values for padding (Left, Top, Right, Bottom).
/// </summary>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
    public Thickness(double horizontal, double vertical) : this(horizontal, vertical, horizontal, vertical) { }
}
