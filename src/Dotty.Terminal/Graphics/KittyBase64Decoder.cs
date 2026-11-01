using System;
using System.Buffers;
using System.Buffers.Text;

namespace Dotty.Terminal.Graphics;

internal enum KittyBase64Status : byte
{
    Ok,
    /// <summary>A byte outside the base64 alphabet, or misplaced/incorrect padding. Sticky.</summary>
    Error,
    /// <summary>The decoded bytes would not fit the destination span. Sticky.</summary>
    Overflow,
}

/// <summary>
/// Streaming standard-alphabet base64 decoder for Kitty graphics payloads.
/// <para>
/// Input may be fed in arbitrary slices (the parser delivers any size) and across protocol chunks (<c>m=1</c>
/// continuation chunks): up to three characters of an incomplete quartet are carried between calls, so quartets may
/// straddle slice and chunk boundaries.
/// </para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Alphabet is <c>A-Za-z0-9+/</c>. Any other byte (whitespace and line breaks included) is an error.</item>
/// <item><c>=</c> padding is only accepted in the final quartet of the <em>final</em> chunk
/// (<c>isFinalChunk == true</c>): it must follow exactly 2 or 3 data characters of that quartet and must complete it
/// (<c>xx==</c> / <c>xxx=</c>); nothing may follow it. Padding in a non-final chunk is an error.</item>
/// <item>Padding is optional at the very end of the final chunk: <see cref="Finish"/> accepts a trailing partial
/// quartet of 2 or 3 characters (clients commonly omit padding) and rejects a lone character.</item>
/// <item>Non-zero unused trailing bits in the last character (e.g. <c>TW</c> instead of <c>TQ</c>) are an error; the
/// BCL decoder rejects them (not checked here).</item>
/// <item>Errors and overflows are sticky: once <see cref="Status"/> is not <see cref="KittyBase64Status.Ok"/> all
/// further input is ignored and the same status is returned.</item>
/// </list>
/// On a non-Ok result the destination content is unspecified and <c>written</c> is 0.
/// </summary>
internal sealed class KittyBase64Decoder
{
    private static readonly SearchValues<byte> Alphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8);

    private readonly byte[] _carry = new byte[4];
    private int _carryLen;
    private int _padCount;
    private KittyBase64Status _status;

    /// <summary>Total bytes written across all calls since construction or <see cref="Reset"/>.</summary>
    public long TotalDecoded { get; private set; }

    public KittyBase64Status Status => _status;

    /// <summary>Number of alphabet characters currently carried (0..3) waiting for the rest of their quartet.</summary>
    public int PendingChars => _carryLen;

    public void Reset()
    {
        _carryLen = 0;
        _padCount = 0;
        _status = KittyBase64Status.Ok;
        TotalDecoded = 0;
    }

    /// <summary>
    /// Decodes <paramref name="input"/> into the start of <paramref name="dst"/>; <paramref name="written"/> is the
    /// number of bytes produced by this call (the caller advances its own destination by that amount).
    /// </summary>
    public KittyBase64Status Decode(ReadOnlySpan<byte> input, Span<byte> dst, out int written, bool isFinalChunk)
    {
        written = 0;
        if (_status != KittyBase64Status.Ok) return _status;
        int pos = 0;
        var rest = input;
        while (!rest.IsEmpty)
        {
            if (_padCount > 0 || rest[0] == (byte)'=')
            {
                if (!isFinalChunk) return Fail(KittyBase64Status.Error);
                if (_padCount == 0 && _carryLen < 2) return Fail(KittyBase64Status.Error);
                if (rest.Length > 4 || rest.IndexOfAnyExcept((byte)'=') >= 0) return Fail(KittyBase64Status.Error);
                bool wasOpen = _carryLen + _padCount < 4;
                _padCount += rest.Length;
                if (_carryLen + _padCount > 4) return Fail(KittyBase64Status.Error);
                if (wasOpen && _carryLen + _padCount == 4)
                {
                    var st = DecodeCarryWithPadding(dst.Slice(pos), out int w);
                    if (st != KittyBase64Status.Ok) return Fail(st);
                    pos += w;
                }
                break;
            }

            int dataLen = rest.IndexOfAnyExcept(Alphabet);
            if (dataLen < 0) dataLen = rest.Length;
            var data = rest.Slice(0, dataLen);
            rest = rest.Slice(dataLen);
            if (!rest.IsEmpty && rest[0] != (byte)'=') return Fail(KittyBase64Status.Error);

            if (_carryLen > 0)
            {
                int take = Math.Min(4 - _carryLen, data.Length);
                data.Slice(0, take).CopyTo(_carry.AsSpan(_carryLen));
                _carryLen += take;
                data = data.Slice(take);
                if (_carryLen == 4)
                {
                    if (dst.Length - pos < 3) return Fail(KittyBase64Status.Overflow);
                    if (Base64.DecodeFromUtf8(_carry, dst.Slice(pos), out _, out int w, isFinalBlock: false) != OperationStatus.Done || w != 3)
                        return Fail(KittyBase64Status.Error);
                    pos += w;
                    _carryLen = 0;
                }
            }

            int whole = data.Length & ~3;
            if (whole > 0)
            {
                int need = whole / 4 * 3;
                if (dst.Length - pos < need) return Fail(KittyBase64Status.Overflow);
                if (Base64.DecodeFromUtf8(data.Slice(0, whole), dst.Slice(pos), out _, out int w, isFinalBlock: false) != OperationStatus.Done || w != need)
                    return Fail(KittyBase64Status.Error);
                pos += w;
                data = data.Slice(whole);
            }

            if (!data.IsEmpty)
            {
                // _carryLen is 0 here: a non-empty remainder only exists when no carry was pending or it was flushed.
                data.CopyTo(_carry);
                _carryLen = data.Length;
            }
        }

        written = pos;
        TotalDecoded += pos;
        return KittyBase64Status.Ok;
    }

    /// <summary>
    /// Ends the stream (call once after the last slice of the final chunk). Flushes a trailing unpadded partial
    /// quartet of 2 or 3 characters; a lone character or an incomplete padded quartet is an error.
    /// </summary>
    public KittyBase64Status Finish(Span<byte> dst, out int written)
    {
        written = 0;
        if (_status != KittyBase64Status.Ok) return _status;
        if (_padCount > 0)
        {
            // Complete padded quartets were already decoded in Decode.
            if (_carryLen + _padCount != 4) return Fail(KittyBase64Status.Error);
            return KittyBase64Status.Ok;
        }
        if (_carryLen == 0) return KittyBase64Status.Ok;
        if (_carryLen == 1) return Fail(KittyBase64Status.Error);

        _padCount = 4 - _carryLen;
        var st = DecodeCarryWithPadding(dst, out int w);
        if (st != KittyBase64Status.Ok) return Fail(st);
        written = w;
        TotalDecoded += w;
        return KittyBase64Status.Ok;
    }

    // Decodes the carried 2 or 3 characters as a padded final quartet.
    private KittyBase64Status DecodeCarryWithPadding(Span<byte> dst, out int written)
    {
        written = 0;
        int bytes = _carryLen - 1;
        if (dst.Length < bytes) return KittyBase64Status.Overflow;
        Span<byte> quad = stackalloc byte[4];
        quad.Fill((byte)'=');
        _carry.AsSpan(0, _carryLen).CopyTo(quad);
        Span<byte> tmp = stackalloc byte[3];
        if (Base64.DecodeFromUtf8(quad, tmp, out _, out int w, isFinalBlock: true) != OperationStatus.Done || w != bytes)
            return KittyBase64Status.Error;
        tmp.Slice(0, w).CopyTo(dst);
        written = w;
        return KittyBase64Status.Ok;
    }

    // Records the sticky failure. On any non-Ok result "written" is 0 and the destination content is unspecified.
    private KittyBase64Status Fail(KittyBase64Status status)
    {
        _status = status;
        return status;
    }
}
