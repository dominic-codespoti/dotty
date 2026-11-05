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
/// How an explicit foreground, background, or underline colour was specified. The ARGB value alone cannot tell a palette colour
/// from a truecolor one (Unicode placeholder cells encode an image id in the foreground and the two cases carry different bit widths).
/// </summary>
public enum ColorKind : byte
{
    /// <summary>No explicit colour (SGR 39 / 49 / 59 / 0).</summary>
    Default = 0,

    /// <summary>A palette entry (30-37, 40-47, 90-97, 100-107, or extended indexed colour); see the channel index properties.</summary>
    Indexed = 1,

    /// <summary>A direct RGB colour (38;2;r;g;b, 48;2;r;g;b, 58;2;r;g;b).</summary>
    Rgb = 2,
}

/// <summary>
/// Cell attributes using zero-allocation ARGB colors instead of hex strings.
/// </summary>
public struct CellAttributes : IEquatable<CellAttributes>
{
    // The colour-kind tags are owned by the SGR parser through Set*/Clear* methods. Assigning ARGB values directly
    // deliberately keeps indexed identity so palette remaps can update only palette-specified colours.
    private bool _fgIndexed;
    private bool _bgIndexed;
    private bool _ulIndexed;
    private byte _fgIndex;
    private byte _bgIndex;
    private byte _ulIndex;

    public SgrColorArgb Foreground { get; set; }
    public SgrColorArgb Background { get; set; }
    public SgrColorArgb UnderlineColor { get; set; }

    /// <summary>Kind of <see cref="Foreground"/>: <see cref="ColorKind.Default"/> whenever it is empty.</summary>
    public readonly ColorKind ForegroundKind => Foreground.IsEmpty ? ColorKind.Default : _fgIndexed ? ColorKind.Indexed : ColorKind.Rgb;

    /// <summary>Kind of <see cref="Background"/>: <see cref="ColorKind.Default"/> whenever it is empty.</summary>
    public readonly ColorKind BackgroundKind => Background.IsEmpty ? ColorKind.Default : _bgIndexed ? ColorKind.Indexed : ColorKind.Rgb;

    /// <summary>Kind of <see cref="UnderlineColor"/>: <see cref="ColorKind.Default"/> whenever it is empty.</summary>
    public readonly ColorKind UnderlineColorKind => UnderlineColor.IsEmpty ? ColorKind.Default : _ulIndexed ? ColorKind.Indexed : ColorKind.Rgb;

    /// <summary>Palette index (0-255) of the foreground when <see cref="ForegroundKind"/> is Indexed, otherwise -1.</summary>
    public readonly int ForegroundIndex => ForegroundKind == ColorKind.Indexed ? _fgIndex : -1;

    /// <summary>Palette index (0-255) of the background when <see cref="BackgroundKind"/> is Indexed, otherwise -1.</summary>
    public readonly int BackgroundIndex => BackgroundKind == ColorKind.Indexed ? _bgIndex : -1;

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

    /// <summary>Sets a palette background colour: <paramref name="argb"/> is the palette entry's current value.</summary>
    public void SetBackgroundIndexed(int index, SgrColorArgb argb)
    {
        Background = argb;
        _bgIndexed = true;
        _bgIndex = (byte)index;
    }

    /// <summary>Sets a direct RGB background colour (clears any palette tag).</summary>
    public void SetBackgroundRgb(SgrColorArgb argb)
    {
        Background = argb;
        _bgIndexed = false;
        _bgIndex = 0;
    }

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

    /// <summary>
    /// Updates the ARGB values of indexed channels while retaining their palette identities and all other attributes.
    /// </summary>
    public void RemapPalette(ReadOnlySpan<uint> colors)
    {
        if (colors.Length != 256)
            throw new ArgumentException("Palette must contain exactly 256 colors.", nameof(colors));
        if (ForegroundKind == ColorKind.Indexed && Foreground.Argb != colors[_fgIndex])
            Foreground = new SgrColorArgb(colors[_fgIndex]);
        if (BackgroundKind == ColorKind.Indexed && Background.Argb != colors[_bgIndex])
            Background = new SgrColorArgb(colors[_bgIndex]);
        if (UnderlineColorKind == ColorKind.Indexed && UnderlineColor.Argb != colors[_ulIndex])
            UnderlineColor = new SgrColorArgb(colors[_ulIndex]);
    }

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
            && BackgroundKind == other.BackgroundKind
            && UnderlineColorKind == other.UnderlineColorKind
            && ForegroundIndex == other.ForegroundIndex
            && BackgroundIndex == other.BackgroundIndex
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
            HashCode.Combine(BackgroundIndex, UnderlineColorIndex));
    }
}
