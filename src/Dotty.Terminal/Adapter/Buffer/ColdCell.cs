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

    // Stored as (short index) + 1 reinterpreted as ushort, so that zero (the default) means "no data".
    private ushort _graphemePlusOne;

    /// <summary>
    /// <c>-1</c> when the cell has no grapheme data; <c>&gt;= 0</c> an index into the grapheme table (only
    /// <c>&gt; 0</c> is a real entry); <c>&lt;= -2</c> a Unicode-placeholder code (see
    /// <c>Dotty.Terminal.Graphics.PlaceholderTable</c>), never a grapheme-store index. Readers that test for
    /// "has cold data" must compare with <c>!= -1</c>, not <c>&gt;= 0</c>.
    /// </summary>
    public short GraphemeIndex
    {
        readonly get => (short)(_graphemePlusOne - 1);
        set => _graphemePlusOne = (ushort)(value + 1);
    }

    public void Reset()
    {
        HyperlinkId = 0;
        _graphemePlusOne = 0;
    }
}
