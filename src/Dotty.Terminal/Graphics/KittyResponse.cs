using System;

namespace Dotty.Terminal.Graphics;

/// <summary>Outcome of a graphics command; each non-Ok member has one fixed ASCII message (see <see cref="KittyResponse.Message"/>).</summary>
internal enum KittyStatus : byte
{
    Ok = 0,

    InvalidKeyValue,
    UnknownAction,
    UnknownFormat,
    InvalidBase64,
    BothIdAndNumber,
    SizeMismatch,
    UnsupportedMedium,
    InvalidGeometry,
    SelfParent,
    VirtualRelative,
    RectOutOfBounds,

    ImageNotFound,
    FrameNotFound,
    PlacementNotFound,

    StorageQuota,
    TooManyPlacements,

    InsufficientData,

    ImageTooLarge,
    PayloadTooLarge,

    BadFile,

    DecodeFailed,
    InflateFailed,

    TooDeep,
    Cycle,
    NoParent,
    BadPng,
}

/// <summary>
/// Formats kitty graphics replies <c>ESC _ G i=&lt;id&gt;[,I=&lt;n&gt;][,p=&lt;pid&gt;];&lt;msg&gt; ESC \</c> and applies the
/// quiet rules. Pure, allocation-free.
/// </summary>
internal static class KittyResponse
{
    /// <summary>Upper bound of a formatted reply in chars; callers may <c>stackalloc char[MaxLength]</c>.</summary>
    public const int MaxLength = 192;

    /// <summary>The single table of fixed reply messages.</summary>
    public static string Message(KittyStatus status) => status switch
    {
        KittyStatus.Ok => "OK",
        KittyStatus.InvalidKeyValue => "EINVAL:Invalid key value",
        KittyStatus.UnknownAction => "EINVAL:Unknown action",
        KittyStatus.UnknownFormat => "EINVAL:Unknown image format",
        KittyStatus.InvalidBase64 => "EINVAL:Invalid base64 data",
        KittyStatus.BothIdAndNumber => "EINVAL:Cannot specify both image id and image number",
        KittyStatus.SizeMismatch => "EINVAL:Image data size does not match declared size",
        KittyStatus.UnsupportedMedium => "EINVAL:Unsupported transmission type",
        KittyStatus.InvalidGeometry => "EINVAL:Invalid placement geometry",
        KittyStatus.SelfParent => "EINVAL:Placement cannot be its own parent",
        KittyStatus.VirtualRelative => "EINVAL:Virtual placement cannot be relative",
        KittyStatus.RectOutOfBounds => "EINVAL:Rectangle out of bounds",
        KittyStatus.ImageNotFound => "ENOENT:Image not found",
        KittyStatus.FrameNotFound => "ENOENT:Frame not found",
        KittyStatus.PlacementNotFound => "ENOENT:Placement not found",
        KittyStatus.StorageQuota => "ENOSPC:Storage quota exceeded",
        KittyStatus.TooManyPlacements => "ENOSPC:Too many placements",
        KittyStatus.InsufficientData => "ENODATA:Insufficient image data",
        KittyStatus.ImageTooLarge => "EFBIG:Image too large",
        KittyStatus.PayloadTooLarge => "EFBIG:Payload too large",
        KittyStatus.BadFile => "EBADF:Failed to read image file",
        KittyStatus.DecodeFailed => "EILSEQ:Failed to decode image data",
        KittyStatus.InflateFailed => "EILSEQ:Failed to inflate image data",
        KittyStatus.TooDeep => "ETOODEEP:Relative placement chain too deep",
        KittyStatus.Cycle => "ECYCLE:Relative placement cycle",
        KittyStatus.NoParent => "ENOPARENT:Parent placement not found",
        KittyStatus.BadPng => "EBADPNG:Invalid PNG data",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>
    /// A reply is sent only when an image id or number is known, and not suppressed by <paramref name="quiet"/>:
    /// 0 = everything, 1 = errors only, 2 (or more) = nothing.
    /// </summary>
    public static bool ShouldReply(byte quiet, KittyStatus status, uint imageId, uint imageNumber)
    {
        if (imageId == 0 && imageNumber == 0)
            return false;
        if (quiet >= 2)
            return false;
        if (quiet == 1 && status == KittyStatus.Ok)
            return false;
        return true;
    }

    /// <summary>
    /// Writes the reply into <paramref name="dst"/> and returns its length, or 0 when no reply is due.
    /// <c>i</c>, <c>I</c>, <c>p</c> are emitted only when non-zero, in that order. For errors pass the ids of
    /// the first chunk of the transfer.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="dst"/> is shorter than <see cref="MaxLength"/>.</exception>
    public static int Format(Span<char> dst, uint imageId, uint imageNumber, uint placementId, KittyStatus status, byte quiet)
    {
        if (dst.Length < MaxLength)
            throw new ArgumentException("Destination must hold at least MaxLength chars.", nameof(dst));
        if (!ShouldReply(quiet, status, imageId, imageNumber))
            return 0;

        int n = 0;
        dst[n++] = '\u001b';
        dst[n++] = '_';
        dst[n++] = 'G';
        bool first = true;
        if (imageId != 0)
        {
            n = Key(dst, n, 'i', imageId, ref first);
        }
        if (imageNumber != 0)
        {
            n = Key(dst, n, 'I', imageNumber, ref first);
        }
        if (placementId != 0)
        {
            n = Key(dst, n, 'p', placementId, ref first);
        }
        dst[n++] = ';';
        string msg = Message(status);
        msg.AsSpan().CopyTo(dst.Slice(n));
        n += msg.Length;
        dst[n++] = '\u001b';
        dst[n++] = '\\';
        return n;
    }

    private static int Key(Span<char> dst, int n, char key, uint value, ref bool first)
    {
        if (!first)
            dst[n++] = ',';
        first = false;
        dst[n++] = key;
        dst[n++] = '=';
        // uint max has 10 digits; write right-to-left into a scratch area then copy.
        Span<char> tmp = stackalloc char[10];
        int t = tmp.Length;
        do
        {
            tmp[--t] = (char)('0' + (int)(value % 10));
            value /= 10;
        } while (value != 0);
        tmp.Slice(t).CopyTo(dst.Slice(n));
        return n + (tmp.Length - t);
    }
}
