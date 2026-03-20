namespace Dotty.Terminal.Adapter;

public struct CellAttributes
{
    public SgrColor? Foreground { get; set; }
    public SgrColor? Background { get; set; }
    public SgrColor? UnderlineColor { get; set; }
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

    public static readonly CellAttributes Default = new();
}
