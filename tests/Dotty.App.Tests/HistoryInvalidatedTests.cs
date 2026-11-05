using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// The single funnel for "history rows were discarded": <see cref="TerminalBuffer.HistoryFloorAbsoluteRow"/> is
/// polled for the implicit ring overwrite, <see cref="TerminalBuffer.HistoryInvalidated"/> fires exactly once for
/// every explicit operation that discards or renumbers history.
/// </summary>
public sealed class HistoryInvalidatedTests
{
    private static List<HistoryDiscardReason> Record(TerminalBuffer buffer)
    {
        var events = new List<HistoryDiscardReason>();
        buffer.HistoryInvalidated += events.Add;
        return events;
    }

    private static void WriteLine(TerminalBuffer buffer, string text)
    {
        buffer.CarriageReturn();
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
    }

    private static TerminalBuffer BufferWithHistory(int lines = 30)
    {
        var buffer = new TerminalBuffer(rows: 5, columns: 20, scrollbackCapacity: 50);
        for (int i = 0; i < lines; i++)
            WriteLine(buffer, $"line-{i}");
        return buffer;
    }

    [Fact]
    public void ClearScrollbackRaisesClearedOnce()
    {
        using var buffer = BufferWithHistory();
        var events = Record(buffer);

        buffer.ClearScrollback();

        Assert.Equal(new[] { HistoryDiscardReason.Cleared }, events);
        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal(0, buffer.HistoryFloorAbsoluteRow);
    }

    [Fact]
    public void OnlyEraseDisplayModeTwoRaisesCleared()
    {
        using var buffer = BufferWithHistory();
        var events = Record(buffer);

        buffer.EraseDisplay(0);
        buffer.EraseDisplay(1);
        Assert.Empty(events);

        buffer.EraseDisplay(2);
        Assert.Equal(new[] { HistoryDiscardReason.Cleared }, events);
    }

    [Fact]
    public void FullResetRaisesResetOnceEvenFromTheAlternateScreen()
    {
        using var buffer = BufferWithHistory();
        buffer.SetAlternateScreen(true);
        var events = Record(buffer);

        buffer.FullReset();

        Assert.Equal(new[] { HistoryDiscardReason.Reset }, events);
        Assert.False(buffer.IsAlternateScreenActive);
        Assert.Equal(0, buffer.ScrollbackCount);
    }

    [Fact]
    public void ClearScreenRaisesResetOnce()
    {
        using var buffer = BufferWithHistory();
        var events = Record(buffer);

        buffer.ClearScreen();

        Assert.Equal(new[] { HistoryDiscardReason.Reset }, events);
    }

    [Fact]
    public void AlternateScreenTrafficLeavesTheMainHistoryAlone()
    {
        using var buffer = BufferWithHistory();
        int before = buffer.ScrollbackCount;
        var events = Record(buffer);

        buffer.SetAlternateScreen(true);
        for (int i = 0; i < 20; i++)
            WriteLine(buffer, $"alt-{i}");
        buffer.EraseDisplay(2);
        buffer.ClearScrollback();
        buffer.SetAlternateScreen(false);

        Assert.Empty(events);
        Assert.Equal(before, buffer.ScrollbackCount);
        Assert.Equal("line-0", buffer.GetScrollbackLine(0).ToString());
    }

    [Fact]
    public void ResizeRaisesRebasedWhenNoRowIsDropped()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 8, scrollbackCapacity: 10);
        var events = Record(buffer);

        buffer.Resize(4, 16);

        Assert.Equal(new[] { HistoryDiscardReason.Rebased }, events);
    }

    [Fact]
    public void ResizeRaisesCapacityShrunkWhenReflowDropsTheOldestRows()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 8, scrollbackCapacity: 10);
        for (int i = 0; i < 13; i++)
            WriteLine(buffer, "ABCDEFG"); // 7 of 8 columns: no wrap now, two rows at 4 columns
        Assert.Equal(10, buffer.ScrollbackCount);
        var events = Record(buffer);

        buffer.Resize(4, 4);

        Assert.Equal(new[] { HistoryDiscardReason.CapacityShrunk }, events);
        Assert.Equal(10, buffer.ScrollbackCount);
    }

    [Fact]
    public void NoOpResizeRaisesNothing()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 8, scrollbackCapacity: 10);
        var events = Record(buffer);

        buffer.Resize(4, 8);

        Assert.Empty(events);
    }

    [Fact]
    public void FloorFollowsTheRingOverwriteWithoutAnyEvent()
    {
        using var buffer = new TerminalBuffer(rows: 3, columns: 10, scrollbackCapacity: 4);
        var events = Record(buffer);

        for (int i = 0; i < 2; i++)
            buffer.ScrollUpLines(1);
        Assert.Equal(0, buffer.HistoryFloorAbsoluteRow);

        for (int i = 0; i < 8; i++)
            buffer.ScrollUpLines(1);

        Assert.Equal(4, buffer.ScrollbackCount);
        Assert.Equal(6, buffer.HistoryFloorAbsoluteRow); // 10 scrolled, 4 retained
        Assert.Empty(events);
    }

    [Fact]
    public void FloorWithoutScrollbackCapacityEqualsTheScrolledLineCount()
    {
        using var buffer = new TerminalBuffer(rows: 3, columns: 10, scrollbackCapacity: 0);

        for (int i = 0; i < 5; i++)
            buffer.ScrollUpLines(1);

        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal(5, buffer.HistoryFloorAbsoluteRow);
    }

    [Fact]
    public void ReverseScrollLowersTheFloor()
    {
        using var buffer = new TerminalBuffer(rows: 3, columns: 10, scrollbackCapacity: 4);
        for (int i = 0; i < 10; i++)
            buffer.ScrollUpLines(1);
        Assert.Equal(6, buffer.HistoryFloorAbsoluteRow);

        buffer.ScrollDownLines(2);

        Assert.Equal(4, buffer.HistoryFloorAbsoluteRow);
    }
}
