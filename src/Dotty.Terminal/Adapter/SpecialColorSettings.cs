namespace Dotty.Terminal.Adapter;

/// <summary>
/// Per-terminal attribute colors. Mode bits 0–4 enable bold, underline, blink,
/// reverse and italic colors; bit 5 permits overriding explicitly selected colors.
/// </summary>
public readonly record struct SpecialColorSettings(
    uint Bold, uint Underline, uint Blink, uint Reverse, uint Italic, byte Modes)
{
    public bool AnyEnabled => (Modes & 31) != 0;
    public bool OverrideExplicitColors => (Modes & 32) != 0;

    internal uint Get(int index) => index switch
    {
        0 => Bold,
        1 => Underline,
        2 => Blink,
        3 => Reverse,
        _ => Italic,
    };

    internal SpecialColorSettings Set(int index, uint value) => index switch
    {
        0 => this with { Bold = value },
        1 => this with { Underline = value },
        2 => this with { Blink = value },
        3 => this with { Reverse = value },
        _ => this with { Italic = value },
    };
}
