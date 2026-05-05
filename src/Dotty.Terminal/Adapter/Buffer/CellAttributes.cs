using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Cell attributes using zero-allocation ARGB colors instead of hex strings.
/// </summary>
public struct CellAttributes : IEquatable<CellAttributes>
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

    public bool Equals(CellAttributes other)
    {
        return Foreground == other.Foreground
            && Background == other.Background
            && UnderlineColor == other.UnderlineColor
            && Bold == other.Bold
            && Italic == other.Italic
            && Underline == other.Underline
            && DoubleUnderline == other.DoubleUnderline
            && Faint == other.Faint
            && Inverse == other.Inverse
            && Strikethrough == other.Strikethrough
            && Overline == other.Overline
            && Invisible == other.Invisible
            && SlowBlink == other.SlowBlink
            && HyperlinkId == other.HyperlinkId;
    }

    public override bool Equals(object? obj)
    {
        return obj is CellAttributes other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            HashCode.Combine(Foreground, Background, UnderlineColor, Bold, Italic, Underline, DoubleUnderline, Faint),
            HashCode.Combine(Inverse, Strikethrough, Overline, Invisible, SlowBlink, HyperlinkId));
    }
}
