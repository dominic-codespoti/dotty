using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Dotty.Terminal.Adapter;

public struct TerminalGrapheme : IEquatable<TerminalGrapheme>
{
    public char C0;
    public char C1;
    public short Length;
    public int PoolIndex;

    public TerminalGrapheme(char c)
    {
        C0 = c;
        C1 = '\0';
        Length = 1;
        PoolIndex = -1;
    }

    public TerminalGrapheme(char c0, char c1)
    {
        C0 = c0;
        C1 = c1;
        Length = 2;
        PoolIndex = -1;
    }

    public TerminalGrapheme(int poolIndex, short length)
    {
        C0 = '\0';
        C1 = '\0';
        Length = length;
        PoolIndex = poolIndex;
    }

    public readonly bool IsEmpty => Length == 0 && (PoolIndex == -1 || PoolIndex == 0);

    public readonly bool IsSpace => Length == 1 && C0 == ' ';

    public void Reset()
    {
        C0 = '\0';
        C1 = '\0';
        Length = 0;
        PoolIndex = -1;
    }

    public override string ToString() { Span<char> span = stackalloc char[32]; int w = GraphemePool.GetSpan(this, span); return span.Slice(0, w).ToString(); } public override bool Equals(object? obj) => obj is TerminalGrapheme other && Equals(other); public override int GetHashCode() => HashCode.Combine(C0, C1, Length, PoolIndex); public static bool operator ==(TerminalGrapheme left, TerminalGrapheme right) => left.Equals(right); public static bool operator !=(TerminalGrapheme left, TerminalGrapheme right) => !(left == right); public bool Equals(TerminalGrapheme other)
    {
        return C0 == other.C0 && C1 == other.C1 && Length == other.Length && PoolIndex == other.PoolIndex;
    }
}
