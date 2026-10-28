using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Plan 03, WI-4b: the history capacity follows the screen size under a byte cap. The buffer keeps its
/// <see cref="ScrollbackPolicy"/> and every reflow gives the new ring <c>policy.CapacityFor(rows, columns)</c> lines:
/// wider screens hold fewer lines (the oldest are trimmed through <see cref="TerminalBuffer.HistoryInvalidated"/>) and
/// narrower ones more, but lines that were discarded never come back.
///
/// Arithmetic used below, for 1 MiB: 24 x 80 holds floor(1048576 / 960) - 24 = 1068 lines, 24 x 200 holds
/// floor(1048576 / 2400) - 24 = 412 and 48 x 80 holds 1092 - 48 = 1044.
/// </summary>
public sealed class ScrollbackCapacityReflowTests
{
    private static readonly ScrollbackPolicy OneMiB = new(5000, 1_048_576);

    private static void WriteLine(TerminalBuffer buffer, string text)
    {
        buffer.CarriageReturn();
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
    }

    // 24 x 80, `lines` numbered lines (line-0 ...), optionally a prompt mark at the start of every `promptEvery`-th line.
    // With 1500 lines and the 1 MiB policy the ring holds line-409 .. line-1476 as history (1068 lines) and
    // line-1477 .. line-1499 on screen (the cursor row below them is blank).
    private static TerminalBuffer BufferWithHistory(ScrollbackPolicy policy, int lines, int promptEvery = 0)
    {
        var buffer = new TerminalBuffer(24, 80, policy);
        for (int i = 0; i < lines; i++)
        {
            if (promptEvery > 0 && i % promptEvery == 0)
                buffer.AddPromptMark(PromptKind.Prompt);
            WriteLine(buffer, $"line-{i}");
        }

        return buffer;
    }

    private static List<HistoryDiscardReason> Record(TerminalBuffer buffer)
    {
        var events = new List<HistoryDiscardReason>();
        buffer.HistoryInvalidated += events.Add;
        return events;
    }

    private static string[] History(TerminalBuffer buffer) =>
        Enumerable.Range(0, buffer.ScrollbackCount).Select(i => buffer.GetScrollbackLine(i).ToString()).ToArray();

    [Fact]
    public void ConstructorsSizeTheRingFromThePolicy()
    {
        using (var buffer = new TerminalBuffer(24, 80, OneMiB))
            Assert.Equal(1068, buffer.ActiveBuffer.ScrollbackCapacity);
        using (var buffer = new TerminalBuffer(24, 200, OneMiB))
            Assert.Equal(412, buffer.ActiveBuffer.ScrollbackCapacity);
        using (var buffer = new TerminalBuffer(24, 200, new ScrollbackPolicy(300, 0)))
            Assert.Equal(300, buffer.ActiveBuffer.ScrollbackCapacity);
        using (var adapter = new TerminalAdapter(24, 80, OneMiB))
            Assert.Equal(1068, adapter.Buffer.ActiveBuffer.ScrollbackCapacity);
        using (var buffer = new TerminalBuffer(24, 80, 123))
            Assert.Equal(123, buffer.ActiveBuffer.ScrollbackCapacity);
    }

    [Fact]
    public void CapacityFollowsColumnsAndRowsUnderAByteCap()
    {
        using var buffer = new TerminalBuffer(24, 80, OneMiB);
        Assert.Equal(1068, buffer.ActiveBuffer.ScrollbackCapacity);

        buffer.Resize(24, 200);
        Assert.Equal(412, buffer.ActiveBuffer.ScrollbackCapacity);

        buffer.Resize(24, 80);
        Assert.Equal(1068, buffer.ActiveBuffer.ScrollbackCapacity);

        buffer.Resize(48, 80); // rows count against the byte budget too
        Assert.Equal(1044, buffer.ActiveBuffer.ScrollbackCapacity);

        buffer.Resize(24, 4000); // one screen no longer fits the budget: no history at all
        Assert.Equal(0, buffer.ActiveBuffer.ScrollbackCapacity);

        buffer.Resize(24, 80);
        Assert.Equal(1068, buffer.ActiveBuffer.ScrollbackCapacity);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void WideningDropsTheOldestHistoryThroughTheDiscardFunnel()
    {
        using var buffer = BufferWithHistory(OneMiB, 1500);
        Assert.Equal(1068, buffer.ScrollbackCount);
        Assert.Equal("line-409", buffer.GetScrollbackLine(0).ToString());
        var events = Record(buffer);

        buffer.Resize(24, 200);

        // 1092 retained rows (1068 history + 24 on screen) into 412 + 24: the newest 412 history lines stay.
        Assert.Equal(new[] { HistoryDiscardReason.CapacityShrunk }, events);
        Assert.Equal(412, buffer.ScrollbackCount);
        Assert.Equal("line-1065", buffer.GetScrollbackLine(0).ToString());
        Assert.Equal("line-1476", buffer.GetScrollbackLine(411).ToString());
        Assert.Equal("line-1477", buffer.GetRowText(0).TrimEnd());
        Assert.Equal(0L, buffer.HistoryFloorAbsoluteRow); // re-based: the retained rows are rows 0 .. 411
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void NarrowingAgainDoesNotResurrectDiscardedHistory()
    {
        using var buffer = BufferWithHistory(OneMiB, 1500);
        buffer.Resize(24, 200);
        string[] kept = History(buffer);
        Assert.Equal(412, kept.Length);
        var events = Record(buffer);

        buffer.Resize(24, 80);

        Assert.Equal(1068, buffer.ActiveBuffer.ScrollbackCapacity);
        Assert.Equal(new[] { HistoryDiscardReason.Rebased }, events);
        Assert.Equal(412, buffer.ScrollbackCount);
        Assert.Equal(kept, History(buffer));
        Assert.Equal("line-1065", buffer.GetScrollbackLine(0).ToString());
        Assert.Empty(buffer.ValidateInvariants());

        // The larger capacity is usable again: new output fills it up to 1068 lines.
        for (int i = 1500; i < 2500; i++)
            WriteLine(buffer, $"line-{i}");
        Assert.Equal(1068, buffer.ScrollbackCount);
        Assert.Equal("line-2476", buffer.GetScrollbackLine(1067).ToString());
    }

    [Fact]
    public void HistoryThatStillFitsTheNewCapacityIsKept()
    {
        using var buffer = BufferWithHistory(OneMiB, 100);
        string[] before = History(buffer);
        Assert.Equal(77, before.Length);
        var events = Record(buffer);

        buffer.Resize(24, 200);

        Assert.Equal(new[] { HistoryDiscardReason.Rebased }, events);
        Assert.Equal(before, History(buffer));
    }

    [Fact]
    public void PromptMarksOfTrimmedHistoryAreDroppedAndTheRestFollowTheWindow()
    {
        // Marks at lines 0, 100, ... 1400. Lines 0-400 are already out of the 1068-line ring; the shrink to 412 lines
        // keeps line-1065 onwards, so the marks at 1100 .. 1400 survive.
        using var buffer = BufferWithHistory(OneMiB, 1500, promptEvery: 100);

        buffer.Resize(24, 200);

        var marks = buffer.GetPromptMarks();
        Assert.Equal(4, marks.Count);
        for (int k = 0; k < 4; k++)
        {
            int line = 1100 + 100 * k;
            Assert.Equal(PromptKind.Prompt, marks[k].Kind);
            Assert.Equal(0, marks[k].AbsoluteColumn);
            // line-1477 is the first visible row; earlier lines are that many rows above it.
            Assert.Equal(line - 1477, buffer.GetPromptVisibleRow(marks[k]));
        }
    }

    [Fact]
    public void AlternateScreenFollowsTheCapacityWhileTheHiddenMainHistoryIsTrimmed()
    {
        using var buffer = BufferWithHistory(OneMiB, 1500);
        var events = Record(buffer);
        buffer.SetAlternateScreen(true);
        Assert.Equal(1068, buffer.ActiveBuffer.ScrollbackCapacity);

        buffer.Resize(24, 200);

        Assert.Equal(412, buffer.ActiveBuffer.ScrollbackCapacity); // the alternate ring
        Assert.Equal(new[] { HistoryDiscardReason.CapacityShrunk }, events);

        buffer.SetAlternateScreen(false);

        Assert.Equal(412, buffer.ActiveBuffer.ScrollbackCapacity);
        Assert.Equal(412, buffer.ScrollbackCount);
        Assert.Equal("line-1065", buffer.GetScrollbackLine(0).ToString());
        Assert.Equal("line-1476", buffer.GetScrollbackLine(411).ToString());
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void NoByteCapKeepsMaxLinesAtEverySize()
    {
        using var buffer = BufferWithHistory(new ScrollbackPolicy(300, 0), 350);
        Assert.Equal(300, buffer.ScrollbackCount);
        string[] before = History(buffer);
        var events = Record(buffer);

        buffer.Resize(24, 200);
        Assert.Equal(300, buffer.ActiveBuffer.ScrollbackCapacity);
        Assert.Equal(before, History(buffer));

        buffer.Resize(100, 500);
        Assert.Equal(300, buffer.ActiveBuffer.ScrollbackCapacity);

        Assert.All(events, reason => Assert.Equal(HistoryDiscardReason.Rebased, reason));
    }

    [Fact]
    public void ZeroLinePolicyStaysWithoutHistory()
    {
        using var buffer = new TerminalBuffer(24, 80, new ScrollbackPolicy(0, 1_048_576));
        for (int i = 0; i < 100; i++)
            WriteLine(buffer, $"line-{i}");
        Assert.Equal(0, buffer.ScrollbackCount);

        buffer.Resize(24, 40);
        Assert.Equal(0, buffer.ActiveBuffer.ScrollbackCapacity);
        Assert.Equal(0, buffer.ScrollbackCount);
    }
}
