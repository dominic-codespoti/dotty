namespace Dotty.Terminal.Graphics;

/// <summary>What a delete command (<c>a=d</c>) selects (the lower-case meaning of its <c>d</c> key).</summary>
internal enum KittyDeleteKind : byte
{
    /// <summary><c>a</c>: placements that intersect the visible screen.</summary>
    Visible,
    /// <summary><c>i</c>: the image with id <c>i</c> (optionally one placement <c>p</c>).</summary>
    Image,
    /// <summary><c>n</c>: the newest image with number <c>I</c> (optionally one placement <c>p</c>).</summary>
    Number,
    /// <summary><c>c</c>: placements that cover the cursor cell.</summary>
    Cursor,
    /// <summary><c>p</c>: placements that cover cell (<c>x</c>, <c>y</c>), 1-based.</summary>
    Cell,
    /// <summary><c>q</c>: like <see cref="Cell"/> and with z-index <c>z</c>.</summary>
    CellZ,
    /// <summary><c>x</c>: placements that cover column <c>x</c> (1-based).</summary>
    Column,
    /// <summary><c>y</c>: placements that cover visible row <c>y</c> (1-based).</summary>
    Row,
    /// <summary><c>z</c>: placements with z-index <c>z</c>.</summary>
    ZIndex,
    /// <summary><c>r</c>: images whose id lies in <c>[x, y]</c>.</summary>
    IdRange,
    /// <summary><c>f</c>: animation frames; handled by the animation stage (a no-op until then).</summary>
    Frames,
    /// <summary>Any other <c>d</c> value: ignored.</summary>
    Unknown,
}

/// <summary>A parsed <c>d</c> key: the selection and whether the image data of fully unreferenced matches is freed (upper case).</summary>
internal readonly record struct KittyDeleteSpec(KittyDeleteKind Kind, bool FreeImages)
{
    /// <summary>Maps the <c>d</c> letter. Upper case frees image data, lower case deletes placements only.</summary>
    public static KittyDeleteSpec Parse(char d)
    {
        bool upper = d >= 'A' && d <= 'Z';
        char lower = upper ? (char)(d | 0x20) : d;
        var kind = lower switch
        {
            'a' => KittyDeleteKind.Visible,
            'i' => KittyDeleteKind.Image,
            'n' => KittyDeleteKind.Number,
            'c' => KittyDeleteKind.Cursor,
            'p' => KittyDeleteKind.Cell,
            'q' => KittyDeleteKind.CellZ,
            'x' => KittyDeleteKind.Column,
            'y' => KittyDeleteKind.Row,
            'z' => KittyDeleteKind.ZIndex,
            'r' => KittyDeleteKind.IdRange,
            'f' => KittyDeleteKind.Frames,
            _ => KittyDeleteKind.Unknown,
        };
        return new KittyDeleteSpec(kind, upper && kind != KittyDeleteKind.Unknown);
    }
}
