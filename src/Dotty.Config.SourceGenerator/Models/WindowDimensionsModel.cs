namespace Dotty.Config.SourceGenerator.Models;

/// <summary>
/// Record representing window dimensions.
/// </summary>
public record WindowDimensionsModel
{
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public string Title { get; init; } = "Dotty";
    public bool StartFullscreen { get; init; } = false;

    /// <summary>
    /// Default window dimensions.
    /// </summary>
    public static WindowDimensionsModel Default => new();

    public WindowDimensionsModel() { }

    public WindowDimensionsModel(int columns, int rows, string title, bool startFullscreen)
    {
        Columns = columns;
        Rows = rows;
        Title = title;
        StartFullscreen = startFullscreen;
    }
}
