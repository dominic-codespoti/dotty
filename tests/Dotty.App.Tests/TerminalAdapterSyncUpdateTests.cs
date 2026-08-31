using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalAdapterSyncUpdateTests
{
    [Fact]
    public void RequestsDuringSyncAreDeferredUntilFlush()
    {
        var adapter = new TerminalAdapter(24, 80);
        int renderCount = 0;
        adapter.RenderRequested += _ => renderCount++;

        adapter.OnSetSynchronizedUpdate(true);

        adapter.OnPrint("abc".AsSpan());
        adapter.OnLineFeed();
        adapter.OnPrint("def".AsSpan());

        Assert.Equal(0, renderCount);

        adapter.OnSetSynchronizedUpdate(false);

        Assert.Equal(1, renderCount);
    }

    [Fact]
    public void RequestsBeforeSyncStillFireNormally()
    {
        var adapter = new TerminalAdapter(24, 80);
        int renderCount = 0;
        adapter.RenderRequested += _ => renderCount++;

        adapter.OnPrint("hello".AsSpan());
        adapter.FlushRender();

        Assert.Equal(1, renderCount);
    }

    [Fact]
    public void SyncThenDisableThenMore_WontDoubleFlush()
    {
        var adapter = new TerminalAdapter(24, 80);
        int renderCount = 0;
        adapter.RenderRequested += _ => renderCount++;

        adapter.OnSetSynchronizedUpdate(true);
        adapter.OnPrint("a".AsSpan());
        adapter.OnSetSynchronizedUpdate(false);
        Assert.Equal(1, renderCount);

        adapter.OnPrint("b".AsSpan());
        adapter.FlushRender();
        Assert.Equal(2, renderCount);
    }

    [Fact]
    public void DuplicateEnableAndDisableAreNoOps()
    {
        var adapter = new TerminalAdapter(24, 80);
        int renderCount = 0;
        adapter.RenderRequested += _ => renderCount++;

        adapter.OnSetSynchronizedUpdate(true);
        adapter.OnSetSynchronizedUpdate(true);
        adapter.OnSetSynchronizedUpdate(false);
        adapter.OnSetSynchronizedUpdate(false);

        Assert.Equal(0, renderCount);
    }

    [Fact]
    public void DisableWithoutDirtyStateDoesNotFlush()
    {
        var adapter = new TerminalAdapter(24, 80);
        int renderCount = 0;
        adapter.RenderRequested += _ => renderCount++;

        adapter.OnSetSynchronizedUpdate(true);
        adapter.OnSetSynchronizedUpdate(false);

        Assert.Equal(0, renderCount);
    }

    [Fact]
    public void MultipleWritesProduceOneNotificationWhenDisabled()
    {
        var adapter = new TerminalAdapter(24, 80);
        int renderCount = 0;
        adapter.RenderRequested += _ => renderCount++;

        adapter.OnSetSynchronizedUpdate(true);
        adapter.OnPrint("one".AsSpan());
        adapter.OnPrint("two".AsSpan());
        adapter.OnPrint("three".AsSpan());
        adapter.OnSetSynchronizedUpdate(false);

        Assert.Equal(1, renderCount);
        Assert.False(adapter.SynchronizedUpdateActive);
    }

    [Fact]
    public void OriginMode_Cha_DoesNotRebaseCurrentRow()
    {
        var adapter = new TerminalAdapter(20, 80);
        var buffer = adapter.Buffer;

        adapter.OnSetScrollRegion(5, 15);
        adapter.OnSetOriginMode(true);
        adapter.OnMoveCursor(3, 10);

        Assert.Equal(6, buffer.CursorRow);
        Assert.Equal(9, buffer.CursorCol);

        adapter.OnCursorHorizontalAbsolute(4);

        Assert.Equal(6, buffer.CursorRow);
        Assert.Equal(3, buffer.CursorCol);
    }

    [Fact]
    public void OriginMode_Vpa_MovesToAbsoluteRowWithoutApplyingRelativeOffsetTwice()
    {
        var adapter = new TerminalAdapter(20, 80);
        var buffer = adapter.Buffer;

        adapter.OnSetScrollRegion(5, 15);
        adapter.OnSetOriginMode(true);
        adapter.OnMoveCursor(3, 10);

        adapter.OnCursorVerticalAbsolute(7);

        Assert.Equal(10, buffer.CursorRow);
        Assert.Equal(9, buffer.CursorCol);
    }

    [Fact]
    public void OriginMode_Tab_PreservesCurrentRow()
    {
        var adapter = new TerminalAdapter(20, 80);
        var buffer = adapter.Buffer;

        adapter.OnSetScrollRegion(5, 15);
        adapter.OnSetOriginMode(true);
        adapter.OnMoveCursor(4, 3);

        Assert.Equal(7, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);

        adapter.OnTab();

        Assert.Equal(7, buffer.CursorRow);
        Assert.Equal(8, buffer.CursorCol);
    }

    [Fact]
    public void OriginMode_BackTab_PreservesCurrentRow()
    {
        var adapter = new TerminalAdapter(20, 80);
        var buffer = adapter.Buffer;

        adapter.OnSetScrollRegion(5, 15);
        adapter.OnSetOriginMode(true);
        adapter.OnMoveCursor(4, 18);

        Assert.Equal(7, buffer.CursorRow);
        Assert.Equal(17, buffer.CursorCol);

        adapter.OnBackTab(1);

        Assert.Equal(7, buffer.CursorRow);
        Assert.Equal(16, buffer.CursorCol);
    }
}
