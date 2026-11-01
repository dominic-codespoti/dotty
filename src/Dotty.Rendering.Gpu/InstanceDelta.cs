using System;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// CPU-side diff between successive captured <see cref="CellInstance"/> frames for the GL instance stream.
/// Owns the last staged source instances, the source→output instance map (decorated cells expand to two
/// output instances) and the merged dirty source ranges, and decides between a partial and a full upload.
/// Pure: no GL state; the renderer consumes <see cref="Instances"/>, <see cref="OutputStarts"/>,
/// <see cref="OutputCounts"/> and the dirty ranges, then calls <see cref="MarkUploaded"/>.
/// </summary>
public sealed class InstanceDelta
{
    private const byte DecorationMask = CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline;

    private CellInstance[] _last = Array.Empty<CellInstance>();
    private int[] _outputStarts = Array.Empty<int>();
    private int[] _outputCounts = Array.Empty<int>();
    private int[] _ranges = Array.Empty<int>();

    /// <summary>Number of staged source instances.</summary>
    public int Count { get; private set; }

    /// <summary>True when staged instances differ from what was last uploaded.</summary>
    public bool Dirty { get; private set; } = true;

    /// <summary>True when the next upload must replace the whole buffer.</summary>
    public bool FullUpload { get; private set; } = true;

    /// <summary>Output instance count of the staged frame (valid after a partial capture or upload).</summary>
    public int StagedOutputCount { get; private set; }

    /// <summary>Number of merged dirty source ranges.</summary>
    public int RangeCount { get; private set; }

    /// <summary>Staged source instances.</summary>
    public ReadOnlySpan<CellInstance> Instances => _last.AsSpan(0, Count);

    /// <summary>Output instance index of each source instance.</summary>
    public ReadOnlySpan<int> OutputStarts => _outputStarts.AsSpan(0, Math.Min(Count, _outputStarts.Length));

    /// <summary>Output instance count (1, or 2 when decorated) of each source instance.</summary>
    public ReadOnlySpan<int> OutputCounts => _outputCounts.AsSpan(0, Math.Min(Count, _outputCounts.Length));

    /// <summary>Half-open source range <c>[Start, End)</c> of dirty range <paramref name="index"/>.</summary>
    public (int Start, int End) GetRange(int index)
    {
        if ((uint)index >= (uint)RangeCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return (_ranges[index * 2], _ranges[index * 2 + 1]);
    }

    /// <summary>Half-open output range covered by dirty range <paramref name="index"/>.</summary>
    public (int Start, int End) GetOutputRange(int index)
    {
        (int start, int end) = GetRange(index);
        return (_outputStarts[start], _outputStarts[end - 1] + _outputCounts[end - 1]);
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
    /// output encoding depends on (cell size, padding, bar rows, menu split) changed since the last upload.
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

        EnsureOutputMapCapacity(instances.Length);
        RangeCount = 0;
        int output = 0;
        int dirtyStart = -1;
        for (int i = 0; i < instances.Length; i++)
        {
            ref readonly var current = ref instances[i];
            ref readonly var previous = ref _last[i];
            bool decorated = (current.Flags & DecorationMask) != 0;
            if (decorated != ((previous.Flags & DecorationMask) != 0))
            {
                StageFull(instances);
                return;
            }

            _outputStarts[i] = output;
            int count = decorated ? 2 : 1;
            _outputCounts[i] = count;
            output += count;

            if (!Equal(current, previous))
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

        StagedOutputCount = output;
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

    /// <summary>Recomputes the output map for every staged instance and returns the total output count.</summary>
    public int RebuildOutputMap()
    {
        EnsureOutputMapCapacity(Count);
        int output = 0;
        for (int i = 0; i < Count; i++)
        {
            _outputStarts[i] = output;
            int count = (_last[i].Flags & DecorationMask) != 0 ? 2 : 1;
            _outputCounts[i] = count;
            output += count;
        }
        return output;
    }

    /// <summary>Records a completed upload of <paramref name="outputCount"/> output instances.</summary>
    public void MarkUploaded(int outputCount)
    {
        StagedOutputCount = outputCount;
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

    private void EnsureOutputMapCapacity(int required)
    {
        if (_outputStarts.Length >= required)
            return;
        int capacity = Math.Max(required, _outputStarts.Length == 0 ? 4096 : _outputStarts.Length * 2);
        Array.Resize(ref _outputStarts, capacity);
        Array.Resize(ref _outputCounts, capacity);
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
