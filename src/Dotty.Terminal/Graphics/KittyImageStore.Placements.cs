using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;

namespace Dotty.Terminal.Graphics;

// Placement half of the store: the placement index, the per-screen cap and the bookkeeping that ties placements
// to their image (eviction order, unreferenced-image sweep) and to GraphicsMemoryMetrics.
internal sealed partial class KittyImageStore
{
    private readonly Dictionary<long, KittyPlacement> _placements = new();
    private readonly Dictionary<(long Image, uint Placement), KittyPlacement> _placementsById = new();
    private readonly List<KittyImage> _sweepCandidates = new();
    private readonly List<KittyPlacement> _scratch = new();
    private long _nextPlacementId = 1;
    private long _viewTop;
    private long _minEndRow = long.MaxValue;
    private int _maxPlacements = DottyDefaults.KittyMaxPlacementsPerScreen;
    private int _reportedPlacements;

    /// <summary>
    /// Absolute index of the screen's logical row 0: it advances only when the screen's ring head rotates
    /// (<see cref="OnRingRotatedUp"/>), so a placement's visible row is <c>AnchorRow - ViewTop</c> and a
    /// full-screen scroll costs O(1).
    /// </summary>
    public long ViewTop => _viewTop;

    /// <summary>Live placements of this screen (virtual ones included).</summary>
    public int PlacementCount => _placements.Count;

    public bool HasPlacements => _placements.Count != 0;

    /// <summary>The live placements in no particular order. Do not mutate the store while enumerating.</summary>
    public IEnumerable<KittyPlacement> Placements => _placements.Values;

    /// <summary>
    /// Cap on <see cref="PlacementCount"/>. A new placement over the cap first deletes the oldest placement that
    /// lies wholly in the history, and fails with <see cref="KittyStatus.TooManyPlacements"/> when there is none.
    /// Lowering the cap never deletes anything by itself.
    /// </summary>
    public int MaxPlacements
    {
        get => _maxPlacements;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _maxPlacements = value;
        }
    }

    /// <summary>The placement of <paramref name="image"/> with protocol id <paramref name="placementId"/>; null for id 0.</summary>
    public KittyPlacement? FindPlacement(KittyImage image, uint placementId) =>
        placementId != 0 && _placementsById.TryGetValue((image.InternalId, placementId), out var p) ? p : null;

    /// <summary>
    /// Creates a placement of <paramref name="image"/> from the placement keys of <paramref name="cmd"/>
    /// (<c>p x y w h X Y c r z U</c>) at the given anchor. The placement id is honoured only when the image has a
    /// client id; placing with an existing <c>(image, id)</c> replaces that placement. <c>U=1</c> creates a
    /// virtual placement (the anchor is ignored). Never moves the cursor and never touches the image pixels.
    /// </summary>
    /// <returns>
    /// <see cref="KittyStatus.Ok"/>; <see cref="KittyStatus.ImageNotFound"/> when the image is not in this store;
    /// <see cref="KittyStatus.RectOutOfBounds"/> when the source rectangle starts outside the image;
    /// <see cref="KittyStatus.TooManyPlacements"/> at the cap with nothing deletable.
    /// </returns>
    public KittyStatus TryPlace(KittyImage image, in KittyGraphicsCommand cmd, long anchorRow, int anchorCol,
        int cellPixelWidth, int cellPixelHeight, out KittyPlacement? placement)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        placement = null;
        if (!_byInternalId.TryGetValue(image.InternalId, out var live) || !ReferenceEquals(live, image))
            return KittyStatus.ImageNotFound;

        var root = image.Root;
        var geometry = PlacementGeometry.Resolve(
            (uint)root.Width, (uint)root.Height,
            cmd.SrcX, cmd.SrcY, cmd.SrcW, cmd.SrcH,
            cmd.CellXOff, cmd.CellYOff,
            cmd.Cols, cmd.Rows,
            cellPixelWidth, cellPixelHeight);
        if (geometry.IsEmpty)
            return KittyStatus.RectOutOfBounds;

        uint placementId = image.ClientId != 0 ? cmd.PlacementId : 0;
        KittyPlacement? replaced = FindPlacement(image, placementId);

        // Over the cap: history placements go first, oldest first; the placement being replaced does not count.
        while (_placements.Count - (replaced != null ? 1 : 0) >= _maxPlacements)
        {
            var victim = FindOldestHistoryPlacement(replaced);
            if (victim == null)
                return KittyStatus.TooManyPlacements;
            RemovePlacement(victim);
        }
        if (replaced != null)
            RemovePlacement(replaced);

        var kind = cmd.Unicode == 1 ? PlacementKind.Virtual : PlacementKind.Normal;
        var created = new KittyPlacement(_nextPlacementId++, placementId, image, kind, in geometry, cmd.Cols, cmd.Rows, cmd.Z);
        if (kind == PlacementKind.Normal)
        {
            created.AnchorRow = anchorRow;
            created.AnchorCol = anchorCol;
        }
        AddPlacement(created);
        SweepUnreferenced();
        placement = created;
        return KittyStatus.Ok;
    }

    private void AddPlacement(KittyPlacement p)
    {
        _placements.Add(p.InternalId, p);
        if (p.ClientId != 0)
            _placementsById[(p.Image.InternalId, p.ClientId)] = p;
        (p.Image.PlacementList ??= new List<KittyPlacement>(1)).Add(p);
        if (p.Kind != PlacementKind.Virtual)
            NoteEndRow(p.EndRow);
        ReportPlacements(1);
    }

    private void NoteEndRow(long endRow)
    {
        if (endRow < _minEndRow)
            _minEndRow = endRow;
    }

    private void ReportPlacements(int delta)
    {
        _reportedPlacements += delta;
        GraphicsMemoryMetrics.ChangeLivePlacements(delta);
    }

    private KittyPlacement? FindOldestHistoryPlacement(KittyPlacement? exclude)
    {
        KittyPlacement? best = null;
        foreach (var p in _placements.Values)
        {
            if (p.Kind == PlacementKind.Virtual || ReferenceEquals(p, exclude) || p.EndRow > _viewTop)
                continue;
            if (best == null || p.InternalId < best.InternalId)
                best = p;
        }
        return best;
    }

    /// <summary>Removes one placement from every index. The image stays (see <see cref="SweepUnreferenced"/>).</summary>
    private void RemovePlacement(KittyPlacement p)
    {
        if (!_placements.Remove(p.InternalId))
            return;
        if (p.ClientId != 0
            && _placementsById.TryGetValue((p.Image.InternalId, p.ClientId), out var indexed)
            && ReferenceEquals(indexed, p))
            _placementsById.Remove((p.Image.InternalId, p.ClientId));
        var list = p.Image.PlacementList;
        if (list != null)
        {
            list.Remove(p);
            if (list.Count == 0 && p.Image.ClientId == 0)
                _sweepCandidates.Add(p.Image);
        }
        ReportPlacements(-1);
    }

    /// <summary>Removes all placements of an image that is being deleted.</summary>
    private void RemovePlacementsOf(KittyImage image)
    {
        var list = image.PlacementList;
        if (list == null || list.Count == 0)
            return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var p = list[i];
            _placements.Remove(p.InternalId);
            if (p.ClientId != 0)
                _placementsById.Remove((image.InternalId, p.ClientId));
        }
        ReportPlacements(-list.Count);
        list.Clear();
    }

    /// <summary>
    /// Deletes anonymous images (no client id) that lost their last placement: nothing can address them any more.
    /// Images with an id stay until quota eviction or an explicit delete.
    /// </summary>
    private void SweepUnreferenced()
    {
        if (_sweepCandidates.Count == 0)
            return;
        for (int i = 0; i < _sweepCandidates.Count; i++)
        {
            var image = _sweepCandidates[i];
            if (!image.IsDeleted && image.ClientId == 0 && image.PlacementCount == 0)
                Delete(image);
        }
        _sweepCandidates.Clear();
    }

    /// <summary>Removes every placement in <see cref="_scratch"/> and reports how many there were. Clears the scratch list.</summary>
    private int RemoveScratchPlacements()
    {
        int count = _scratch.Count;
        for (int i = 0; i < count; i++)
            RemovePlacement(_scratch[i]);
        _scratch.Clear();
        SweepUnreferenced();
        return count;
    }

    private void ValidatePlacements()
    {
        int total = 0;
        foreach (var image in _byInternalId.Values)
        {
            var list = image.PlacementList;
            if (list == null)
                continue;
            total += list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                Require(ReferenceEquals(p.Image, image), "placement listed under a foreign image");
                Require(_placements.TryGetValue(p.InternalId, out var indexed) && ReferenceEquals(indexed, p),
                    "image lists a placement the store does not index");
            }
        }
        Require(total == _placements.Count, "per-image placement counts do not add up to the store's placements");
        Require(_reportedPlacements == _placements.Count, "placement metric is out of step with the live placements");
        Require(_anchors.Count == 0, "captured reflow anchors were never rebased or abandoned");

        int withId = 0;
        foreach (var (internalId, p) in _placements)
        {
            Require(p.InternalId == internalId, "placement stored under a foreign internal id");
            Require(internalId < _nextPlacementId, "placement id not below the next id");
            Require(!p.Image.IsDeleted && _byInternalId.TryGetValue(p.Image.InternalId, out var liveImage) && ReferenceEquals(liveImage, p.Image),
                "placement of an image that is not live");
            Require(p.Image.PlacementList != null && p.Image.PlacementList.Contains(p), "placement missing from its image's list");
            Require(p.EffCols > 0 && p.EffRows > 0 && p.SrcW > 0 && p.SrcH > 0, "placement with an empty geometry");
            if (p.ClientId != 0)
            {
                withId++;
                Require(p.Image.ClientId != 0, "placement id on an image without a client id");
                Require(_placementsById.TryGetValue((p.Image.InternalId, p.ClientId), out var byId) && ReferenceEquals(byId, p),
                    "placement id index misses a live placement");
            }
            if (p.Kind != PlacementKind.Virtual)
            {
                Require(p.EndRow >= _minEndRow, "cached minimum end row is above a placement's end row");
                Require(p.AnchorCol >= 0, "placement with a negative anchor column");
            }
            if (p.Parent != null)
            {
                var step = p.Parent;
                for (int depth = 0; step != null; depth++)
                {
                    Require(!ReferenceEquals(step, p) && depth <= _placements.Count, "placement parent chain is cyclic");
                    Require(_placements.TryGetValue(step.InternalId, out var parent) && ReferenceEquals(parent, step),
                        "placement parent is not a live placement");
                    step = step.Parent;
                }
            }
        }
        Require(withId == _placementsById.Count, "placement id index has stale entries");
    }
}
