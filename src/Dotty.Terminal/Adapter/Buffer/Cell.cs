using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

[StructLayout(LayoutKind.Sequential)]
public struct Cell
{
    public uint Rune;
    public uint Foreground;
    public uint Background;
    public uint UnderlineColor;
    
    public ushort Flags;
    public byte Width;
    public bool IsContinuation;

    public void Reset()
    {
        Rune = 0;
        Foreground = 0;
        Background = 0;
        Flags = 0;
        Width = 0;
        IsContinuation = false;
        UnderlineColor = 0;
    }

    public readonly bool IsEmpty => Rune == 0 && !IsContinuation;
    
    public bool Bold { get => (Flags & 1) != 0; set => Flags = (ushort)(value ? (Flags | 1) : (Flags & ~1)); }
    public bool Italic { get => (Flags & 2) != 0; set => Flags = (ushort)(value ? (Flags | 2) : (Flags & ~2)); }
    public bool Underline { get => (Flags & 4) != 0; set => Flags = (ushort)(value ? (Flags | 4) : (Flags & ~4)); }
    public bool DoubleUnderline { get => (Flags & 8) != 0; set => Flags = (ushort)(value ? (Flags | 8) : (Flags & ~8)); }
    public bool Faint { get => (Flags & 16) != 0; set => Flags = (ushort)(value ? (Flags | 16) : (Flags & ~16)); }
    public bool Inverse { get => (Flags & 32) != 0; set => Flags = (ushort)(value ? (Flags | 32) : (Flags & ~32)); }
    public bool Strikethrough { get => (Flags & 64) != 0; set => Flags = (ushort)(value ? (Flags | 64) : (Flags & ~64)); }
    public bool Overline { get => (Flags & 128) != 0; set => Flags = (ushort)(value ? (Flags | 128) : (Flags & ~128)); }
    public bool Invisible { get => (Flags & 256) != 0; set => Flags = (ushort)(value ? (Flags | 256) : (Flags & ~256)); }
    public bool SlowBlink { get => (Flags & 512) != 0; set => Flags = (ushort)(value ? (Flags | 512) : (Flags & ~512)); }
    
    public string? Grapheme 
    { 
        get => Rune == 0 ? null : char.ConvertFromUtf32((int)Rune); 
        set => Rune = string.IsNullOrEmpty(value) ? 0 : (uint)char.ConvertToUtf32(value, 0); 
    }
}
