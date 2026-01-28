using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// High-performance zero-allocation StringBuilder inspired by ZString.
/// Uses ThreadStatic buffer for fast path, ArrayPool for nested/concurrent scenarios.
/// All primitive types are written directly to buffer without ToString() allocation.
/// </summary>
/// <remarks>
/// MUST be used with 'using' statement or try-finally to ensure buffer is returned.
/// Pass by ref when used as method parameter (it's a mutable struct).
/// </remarks>
public ref struct ValueStringBuilder
{
    private const int DefaultCapacity = 256;
    private const int ThreadStaticBufferSize = 65536; // 64KB like ZString

    [ThreadStatic]
    private static char[]? t_staticBuffer;

    private char[] _buffer;
    private int _length;
    private readonly bool _useThreadStaticBuffer;

    /// <summary>
    /// Creates a new ValueStringBuilder.
    /// </summary>
    /// <param name="notNested">
    /// If true, uses faster ThreadStatic buffer but MUST NOT be used in nested scenarios
    /// (no ZString/Concat calls or nested builders while this one is active).
    /// If false (default), uses ArrayPool which is safe for any scenario.
    /// </param>
    public ValueStringBuilder(bool notNested = false) : this(notNested, DefaultCapacity) { }

    /// <summary>
    /// Creates a new ValueStringBuilder with specified initial capacity.
    /// </summary>
    public ValueStringBuilder(bool notNested, int initialCapacity)
    {
        if (notNested)
        {
            _buffer = t_staticBuffer ?? new char[ThreadStaticBufferSize];
            t_staticBuffer = null; // Mark as in-use
            _useThreadStaticBuffer = true;
        }
        else
        {
            _buffer = ArrayPool<char>.Shared.Rent(Math.Max(initialCapacity, DefaultCapacity));
            _useThreadStaticBuffer = false;
        }
        _length = 0;
    }

    /// <summary>
    /// Creates a ValueStringBuilder with a pre-allocated buffer (for stack allocation scenarios).
    /// </summary>
    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _buffer = ArrayPool<char>.Shared.Rent(initialBuffer.Length);
        initialBuffer.CopyTo(_buffer);
        _length = 0;
        _useThreadStaticBuffer = false;
    }

    /// <summary>Length of the current content.</summary>
    public readonly int Length => _length;

    /// <summary>Current capacity of the buffer.</summary>
    public readonly int Capacity => _buffer.Length;

    /// <summary>Gets the written content as a ReadOnlySpan.</summary>
    public readonly ReadOnlySpan<char> AsSpan() => _buffer.AsSpan(0, _length);

    /// <summary>Gets a span to write to directly, then call Advance().</summary>
    public Span<char> GetSpan(int sizeHint)
    {
        EnsureCapacity(_length + sizeHint);
        return _buffer.AsSpan(_length);
    }

    /// <summary>Advances the length after writing to GetSpan().</summary>
    public void Advance(int count) => _length += count;

    /// <summary>Clears the content but keeps the buffer.</summary>
    public void Clear() => _length = 0;

    #region Append Methods - Zero Allocation for Primitives

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(char value)
    {
        int pos = _length;
        if ((uint)pos < (uint)_buffer.Length)
        {
            _buffer[pos] = value;
            _length = pos + 1;
        }
        else
        {
            AppendSlow(value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendSlow(char value)
    {
        Grow(1);
        _buffer[_length++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(string? value)
    {
        if (value is null) return;
        
        int length = value.Length;
        if (length == 0) return;

        EnsureCapacity(_length + length);
        value.AsSpan().CopyTo(_buffer.AsSpan(_length));
        _length += length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(scoped ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return;

        EnsureCapacity(_length + value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Appends an integer without any allocation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(int value)
    {
        // Fast path for small positive numbers (very common in terminal: row/col numbers)
        if (value >= 0 && value < 10)
        {
            Append((char)('0' + value));
            return;
        }
        
        Span<char> buffer = stackalloc char[11]; // int.MinValue is 11 chars
        if (value.TryFormat(buffer, out int charsWritten))
        {
            Append(buffer.Slice(0, charsWritten));
        }
    }

    /// <summary>Appends a long without any allocation.</summary>
    public void Append(long value)
    {
        Span<char> buffer = stackalloc char[20];
        if (value.TryFormat(buffer, out int charsWritten))
        {
            Append(buffer.Slice(0, charsWritten));
        }
    }

    /// <summary>Appends a uint without any allocation.</summary>
    public void Append(uint value)
    {
        Span<char> buffer = stackalloc char[10];
        if (value.TryFormat(buffer, out int charsWritten))
        {
            Append(buffer.Slice(0, charsWritten));
        }
    }

    /// <summary>Appends a double without any allocation.</summary>
    public void Append(double value, string? format = null)
    {
        Span<char> buffer = stackalloc char[32];
        if (value.TryFormat(buffer, out int charsWritten, format))
        {
            Append(buffer.Slice(0, charsWritten));
        }
    }

    /// <summary>Appends a float without any allocation.</summary>
    public void Append(float value, string? format = null)
    {
        Span<char> buffer = stackalloc char[32];
        if (value.TryFormat(buffer, out int charsWritten, format))
        {
            Append(buffer.Slice(0, charsWritten));
        }
    }

    /// <summary>Appends a boolean without any allocation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(bool value)
    {
        Append(value ? "True" : "False");
    }

    /// <summary>Generic append - uses TryFormat if available, falls back to ToString().</summary>
    public void Append<T>(T value)
    {
        if (value is null) return;

        switch (value)
        {
            case string s: Append(s); break;
            case char c: Append(c); break;
            case int i: Append(i); break;
            case long l: Append(l); break;
            case uint u: Append(u); break;
            case double d: Append(d); break;
            case float f: Append(f); break;
            case bool b: Append(b); break;
            case ISpanFormattable sf:
                Span<char> buffer = stackalloc char[64];
                if (sf.TryFormat(buffer, out int charsWritten, default, null))
                {
                    Append(buffer.Slice(0, charsWritten));
                }
                else
                {
                    Append(value.ToString());
                }
                break;
            default:
                Append(value.ToString());
                break;
        }
    }

    #endregion

    #region AppendLine Methods

    public void AppendLine() => Append(Environment.NewLine);

    public void AppendLine(string? value)
    {
        Append(value);
        AppendLine();
    }

    public void AppendLine(char value)
    {
        Append(value);
        AppendLine();
    }

    public void AppendLine(int value)
    {
        Append(value);
        AppendLine();
    }

    public void AppendLine<T>(T value)
    {
        Append(value);
        AppendLine();
    }

    #endregion

    #region Join Methods

    public void AppendJoin(char separator, ReadOnlySpan<string?> values)
    {
        if (values.IsEmpty) return;

        Append(values[0]);
        for (int i = 1; i < values.Length; i++)
        {
            Append(separator);
            Append(values[i]);
        }
    }

    public void AppendJoin(string separator, ReadOnlySpan<string?> values)
    {
        if (values.IsEmpty) return;

        Append(values[0]);
        for (int i = 1; i < values.Length; i++)
        {
            Append(separator);
            Append(values[i]);
        }
    }

    #endregion

    #region Buffer Management

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int capacity)
    {
        if ((uint)capacity > (uint)_buffer.Length)
        {
            Grow(capacity - _length);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int additionalCapacity)
    {
        int newCapacity = Math.Max(_buffer.Length * 2, _length + additionalCapacity);
        
        char[] newBuffer = ArrayPool<char>.Shared.Rent(newCapacity);
        _buffer.AsSpan(0, _length).CopyTo(newBuffer);

        if (!_useThreadStaticBuffer)
        {
            ArrayPool<char>.Shared.Return(_buffer);
        }
        
        _buffer = newBuffer;
    }

    /// <summary>
    /// Copies the content to the destination span.
    /// </summary>
    public readonly bool TryCopyTo(Span<char> destination, out int charsWritten)
    {
        if (destination.Length >= _length)
        {
            _buffer.AsSpan(0, _length).CopyTo(destination);
            charsWritten = _length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    #endregion

    /// <summary>
    /// Creates the final string. This is the only allocation in the entire operation.
    /// </summary>
    public override readonly string ToString()
    {
        return _length == 0 ? string.Empty : new string(_buffer, 0, _length);
    }

    /// <summary>
    /// Returns the buffer to the pool. MUST be called (use 'using' statement).
    /// </summary>
    public void Dispose()
    {
        char[]? buffer = _buffer;
        _buffer = null!;
        _length = 0;

        if (buffer is null) return;

        if (_useThreadStaticBuffer)
        {
            // Return to ThreadStatic cache
            t_staticBuffer = buffer;
        }
        else
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Static helper methods for common string operations without allocation.
/// </summary>
public static class ZStr
{
    /// <summary>
    /// Concatenates values without intermediate string allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Concat<T1, T2>(T1 arg1, T2 arg2)
    {
        using var sb = new ValueStringBuilder(notNested: true);
        sb.Append(arg1);
        sb.Append(arg2);
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Concat<T1, T2, T3>(T1 arg1, T2 arg2, T3 arg3)
    {
        using var sb = new ValueStringBuilder(notNested: true);
        sb.Append(arg1);
        sb.Append(arg2);
        sb.Append(arg3);
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Concat<T1, T2, T3, T4>(T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        using var sb = new ValueStringBuilder(notNested: true);
        sb.Append(arg1);
        sb.Append(arg2);
        sb.Append(arg3);
        sb.Append(arg4);
        return sb.ToString();
    }

    /// <summary>
    /// Joins values with a separator without intermediate allocations.
    /// </summary>
    public static string Join<T>(char separator, ReadOnlySpan<T> values)
    {
        if (values.IsEmpty) return string.Empty;

        using var sb = new ValueStringBuilder(notNested: true);
        sb.Append(values[0]);
        for (int i = 1; i < values.Length; i++)
        {
            sb.Append(separator);
            sb.Append(values[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Creates a string builder. Prefer using ValueStringBuilder directly with 'using'.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueStringBuilder CreateStringBuilder(bool notNested = false)
        => new ValueStringBuilder(notNested);

    /// <summary>
    /// Creates a string builder with initial capacity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueStringBuilder CreateStringBuilder(int capacity)
        => new ValueStringBuilder(notNested: false, initialCapacity: capacity);
}

// Keep backward compatibility alias
/// <summary>
/// Backward compatibility - use ValueStringBuilder directly for new code.
/// </summary>
public static class StringBuilderPool
{
    /// <summary>
    /// Creates a pooled StringBuilder. Use ValueStringBuilder directly for better performance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueStringBuilder Rent(int capacity = 256)
        => new ValueStringBuilder(notNested: false, initialCapacity: capacity);
    
    /// <summary>
    /// Gets the string and disposes the builder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ToStringAndReturn(ref ValueStringBuilder sb)
    {
        var result = sb.ToString();
        sb.Dispose();
        return result;
    }
}
