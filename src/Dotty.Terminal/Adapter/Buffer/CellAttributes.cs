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
/// How a foreground or underline colour was specified. The ARGB value alone cannot tell a palette colour
/// from a truecolor one (Unicode placeholder cells encode an image id in the foreground and the two cases
/// carry different bit widths).
/// </summary>
public enum ColorKind : byte
{
    /// <summary>No explicit colour (SGR 39 / 59 / 0).</summary>
    Default = 0,

    /// <summary>A palette entry (30-37, 90-97, 38;5;n, 58;5;n); see <see cref="CellAttributes.ForegroundIndex"/>.</summary>
    Indexed = 1,

    /// <summary>A direct RGB colour (38;2;r;g;b, 58;2;r;g;b).</summary>
    Rgb = 2,
}

/// <summary>
/// Cell attributes using zero-allocation ARGB colors instead of hex strings.
/// </summary>
public struct CellAttributes : IEquatable<CellAttributes>
{
    // The colour-kind tags (_fgIndexed/_fgIndex, _ulIndexed/_ulIndex) are owned by the SGR parser through the
    // Set*/Clear* methods below. Assigning Foreground/UnderlineColor directly (e.g. the theme remap in
    // StyleSet) changes only the ARGB value and deliberately keeps the tag, so a palette colour stays a
    // palette colour across a theme change. Tags take part in Equals/GetHashCode, which keeps StyleSet
    // interning from merging "palette 1" with an RGB colour that happens to have the same ARGB.
    private bool _fgIndexed;
    private bool _ulIndexed;
    private byte _fgIndex;
    private byte _ulIndex;

    public SgrColorArgb Foreground { get; set; }
    public SgrColorArgb Background { get; set; }
    public SgrColorArgb UnderlineColor { get; set; }

    /// <summary>Kind of <see cref="Foreground"/>: <see cref="ColorKind.Default"/> whenever it is empty.</summary>
    public readonly ColorKind ForegroundKind => Foreground.IsEmpty ? ColorKind.Default : _fgIndexed ? ColorKind.Indexed : ColorKind.Rgb;

    /// <summary>Kind of <see cref="UnderlineColor"/>: <see cref="ColorKind.Default"/> whenever it is empty.</summary>
    public readonly ColorKind UnderlineColorKind => UnderlineColor.IsEmpty ? ColorKind.Default : _ulIndexed ? ColorKind.Indexed : ColorKind.Rgb;

    /// <summary>Palette index (0-255) of the foreground when <see cref="ForegroundKind"/> is Indexed, otherwise -1.</summary>
    public readonly int ForegroundIndex => ForegroundKind == ColorKind.Indexed ? _fgIndex : -1;

    /// <summary>Palette index (0-255) of the underline colour when <see cref="UnderlineColorKind"/> is Indexed, otherwise -1.</summary>
    public readonly int UnderlineColorIndex => UnderlineColorKind == ColorKind.Indexed ? _ulIndex : -1;

    /// <summary>Sets a palette foreground colour: <paramref name="argb"/> is the palette entry's current value.</summary>
    public void SetForegroundIndexed(int index, SgrColorArgb argb)
    {
        Foreground = argb;
        _fgIndexed = true;
        _fgIndex = (byte)index;
    }

    /// <summary>Sets a direct RGB foreground colour (clears any palette tag).</summary>
    public void SetForegroundRgb(SgrColorArgb argb)
    {
        Foreground = argb;
        _fgIndexed = false;
        _fgIndex = 0;
    }

    /// <summary>Resets the foreground to the terminal default.</summary>
    public void ClearForeground() => SetForegroundRgb(default);

    /// <summary>Sets a palette underline colour: <paramref name="argb"/> is the palette entry's current value.</summary>
    public void SetUnderlineColorIndexed(int index, SgrColorArgb argb)
    {
        UnderlineColor = argb;
        _ulIndexed = true;
        _ulIndex = (byte)index;
    }

    /// <summary>Sets a direct RGB underline colour (clears any palette tag).</summary>
    public void SetUnderlineColorRgb(SgrColorArgb argb)
    {
        UnderlineColor = argb;
        _ulIndexed = false;
        _ulIndex = 0;
    }

    /// <summary>Resets the underline colour to the terminal default.</summary>
    public void ClearUnderlineColor() => SetUnderlineColorRgb(default);

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
            && ForegroundKind == other.ForegroundKind
            && UnderlineColorKind == other.UnderlineColorKind
            && ForegroundIndex == other.ForegroundIndex
            && UnderlineColorIndex == other.UnderlineColorIndex
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
            HashCode.Combine(Inverse, Strikethrough, Overline, Invisible, SlowBlink, HyperlinkId),
            ForegroundIndex,
            UnderlineColorIndex);
    }
}
