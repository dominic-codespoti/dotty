using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class SmoothScrollTests
{
    [Fact]
    public void DecPrivateModeFourTracksTransitionsAndDisablingStopsAnimation()
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 8, scrollbackCapacity: 8);
        int notifications = 0;
        adapter.SmoothScrollModeChanged += _ => notifications++;

        adapter.OnSetSmoothScroll(true);
        adapter.OnSetSmoothScroll(true);
        Assert.True(adapter.SmoothScrollEnabled);
        Assert.Equal(1, notifications);

        adapter.Buffer.SetCursor(1, 0);
        adapter.Buffer.LineFeed();
        Assert.True(adapter.SmoothScrollActive);
        Assert.Equal(0f, adapter.SmoothScrollProgress);
        long nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000
            / System.Diagnostics.Stopwatch.Frequency;
        Assert.True(adapter.AdvanceSmoothScrollFrame(nowMs + 20));
        Assert.InRange(adapter.SmoothScrollProgress, 0f, 1f);

        adapter.OnSetSmoothScroll(false);
        Assert.False(adapter.SmoothScrollEnabled);
        Assert.False(adapter.SmoothScrollActive);
        Assert.False(adapter.AdvanceSmoothScrollFrame(500));
        Assert.Equal(0, adapter.SmoothScrollDirection);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void RealBufferScrollStartsOneBoundedAnimationAndBurstCoalesces()
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 8, scrollbackCapacity: 8);
        adapter.Buffer.SetCursor(0, 0);
        adapter.Buffer.WriteText("old", CellAttributes.Default);
        adapter.Buffer.SetCursor(1, 0);
        adapter.Buffer.WriteText("new", CellAttributes.Default);
        adapter.OnSetSmoothScroll(true);
        adapter.Buffer.LineFeed();
        Assert.True(adapter.SmoothScrollActive);
        Assert.NotNull(adapter.SmoothScrollOutgoingSnapshot);
        Assert.StartsWith("old", adapter.SmoothScrollOutgoingSnapshot!.GetVisibleRowText(0));
        Assert.StartsWith("new", adapter.Buffer.GetRowText(0));
        var firstOutgoing = adapter.SmoothScrollOutgoingSnapshot;
        adapter.Buffer.LineFeed();
        Assert.NotSame(firstOutgoing, adapter.SmoothScrollOutgoingSnapshot);
        Assert.StartsWith("new", adapter.SmoothScrollOutgoingSnapshot!.GetVisibleRowText(0));

        long nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000
            / System.Diagnostics.Stopwatch.Frequency + 100;
        Assert.True(adapter.AdvanceSmoothScrollFrame(nowMs));
        Assert.True(adapter.SmoothScrollProgress > 0f);
        Assert.True(adapter.AdvanceSmoothScrollFrame(nowMs + 1_000));
        Assert.Equal(1f, adapter.SmoothScrollProgress);
        Assert.False(adapter.AdvanceSmoothScrollFrame(nowMs + 1_001));
        Assert.False(adapter.SmoothScrollActive);
        adapter.OnSetSmoothScroll(false);
    }

    [Fact]
    public void DownScrollUsesOppositeDirectionAndPartialWidthRemainsAnExactJump()
    {
        using var adapter = new TerminalAdapter(rows: 3, columns: 8, scrollbackCapacity: 8);
        adapter.Buffer.WriteText("A", CellAttributes.Default);
        adapter.OnSetSmoothScroll(true);
        adapter.Buffer.ScrollDownLines(1);
        Assert.True(adapter.SmoothScrollActive);
        Assert.Equal(1, adapter.SmoothScrollDirection);
        Assert.StartsWith("A", adapter.SmoothScrollOutgoingSnapshot!.GetVisibleRowText(0));

        adapter.ResetSmoothScrollAnimation();
        Assert.True(adapter.SmoothScrollEnabled);
        Assert.False(adapter.SmoothScrollActive);
        Assert.Null(adapter.SmoothScrollOutgoingSnapshot);
        adapter.Buffer.SetLeftRightMarginMode(true);
        adapter.Buffer.SetHorizontalMargins(2, 7);
        adapter.Buffer.ScrollUpLines(1);
        Assert.False(adapter.SmoothScrollActive);
        Assert.Null(adapter.SmoothScrollOutgoingSnapshot);
        adapter.OnSetSmoothScroll(false);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchingScreensCancelsOutgoingFrameAndNextScrollCapturesNewScreen(bool use1049)
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 8, scrollbackCapacity: 8);
        adapter.Buffer.WriteText("old", CellAttributes.Default);
        adapter.OnSetSmoothScroll(true);
        adapter.Buffer.SetCursor(1, 0);
        adapter.Buffer.LineFeed();
        Assert.NotNull(adapter.SmoothScrollOutgoingSnapshot);
        Assert.StartsWith("old", adapter.SmoothScrollOutgoingSnapshot!.GetVisibleRowText(0));

        if (use1049)
            adapter.OnSetAlternateScreen(true);
        else
            adapter.OnSwitchAlternateScreen(47, true);

        Assert.True(adapter.SmoothScrollEnabled);
        Assert.False(adapter.SmoothScrollActive);
        Assert.Null(adapter.SmoothScrollOutgoingSnapshot);
        Assert.Equal(0u, adapter.Buffer.GetCell(0, 0).Rune);

        adapter.Buffer.SetCursor(0, 0);
        adapter.Buffer.WriteText("alt", CellAttributes.Default);
        adapter.Buffer.SetCursor(1, 0);
        adapter.Buffer.LineFeed();
        Assert.NotNull(adapter.SmoothScrollOutgoingSnapshot);
        Assert.StartsWith("alt", adapter.SmoothScrollOutgoingSnapshot!.GetVisibleRowText(0));
        adapter.OnSetSmoothScroll(false);
    }

}
