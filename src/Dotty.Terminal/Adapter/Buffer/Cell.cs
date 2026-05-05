using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

[StructLayout(LayoutKind.Sequential)]
public struct CellHot
{
    public uint Rune;
    public ushort StyleId;
    public byte PackedFlags;
    private readonly byte _pad;

    public byte Width
    {
        get => (byte)((PackedFlags & 0x01) != 0 ? 2 : 1);
        set => PackedFlags = (byte)((PackedFlags & ~0x01) | (value > 1 ? 0x01 : 0));
    }

    public bool IsContinuation
    {
        get => (PackedFlags & 0x02) != 0;
        set => PackedFlags = value ? (byte)(PackedFlags | 0x02) : (byte)(PackedFlags & ~0x02);
    }

    public bool HasHyperlink
    {
        get => (PackedFlags & 0x04) != 0;
        set => PackedFlags = value ? (byte)(PackedFlags | 0x04) : (byte)(PackedFlags & ~0x04);
    }

    public bool HasGrapheme
    {
        get => (PackedFlags & 0x08) != 0;
        set => PackedFlags = value ? (byte)(PackedFlags | 0x08) : (byte)(PackedFlags & ~0x08);
    }

    public readonly bool IsEmpty => Rune == 0 && (PackedFlags & 0x02) == 0;

    public void Reset()
    {
        Rune = 0;
        StyleId = 0;
        PackedFlags = 0;
    }

    public void SetAscii(char ch)
    {
        Rune = ch;
    }
}
