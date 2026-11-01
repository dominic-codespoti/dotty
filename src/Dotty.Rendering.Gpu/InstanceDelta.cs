using System;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// CPU-side diff between successive captured <see cref="CellInstance"/> frames for the GL instance stream.
/// Owns the last staged instances and the merged dirty ranges, and decides between a partial and a full upload.
/// Each staged instance is exactly one GPU instance (decorations are ordinary sprite instances from the frame
/// builder), so dirty ranges index the GPU buffer directly.
/// Pure: no GL state; the renderer consumes <see cref="Instances"/> and the dirty ranges, then calls
/// <see cref="MarkUploaded"/>.
/// </summary>
public sealed class InstanceDelta
{
    private CellInstance[] _last = Array.Empty<CellInstance>();
    private int[] _ranges = Array.Empty<int>();

    /// <summary>Number of staged instances (and GPU instances).</summary>
    public int Count { get; private set; }

    /// <summary>True when staged instances differ from what was last uploaded.</summary>
    public bool Dirty { get; private set; } = true;

    /// <summary>True when the next upload must replace the whole buffer.</summary>
    public bool FullUpload { get; private set; } = true;

    /// <summary>Number of merged dirty ranges.</summary>
    public int RangeCount { get; private set; }

    /// <summary>Staged instances.</summary>
    public ReadOnlySpan<CellInstance> Instances => _last.AsSpan(0, Count);

    /// <summary>Half-open instance range <c>[Start, End)</c> of dirty range <paramref name="index"/>.</summary>
    public (int Start, int End) GetRange(int index)
    {
        if ((uint)index >= (uint)RangeCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return (_ranges[index * 2], _ranges[index * 2 + 1]);
    }

    /// <summary>Forces the next upload to replace the whole buffer.</summary>
    public void Invalidate()
    {
        Dirty = true;
        FullUpload = true;
    }

    /// <summary>Downgrades a pending partial upload to a full one (e.g. GPU buffer too small).</summary>
    public void ForceFullUpload() => FullUpload = true;

    /// <summary>
    /// Stages <paramref name="instances"/>. <paramref name="stagingCompatible"/> is false when anything the
    /// GPU encoding depends on (cell size, padding, bar rows, menu split) changed since the last upload.
    /// </summary>
    public void Capture(ReadOnlySpan<CellInstance> instances, bool stagingCompatible)
    {
        bool compatible = stagingCompatible
            && !Dirty
            && Count == instances.Length
            && _last.Length >= instances.Length;
        EnsureLastCapacity(instances.Length);
        if (!compatible)
        {
            StageFull(instances);
            return;
        }

        RangeCount = 0;
        int dirtyStart = -1;
        for (int i = 0; i < instances.Length; i++)
        {
            if (!Equal(instances[i], _last[i]))
            {
                if (dirtyStart < 0)
                    dirtyStart = i;
            }
            else if (dirtyStart >= 0)
            {
                AddRange(dirtyStart, i);
                dirtyStart = -1;
            }
        }

        if (dirtyStart >= 0)
            AddRange(dirtyStart, instances.Length);

        Count = instances.Length;
        if (RangeCount == 0)
        {
            Dirty = false;
            FullUpload = false;
            return;
        }

        for (int r = 0; r < RangeCount; r++)
        {
            int start = _ranges[r * 2];
            int end = _ranges[r * 2 + 1];
            instances.Slice(start, end - start).CopyTo(_last.AsSpan(start));
        }
        Dirty = true;
        FullUpload = false;
    }

    /// <summary>Records a completed upload of the staged instances.</summary>
    public void MarkUploaded()
    {
        Dirty = false;
        FullUpload = Count == 0;
        RangeCount = 0;
    }

    private void StageFull(ReadOnlySpan<CellInstance> instances)
    {
        instances.CopyTo(_last);
        Count = instances.Length;
        RangeCount = 0;
        Dirty = true;
        FullUpload = true;
    }

    private void EnsureLastCapacity(int required)
    {
        if (_last.Length >= required)
            return;
        int capacity = Math.Max(required, _last.Length == 0 ? 4096 : _last.Length * 2);
        _last = new CellInstance[capacity];
    }

    private void AddRange(int start, int end)
    {
        int needed = checked((RangeCount + 1) * 2);
        if (_ranges.Length < needed)
        {
            int capacity = Math.Max(needed, _ranges.Length == 0 ? 16 : _ranges.Length * 2);
            Array.Resize(ref _ranges, capacity);
        }
        _ranges[RangeCount * 2] = start;
        _ranges[RangeCount * 2 + 1] = end;
        RangeCount++;
    }

    /// <summary>Field-wise equality of two instances (padding-independent).</summary>
    public static bool Equal(in CellInstance left, in CellInstance right) =>
        left.Col == right.Col
        && left.Row == right.Row
        && left.GlyphX == right.GlyphX
        && left.GlyphY == right.GlyphY
        && left.GlyphW == right.GlyphW
        && left.GlyphH == right.GlyphH
        && left.OffX == right.OffX
        && left.OffY == right.OffY
        && left.FgR == right.FgR
        && left.FgG == right.FgG
        && left.FgB == right.FgB
        && left.FgA == right.FgA
        && left.Flags == right.Flags
        && left.BgR == right.BgR
        && left.BgG == right.BgG
        && left.BgB == right.BgB
        && left.BgA == right.BgA;
}
