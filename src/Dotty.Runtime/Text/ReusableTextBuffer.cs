using System;
using System.Text;

namespace Dotty.Runtime.Text;

/// <summary>
/// Growable, reusable UTF-16 text storage for display strings that change at runtime
/// (Lua-formatted titles, status text, warnings). Setting identical content is a no-op
/// and reports <c>false</c>; storage only grows, so steady-state updates never allocate.
/// Not thread-safe: owned and used on the UI thread.
/// </summary>
public sealed class ReusableTextBuffer
{
    private char[] _chars;
    private int _length;

    public ReusableTextBuffer(int initialCapacity = 64)
    {
        _chars = new char[Math.Max(1, initialCapacity)];
    }

    public int Length => _length;

    public bool IsEmpty => _length == 0;

    public ReadOnlySpan<char> Span => _chars.AsSpan(0, _length);

    /// <summary>Clears the content. Returns true if it was non-empty.</summary>
    public bool Clear()
    {
        bool changed = _length != 0;
        _length = 0;
        return changed;
    }

    /// <summary>Replaces the content. Returns true when the content changed.</summary>
    public bool Set(ReadOnlySpan<char> value)
    {
        if (value.SequenceEqual(Span))
            return false;

        EnsureCapacity(value.Length);
        value.CopyTo(_chars);
        _length = value.Length;
        return true;
    }

    /// <summary>Replaces the content with decoded UTF-8. Returns true when the content changed.</summary>
    public bool SetUtf8(ReadOnlySpan<byte> utf8)
    {
        int needed = Encoding.UTF8.GetMaxCharCount(utf8.Length);
        if (needed <= 512)
        {
            Span<char> scratch = stackalloc char[needed];
            int written = Encoding.UTF8.GetChars(utf8, scratch);
            return Set(scratch[..written]);
        }

        int count = Encoding.UTF8.GetCharCount(utf8);
        if (count == _length)
        {
            char[] rented = System.Buffers.ArrayPool<char>.Shared.Rent(count);
            try
            {
                int written = Encoding.UTF8.GetChars(utf8, rented);
                return Set(rented.AsSpan(0, written));
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }

        EnsureCapacity(count);
        _length = Encoding.UTF8.GetChars(utf8, _chars);
        return true;
    }

    private void EnsureCapacity(int length)
    {
        if (_chars.Length >= length)
            return;

        int next = Math.Max(length, _chars.Length * 2);
        _chars = new char[next];
    }

    public override string ToString() => new(Span);
}
