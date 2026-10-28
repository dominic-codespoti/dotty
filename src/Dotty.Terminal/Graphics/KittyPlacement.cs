using System.Collections.Generic;

namespace Dotty.Terminal.Graphics;

/// <summary>How a placement is positioned.</summary>
internal enum PlacementKind : byte
{
    /// <summary>Anchored at a screen cell (absolute row + column).</summary>
    Normal,

    /// <summary>Unicode-placeholder placement (<c>U=1</c>): a <c>c</c>×<c>r</c> grid without a screen anchor.</summary>
    Virtual,

    /// <summary>Positioned relative to a parent placement (<c>P/Q/H/V</c>); the anchor fields are unused.</summary>
    Relative,
}

/// <summary>
/// One display of an image. Owned by <see cref="KittyImageStore"/>, which is the only writer; renderers read it
/// through <see cref="KittyRenderSnapshot"/> copies.
/// </summary>
/// <remarks>
/// <see cref="AnchorRow"/> is an absolute row in the owning screen's <see cref="KittyImageStore.ViewTop"/>
/// coordinate system: the visible row is <c>AnchorRow - ViewTop</c> (negative = in the history). Virtual
/// placements carry no anchor (it is 0 and ignored).
/// </remarks>
internal sealed class KittyPlacement
{
    internal KittyPlacement(long internalId, uint clientId, KittyImage image, PlacementKind kind, in ResolvedGeometry geometry,
        uint requestedCols, uint requestedRows, int z)
    {
        InternalId = internalId;
        ClientId = clientId;
        Image = image;
        Kind = kind;
        Geometry = geometry;
        RequestedCols = requestedCols;
        RequestedRows = requestedRows;
        Z = z;
    }

    /// <summary>Monotonic per store, never reused; the "age" used when the placement cap forces a deletion.</summary>
    public long InternalId { get; }

    /// <summary>Protocol placement id (<c>p</c>); 0 when the placement cannot be addressed (anonymous image, or no <c>p</c>).</summary>
    public uint ClientId { get; }

    public KittyImage Image { get; }

    public PlacementKind Kind { get; }

    public long AnchorRow { get; internal set; }

    /// <summary>0-based anchor column.</summary>
    public int AnchorCol { get; internal set; }

    /// <summary>
    /// Current resolved geometry: clamped source rectangle, in-cell offsets, cell box and the cell size they were
    /// resolved at. Replaced (never mutated) when the placement is cropped by a margin scroll or re-resolved.
    /// </summary>
    public ResolvedGeometry Geometry { get; internal set; }

    /// <summary>The <c>c</c> / <c>r</c> the client asked for (0 = auto).</summary>
    public uint RequestedCols { get; }
    public uint RequestedRows { get; }

    public int Z { get; }

    public int SrcX => Geometry.SrcX;
    public int SrcY => Geometry.SrcY;
    public int SrcW => Geometry.SrcW;
    public int SrcH => Geometry.SrcH;
    public int CellXOff => Geometry.XOff;
    public int CellYOff => Geometry.YOff;
    public int EffCols => Geometry.EffCols;
    public int EffRows => Geometry.EffRows;
    public int BasisCellW => Geometry.BasisCellW;
    public int BasisCellH => Geometry.BasisCellH;

    /// <summary>First row past the placement: <c>AnchorRow + EffRows</c>.</summary>
    public long EndRow => AnchorRow + Geometry.EffRows;

    /// <summary>Scratch mark of <see cref="KittyImageStore.RebaseAnchors"/>: the placement was mapped to a new position.</summary>
    internal bool Rebased { get; set; }

    /// <summary>
    /// Relative placements (<c>P/Q/H/V</c>): the placement this one is positioned against, or null. A relative
    /// placement (<see cref="PlacementKind.Relative"/>) stores no absolute anchor: its position is derived from the
    /// parent chain at capture time (<see cref="KittyImageStore.MaxRelativeDepth"/> links at most), so it follows
    /// the parent through scrolls, reflow and replacement without any notification touching it.
    /// </summary>
    internal KittyPlacement? Parent { get; set; }

    /// <summary>Offset from the parent's top-left cell: columns (<c>H</c>) and rows (<c>V</c>), either sign.</summary>
    internal int ParentDx { get; set; }
    internal int ParentDy { get; set; }

    /// <summary>The live placements whose <see cref="Parent"/> is this one (null when none); deleted with it.</summary>
    internal List<KittyPlacement>? Children { get; set; }
}
