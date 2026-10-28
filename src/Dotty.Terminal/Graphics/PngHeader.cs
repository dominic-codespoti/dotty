using System;
using System.Buffers.Binary;

namespace Dotty.Terminal.Graphics;

/// <summary>Fields of a PNG IHDR chunk plus a cheap transparency hint.</summary>
/// <param name="HasTrns">
/// True when a <c>tRNS</c> chunk is seen among the chunks between IHDR and the first IDAT inside the span that was
/// passed. Always false for colour types 4 and 6 (alpha is intrinsic) and false when the span ends before the answer is known.
/// </param>
public readonly record struct PngInfo(int Width, int Height, byte BitDepth, byte ColorType, byte Interlace, bool HasTrns)
{
    /// <summary>True when the format carries or may carry non-opaque pixels (alpha channel or tRNS).</summary>
    public bool MayBeTransparent => ColorType is 4 or 6 || HasTrns;
}

/// <summary>Allocation-free PNG signature + IHDR validation. Does not verify CRCs or inspect pixel data.</summary>
public static class PngHeader
{
    private const int SignatureLength = 8;
    private const int IhdrEnd = SignatureLength + 4 + 4 + 13 + 4; // 33
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryRead(ReadOnlySpan<byte> png, out PngInfo info)
    {
        info = default;
        if (png.Length < IhdrEnd || !png.StartsWith(Signature)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13) return false;
        if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') return false;

        uint w = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        uint h = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;

        byte depth = png[24], color = png[25], compression = png[26], filter = png[27], interlace = png[28];
        if (compression != 0 || filter != 0 || interlace > 1) return false;
        bool depthOk = color switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false,
        };
        if (!depthOk) return false;

        info = new PngInfo((int)w, (int)h, depth, color, interlace, color is 0 or 2 or 3 && ScanForTrns(png.Slice(IhdrEnd)));
        return true;
    }

    private static bool ScanForTrns(ReadOnlySpan<byte> rest)
    {
        while (rest.Length >= 8)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(rest);
            ReadOnlySpan<byte> type = rest.Slice(4, 4);
            if (type.SequenceEqual("tRNS"u8)) return true;
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return false;
            long next = 12L + len; // length + type + data + crc
            if (next > rest.Length) return false;
            rest = rest.Slice((int)next);
        }
        return false;
    }
}
