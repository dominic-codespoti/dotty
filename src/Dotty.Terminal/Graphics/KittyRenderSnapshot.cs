using System;

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
/// Lease holder for the placements visible in one pane snapshot. <see cref="Add"/> retains each placement's
/// <see cref="ImagePixels"/>; <see cref="Dispose"/> (idempotent) releases every lease. Not thread-safe: one
/// owner fills it, then hands it to the consumer, which disposes it.
/// </summary>
public sealed class KittyRenderSnapshot : IDisposable
{
    private KittyRenderPlacement[] _items = new KittyRenderPlacement[8];
    private int _count;

    internal KittyRenderSnapshot() { }

    public ReadOnlySpan<KittyRenderPlacement> Placements => new(_items, 0, _count);

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

    /// <summary>Releases all leases and empties the snapshot for reuse.</summary>
    internal void Reset()
    {
        for (int i = 0; i < _count; i++)
        {
            _items[i].Pixels.Release();
            _items[i] = default;
        }
        _count = 0;
        NextAnimationDeadlineTicks = 0;
    }

    public void Dispose() => Reset();
}
