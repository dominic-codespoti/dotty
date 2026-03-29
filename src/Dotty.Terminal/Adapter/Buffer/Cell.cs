using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

[StructLayout(LayoutKind.Sequential)]
public struct Cell
{
    private static readonly string[] s_asciiCache = BuildAsciiCache();

    public uint Rune;
    public uint Foreground;
    public uint Background;
    public uint UnderlineColor;
    
    public ushort Flags;
    public byte Width;
    public bool IsContinuation;
    
    // Stores multi-character graphemes (base + combining marks)
    // When this is null, use Rune field for single codepoint graphemes
    private string? _grapheme;

    public void Reset()
    {
        Rune = 0;
        Foreground = 0;
        Background = 0;
        Flags = 0;
        Width = 0;
        IsContinuation = false;
        UnderlineColor = 0;
        _grapheme = null;
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
        get
        {
            if (_grapheme != null) return _grapheme;
            if (Rune == 0) return null;
            if (Rune < 128) return s_asciiCache[Rune];
            return char.ConvertFromUtf32((int)Rune);
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                Rune = 0;
                _grapheme = null;
                return;
            }

            // Check if it's a multi-character grapheme cluster (combining marks, etc.)
            // or contains invalid UTF-16 that can't be represented as a single codepoint
            if (value.Length > 1)
            {
                _grapheme = value;
                // Try to store the first codepoint in Rune as well for fast access
                try
                {
                    Rune = (uint)char.ConvertToUtf32(value, 0);
                }
                catch (ArgumentException)
                {
                    // Invalid surrogate pair - just keep Rune as 0, we have the full string
                    Rune = 0;
                }
            }
            else
            {
                // Single character
                _grapheme = null;
                try
                {
                    Rune = (uint)char.ConvertToUtf32(value, 0);
                }
                catch (ArgumentException)
                {
                    // Invalid surrogate pair - treat as empty
                    Rune = 0;
                }
            }
        }
    }

    public void SetAscii(char ch)
    {
        Rune = ch;
        _grapheme = null;
    }

    private static string[] BuildAsciiCache()
    {
        var cache = new string[128];
        for (int i = 0; i < cache.Length; i++) cache[i] = ((char)i).ToString();
        return cache;
    }
}
