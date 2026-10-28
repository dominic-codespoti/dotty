using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Graphics;

/// <summary>One image placement resolved against the visible viewport, ready for quad generation.</summary>
public readonly struct KittyRenderPlacement
{
    /// <summary>Pixel storage; the owning <see cref="KittyRenderSnapshot"/> holds a lease for its lifetime.</summary>
    public ImagePixels Pixels { get; init; }

    /// <summary><see cref="ImagePixels.Serial"/>; GPU texture cache key.</summary>
    public long ImageSerial { get; init; }

    /// <summary>Viewport row of the top-left cell (may be negative); pane-local column.</summary>
    public int ViewRow { get; init; }
    public int Col { get; init; }

    /// <summary>Resolved cell box (post-crop).</summary>
    public int Cols { get; init; }
    public int Rows { get; init; }

    /// <summary>Sub-cell offset as a fraction of one cell.</summary>
    public float OffXCells { get; init; }
    public float OffYCells { get; init; }

    /// <summary>Source rectangle, normalised 0..1.</summary>
    public float U0 { get; init; }
    public float V0 { get; init; }
    public float U1 { get; init; }
    public float V1 { get; init; }

    /// <summary>Set only when neither columns nor rows were requested: draw at native pixels scaled by cell/basis.</summary>
    public bool NativeSize { get; init; }
    public int NativeW { get; init; }
    public int NativeH { get; init; }
    public int BasisCellW { get; init; }
    public int BasisCellH { get; init; }

    public int Z { get; init; }
    public uint ImageId { get; init; }
    public uint PlacementId { get; init; }
}

/// <summary>
/// A virtual placement (<c>U=1</c>) captured for the placeholder resolver: the prototype the Unicode-placeholder cells of
/// one image draw from. <see cref="Cols"/> × <see cref="Rows"/> is the grid the image is fitted into.
/// </summary>
public readonly struct KittyVirtualPlacement
{
    /// <summary>Pixel storage; the owning <see cref="KittyRenderSnapshot"/> holds a lease for its lifetime.</summary>
    public ImagePixels Pixels { get; init; }

    /// <summary><see cref="ImagePixels.Serial"/>; GPU texture cache key.</summary>
    public long ImageSerial { get; init; }

    /// <summary>Protocol image id the placeholders name (foreground colour plus the high byte).</summary>
    public uint ImageId { get; init; }

    /// <summary>Protocol placement id (underline colour); 0 for a placement without one.</summary>
    public uint PlacementId { get; init; }

    /// <summary>Store-internal placement id: unique, and the tie-break for equal <see cref="ImageId"/>/<see cref="PlacementId"/>.</summary>
    public long InternalId { get; init; }

    /// <summary>Grid size in cells (<c>c</c> × <c>r</c>, resolved).</summary>
    public int Cols { get; init; }
    public int Rows { get; init; }

    /// <summary>Size in pixels of the source rectangle that is fitted into the grid (aspect ratio source).</summary>
    public int SrcW { get; init; }
    public int SrcH { get; init; }

    /// <summary>Source rectangle, normalised 0..1.</summary>
    public float U0 { get; init; }
    public float V0 { get; init; }
    public float U1 { get; init; }
    public float V1 { get; init; }
}

/// <summary>
/// A relative placement whose root is a virtual placement: it can only be positioned once the placeholder cells are
/// found. <see cref="Placement"/> carries the child's own geometry; its <see cref="KittyRenderPlacement.ViewRow"/> and
/// <see cref="KittyRenderPlacement.Col"/> are the summed <c>V</c>/<c>H</c> offsets from the virtual placement's
/// top-left placeholder cell.
/// </summary>
public readonly struct KittyVirtualChild
{
    public KittyRenderPlacement Placement { get; init; }

    /// <summary><see cref="KittyVirtualPlacement.InternalId"/> of the virtual placement at the root of the chain.</summary>
    public long VirtualInternalId { get; init; }
}

/// <summary>
/// Lease holder for the placements visible in one pane snapshot. <see cref="Add"/> retains each placement's
/// <see cref="ImagePixels"/>; <see cref="Dispose"/> (idempotent) releases every lease. Not thread-safe: one
/// owner fills it, then hands it to the consumer, which disposes it.
/// </summary>
public sealed class KittyRenderSnapshot : IDisposable
{
    private KittyRenderPlacement[] _items = new KittyRenderPlacement[8];
    private int _count;
    private KittyVirtualPlacement[] _virtuals = Array.Empty<KittyVirtualPlacement>();
    private int _virtualCount;
    private KittyVirtualChild[] _virtualChildren = Array.Empty<KittyVirtualChild>();
    private int _virtualChildCount;

    internal KittyRenderSnapshot() { }

    public ReadOnlySpan<KittyRenderPlacement> Placements => new(_items, 0, _count);

    /// <summary>
    /// Virtual placements of the screen, sorted by (<see cref="KittyVirtualPlacement.ImageId"/>,
    /// <see cref="KittyVirtualPlacement.PlacementId"/>, <see cref="KittyVirtualPlacement.InternalId"/>) so the resolver
    /// binary-searches them. Every virtual placement is captured, visible or not: placeholders may name any of them.
    /// </summary>
    public ReadOnlySpan<KittyVirtualPlacement> Virtuals => new(_virtuals, 0, _virtualCount);

    /// <summary>Relative placements rooted at a virtual placement, positioned by <see cref="PlaceholderResolver"/>.</summary>
    public ReadOnlySpan<KittyVirtualChild> VirtualChildren => new(_virtualChildren, 0, _virtualChildCount);

    /// <summary>Earliest <see cref="Environment.TickCount64"/>-style deadline at which an animation frame changes; 0 = none.</summary>
    public long NextAnimationDeadlineTicks { get; internal set; }

    /// <summary>Appends a placement and takes a lease (<see cref="ImagePixels.Retain"/>) on its pixels.</summary>
    internal void Add(in KittyRenderPlacement placement)
    {
        ImagePixels pixels = placement.Pixels ?? throw new ArgumentException("Placement has no pixels.", nameof(placement));
        if (_count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);
        pixels.Retain();
        _items[_count++] = placement;
    }

    /// <summary>Appends a virtual placement and takes a lease on its pixels. Call <see cref="SortVirtuals"/> when done adding.</summary>
    internal void AddVirtual(in KittyVirtualPlacement virtualPlacement)
    {
        ImagePixels pixels = virtualPlacement.Pixels ?? throw new ArgumentException("Virtual placement has no pixels.", nameof(virtualPlacement));
        if (_virtualCount == _virtuals.Length)
            Array.Resize(ref _virtuals, Math.Max(4, _virtuals.Length * 2));
        pixels.Retain();
        _virtuals[_virtualCount++] = virtualPlacement;
    }

    /// <summary>Appends a relative placement rooted at a virtual placement and takes a lease on its pixels.</summary>
    internal void AddVirtualChild(in KittyVirtualChild child)
    {
        ImagePixels pixels = child.Placement.Pixels ?? throw new ArgumentException("Placement has no pixels.", nameof(child));
        if (_virtualChildCount == _virtualChildren.Length)
            Array.Resize(ref _virtualChildren, Math.Max(4, _virtualChildren.Length * 2));
        pixels.Retain();
        _virtualChildren[_virtualChildCount++] = child;
    }

    /// <summary>Orders <see cref="Virtuals"/> for the resolver's binary search.</summary>
    internal void SortVirtuals()
    {
        if (_virtualCount > 1)
            Array.Sort(_virtuals, 0, _virtualCount, VirtualOrder.Instance);
    }

    private sealed class VirtualOrder : IComparer<KittyVirtualPlacement>
    {
        public static readonly VirtualOrder Instance = new();

        public int Compare(KittyVirtualPlacement x, KittyVirtualPlacement y)
        {
            if (x.ImageId != y.ImageId) return x.ImageId < y.ImageId ? -1 : 1;
            if (x.PlacementId != y.PlacementId) return x.PlacementId < y.PlacementId ? -1 : 1;
            return x.InternalId.CompareTo(y.InternalId);
        }
    }

    /// <summary>Releases all leases and empties the snapshot for reuse.</summary>
    internal void Reset()
    {
        for (int i = 0; i < _count; i++)
        {
            _items[i].Pixels.Release();
            _items[i] = default;
        }
        _count = 0;
        for (int i = 0; i < _virtualCount; i++)
        {
            _virtuals[i].Pixels.Release();
            _virtuals[i] = default;
        }
        _virtualCount = 0;
        for (int i = 0; i < _virtualChildCount; i++)
        {
            _virtualChildren[i].Placement.Pixels.Release();
            _virtualChildren[i] = default;
        }
        _virtualChildCount = 0;
        NextAnimationDeadlineTicks = 0;
    }

    public void Dispose() => Reset();
}
