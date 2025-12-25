namespace Dotty.Terminal.Adapter;

public struct Cell
{
    public string? Grapheme;
    public SgrColor? Foreground;
    public SgrColor? Background;
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public bool Faint;
    public bool Inverse;
    public SgrColor? UnderlineColor;
    public byte Width;
    public bool IsContinuation;

    public void Reset()
    {
        Grapheme = null;
        Foreground = null;
        Background = null;
        Bold = false;
        Italic = false;
        Underline = false;
        Faint = false;
        Inverse = false;
        UnderlineColor = null;
        Width = 0;
        IsContinuation = false;
    }

    public readonly bool IsEmpty => string.IsNullOrEmpty(Grapheme) && !IsContinuation;
}
