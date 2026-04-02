namespace Dotty.Config.SourceGenerator.Models;

/// <summary>
/// Record representing cursor settings.
/// </summary>
public record CursorModel
{
    public string Shape { get; init; } = "Block";
    public bool Blink { get; init; } = true;
    public int BlinkIntervalMs { get; init; } = 500;
    public uint? Color { get; init; } = null;  // null = use foreground
    public bool ShowUnfocused { get; init; } = false;

    /// <summary>
    /// Default cursor settings.
    /// </summary>
    public static CursorModel Default => new();
}
