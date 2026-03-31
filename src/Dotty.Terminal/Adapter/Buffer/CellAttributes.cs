namespace Dotty.Terminal.Adapter;

/// <summary>
/// Cell attributes using zero-allocation ARGB colors instead of hex strings.
/// </summary>
public struct CellAttributes
{
    public SgrColorArgb Foreground { get; set; }
    public SgrColorArgb Background { get; set; }
    public SgrColorArgb UnderlineColor { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool DoubleUnderline { get; set; }
    public bool Faint { get; set; }
    public bool Inverse { get; set; }
    public bool Strikethrough { get; set; }
    public bool Overline { get; set; }
    public bool Invisible { get; set; }
    public bool SlowBlink { get; set; }
    public ushort HyperlinkId { get; set; }

    public static readonly CellAttributes Default = new();
    
    /// <summary>
    /// Returns true if no color is set (all colors are empty/transparent).
    /// </summary>
    public bool IsDefaultColors => Foreground.IsEmpty && Background.IsEmpty && UnderlineColor.IsEmpty;
}
