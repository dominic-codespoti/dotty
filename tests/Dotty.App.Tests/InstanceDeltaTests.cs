using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class InstanceDeltaTests
{
    private static CellInstance Cell(int col, byte flags = 0, byte fg = 0x10) =>
        new() { Col = (ushort)col, Row = 0, FgR = fg, FgA = 0xFF, Flags = flags, BgA = 0xFF };

    private static CellInstance[] Row(int count, Func<int, CellInstance>? make = null)
    {
        var cells = new CellInstance[count];
        for (int i = 0; i < count; i++)
            cells[i] = make?.Invoke(i) ?? Cell(i);
        return cells;
    }

    private static List<(int, int)> Ranges(InstanceDelta delta)
    {
        var list = new List<(int, int)>();
        for (int i = 0; i < delta.RangeCount; i++)
            list.Add(delta.GetRange(i));
        return list;
    }

    [Fact]
    public void FirstCaptureIsFullUpload()
    {
        var delta = new InstanceDelta();
        delta.Capture(Row(4), stagingCompatible: true);
        Assert.True(delta.Dirty);
        Assert.True(delta.FullUpload);
        Assert.Equal(4, delta.Count);
        Assert.Equal(0, delta.RangeCount);
    }

    [Fact]
    public void IdenticalFrameAfterUploadIsClean()
    {
        var delta = new InstanceDelta();
        delta.Capture(Row(4), true);
        delta.MarkUploaded(delta.RebuildOutputMap());
        delta.Capture(Row(4), true);
        Assert.False(delta.Dirty);
        Assert.False(delta.FullUpload);
        Assert.Equal(4, delta.StagedOutputCount);
    }

    public static TheoryData<string, int[], (int, int)[]> ChangedCells => new()
    {
        { "single middle", new[] { 3 }, new[] { (3, 4) } },
        { "first", new[] { 0 }, new[] { (0, 1) } },
        { "last", new[] { 7 }, new[] { (7, 8) } },
        { "adjacent merge", new[] { 2, 3, 4 }, new[] { (2, 5) } },
        { "two runs", new[] { 1, 2, 5 }, new[] { (1, 3), (5, 6) } },
        { "all", new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, new[] { (0, 8) } },
    };

    [Theory]
    [MemberData(nameof(ChangedCells))]
    public void ChangedCellsMergeIntoHalfOpenRanges(string name, int[] changed, (int, int)[] expected)
    {
        _ = name;
        var delta = new InstanceDelta();
        delta.Capture(Row(8), true);
        delta.MarkUploaded(delta.RebuildOutputMap());
        var next = Row(8);
        foreach (int i in changed)
            next[i].FgR = 0x99;
        delta.Capture(next, true);
        Assert.True(delta.Dirty);
        Assert.False(delta.FullUpload);
        Assert.Equal(expected, Ranges(delta));
        Assert.Equal(0x99, delta.Instances[changed[0]].FgR);
    }

    [Fact]
    public void DecoratedCellsOccupyTwoOutputSlots()
    {
        var delta = new InstanceDelta();
        var cells = Row(4, i => Cell(i, i == 1 ? CellFlags.Underline : (byte)0));
        delta.Capture(cells, true);
        Assert.Equal(5, delta.RebuildOutputMap());
        Assert.Equal(new[] { 0, 1, 3, 4 }, delta.OutputStarts.ToArray());
        Assert.Equal(new[] { 1, 2, 1, 1 }, delta.OutputCounts.ToArray());
        delta.MarkUploaded(5);

        var next = Row(4, i => Cell(i, i == 1 ? CellFlags.Underline : (byte)0, i >= 1 ? (byte)0x55 : (byte)0x10));
        next[3].FgR = 0x10;
        delta.Capture(next, true);
        Assert.Equal(new[] { (1, 3) }, Ranges(delta));
        Assert.Equal((1, 4), delta.GetOutputRange(0));
    }

    public static TheoryData<string, Action<CellInstance[]>, int, bool> FullUploadTriggers => new()
    {
        { "decoration added", c => c[2].Flags = CellFlags.Strikethrough, 4, true },
        { "count changed", _ => { }, 5, true },
        { "staging incompatible", _ => { }, 4, false },
    };

    [Theory]
    [MemberData(nameof(FullUploadTriggers))]
    public void LayoutChangesForceFullUpload(string name, Action<CellInstance[]> mutate, int count, bool compatible)
    {
        _ = name;
        var delta = new InstanceDelta();
        delta.Capture(Row(4), true);
        delta.MarkUploaded(delta.RebuildOutputMap());
        var next = Row(count);
        mutate(next);
        delta.Capture(next, compatible);
        Assert.True(delta.Dirty);
        Assert.True(delta.FullUpload);
        Assert.Equal(0, delta.RangeCount);
        Assert.Equal(count, delta.Count);
    }

    [Fact]
    public void EmptyUploadKeepsNextUploadFull()
    {
        var delta = new InstanceDelta();
        delta.Capture(ReadOnlySpan<CellInstance>.Empty, true);
        delta.MarkUploaded(0);
        Assert.False(delta.Dirty);
        Assert.True(delta.FullUpload);
    }

    [Fact]
    public void GetRangeRejectsOutOfRangeIndex()
    {
        var delta = new InstanceDelta();
        Assert.Throws<ArgumentOutOfRangeException>(() => delta.GetRange(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    public void MatchesLegacyAlgorithmOnRandomFrameSequences(int seed)
    {
        var random = new Random(seed);
        var delta = new InstanceDelta();
        var oracle = new LegacyCapture();
        CellInstance[] frame = Row(random.Next(0, 40));
        for (int step = 0; step < 400; step++)
        {
            frame = Mutate(random, frame);
            bool compatible = random.Next(10) != 0;
            bool captured = random.Next(8) != 0;
            if (captured)
            {
                delta.Capture(frame, compatible);
                oracle.Capture(frame, compatible);
            }
            if (!compatible)
            {
                delta.Invalidate();
                oracle.Dirty = oracle.Full = true;
            }

            Assert.Equal(oracle.Count, delta.Count);
            Assert.Equal(oracle.Dirty, delta.Dirty);
            Assert.Equal(oracle.Full, delta.FullUpload);
            Assert.Equal(oracle.Ranges, Ranges(delta));
            Assert.True(oracle.Last.AsSpan(0, oracle.Count).SequenceEqual(delta.Instances));
            if (!delta.Dirty)
                continue;

            if (!delta.FullUpload)
            {
                Assert.Equal(oracle.StagedOutput, delta.StagedOutputCount);
                Assert.True(oracle.Starts.AsSpan(0, oracle.Count).SequenceEqual(delta.OutputStarts));
                Assert.True(oracle.Counts.AsSpan(0, oracle.Count).SequenceEqual(delta.OutputCounts));
            }

            // Upload: the oracle mirrors the renderer's pre-extraction UploadAndDraw bookkeeping.
            int output = delta.FullUpload ? delta.RebuildOutputMap() : delta.StagedOutputCount;
            int oracleOutput = oracle.Upload();
            Assert.Equal(oracleOutput, output);
            delta.MarkUploaded(output);
            Assert.Equal(oracle.Full, delta.FullUpload);
            Assert.Equal(oracle.Dirty, delta.Dirty);
        }
    }

    private static CellInstance[] Mutate(Random random, CellInstance[] frame)
    {
        int roll = random.Next(20);
        if (roll == 0)
            return Row(random.Next(0, 40), i => Cell(i, (byte)(random.Next(4) == 0 ? CellFlags.Underline : 0)));
        var next = (CellInstance[])frame.Clone();
        int edits = roll < 5 ? 0 : random.Next(1, 6);
        for (int e = 0; e < edits && next.Length > 0; e++)
        {
            int i = random.Next(next.Length);
            switch (random.Next(4))
            {
                case 0: next[i].FgR = (byte)random.Next(256); break;
                case 1: next[i].GlyphX = (short)random.Next(100); break;
                case 2: next[i].BgB = (byte)random.Next(256); break;
                default: next[i].Flags ^= CellFlags.Overline; break;
            }
        }
        return next;
    }

    /// <summary>Verbatim port of the pre-W4-C21 SilkTerminalRenderer.CaptureInstances state machine.</summary>
    private sealed class LegacyCapture
    {
        private const byte Mask = CellFlags.Underline | CellFlags.Strikethrough | CellFlags.Overline;
        public CellInstance[] Last = Array.Empty<CellInstance>();
        public int[] Starts = Array.Empty<int>();
        public int[] Counts = Array.Empty<int>();
        public List<(int, int)> Ranges = new();
        public int Count;
        public int StagedOutput;
        public bool Dirty = true;
        public bool Full = true;

        public void Capture(CellInstance[] instances, bool stagingCompatible)
        {
            bool compatible = !Dirty && Count == instances.Length && Last.Length >= instances.Length && stagingCompatible;
            if (Last.Length < instances.Length)
                Last = new CellInstance[Math.Max(instances.Length, Last.Length == 0 ? 4096 : Last.Length * 2)];
            if (!compatible)
            {
                StageFull(instances);
                return;
            }
            if (Starts.Length < instances.Length)
            {
                Array.Resize(ref Starts, instances.Length);
                Array.Resize(ref Counts, instances.Length);
            }
            Ranges.Clear();
            int output = 0;
            int dirtyStart = -1;
            for (int i = 0; i < instances.Length; i++)
            {
                bool has = (instances[i].Flags & Mask) != 0;
                bool had = (Last[i].Flags & Mask) != 0;
                if (has != had)
                {
                    StageFull(instances);
                    return;
                }
                Starts[i] = output;
                Counts[i] = has ? 2 : 1;
                output += Counts[i];
                if (!InstanceDelta.Equal(instances[i], Last[i]))
                {
                    if (dirtyStart < 0)
                        dirtyStart = i;
                }
                else if (dirtyStart >= 0)
                {
                    Ranges.Add((dirtyStart, i));
                    dirtyStart = -1;
                }
            }
            if (dirtyStart >= 0)
                Ranges.Add((dirtyStart, instances.Length));
            StagedOutput = output;
            Count = instances.Length;
            if (Ranges.Count == 0)
            {
                Dirty = false;
                Full = false;
                return;
            }
            foreach ((int start, int end) in Ranges)
                Array.Copy(instances, start, Last, start, end - start);
            Dirty = true;
            Full = false;
        }

        public int Upload()
        {
            int output = StagedOutput;
            if (Full)
            {
                output = 0;
                for (int i = 0; i < Count; i++)
                    output += (Last[i].Flags & Mask) != 0 ? 2 : 1;
            }
            StagedOutput = output;
            Dirty = false;
            Full = Count == 0;
            Ranges.Clear();
            return output;
        }

        private void StageFull(CellInstance[] instances)
        {
            Array.Copy(instances, Last, instances.Length);
            Count = instances.Length;
            Ranges.Clear();
            Dirty = true;
            Full = true;
        }
    }
}
