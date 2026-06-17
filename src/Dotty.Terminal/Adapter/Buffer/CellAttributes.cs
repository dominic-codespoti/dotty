using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Underline style variants per ITU-T T.416 / SGR 4:x colon subparameters.
/// </summary>
public enum UnderlineStyle : byte
{
    None = 0,
    Single = 1,
    Double = 2,
    Curl = 3,
    Dotted = 4,
    Dashed = 5,
}

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
    public UnderlineStyle UnderlineStyle { get; set; }
    public bool Faint { get; set; }
    public bool Inverse { get; set; }
    public bool Strikethrough { get; set; }
    public bool Overline { get; set; }
    public bool Invisible { get; set; }
    public bool SlowBlink { get; set; }
    public ushort HyperlinkId { get; set; }

    // Backward-compatible computed properties.
    public bool Underline => UnderlineStyle != UnderlineStyle.None;
    public bool DoubleUnderline => UnderlineStyle == UnderlineStyle.Double;

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
            && UnderlineStyle == other.UnderlineStyle
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
            HashCode.Combine(Foreground, Background, UnderlineColor, Bold, Italic, (int)UnderlineStyle, Faint),
            HashCode.Combine(Inverse, Strikethrough, Overline, Invisible, SlowBlink, HyperlinkId));
    }
}
