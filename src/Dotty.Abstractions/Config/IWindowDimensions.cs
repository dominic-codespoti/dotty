namespace Dotty.Abstractions.Config;

/// <summary>
/// Window dimensions configuration.
/// </summary>
public interface IWindowDimensions
{
    /// <summary>
    /// Initial width in columns.
    /// </summary>
    int Columns { get; }

    /// <summary>
    /// Initial height in rows.
    /// </summary>
    int Rows { get; }

    /// <summary>
    /// Window width in pixels (optional, overrides Columns if set).
    /// </summary>
    int? WidthPixels { get; }

    /// <summary>
    /// Window height in pixels (optional, overrides Rows if set).
    /// </summary>
    int? HeightPixels { get; }

    /// <summary>
    /// Whether to start in fullscreen mode.
    /// </summary>
    bool StartFullscreen { get; }

    /// <summary>
    /// Window title.
    /// </summary>
    string? Title { get; }
}
