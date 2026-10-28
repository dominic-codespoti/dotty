using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Rarely-used per-cell data (OSC 8 hyperlink id, multi-codepoint grapheme index) kept apart from
/// <see cref="CellHot"/>. 4 bytes; <c>default(ColdCell)</c> — all-zero bytes — is the empty cell
/// (<see cref="HyperlinkId"/> 0, <see cref="GraphemeIndex"/> -1). The screen arenas rely on that: freshly
/// reserved or discarded OS pages are zero, so they are already a valid blank cold plane without being
/// initialised.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ColdCell
{
    public ushort HyperlinkId;

    // Stored as index + 1 so that zero (the default) means "no grapheme".
    private ushort _graphemePlusOne;

    /// <summary>Index into the grapheme table, or <c>-1</c> when the cell has none.</summary>
    public short GraphemeIndex
    {
        readonly get => (short)(_graphemePlusOne - 1);
        set => _graphemePlusOne = (ushort)(value < 0 ? 0 : value + 1);
    }

    public void Reset()
    {
        HyperlinkId = 0;
        _graphemePlusOne = 0;
    }
}
