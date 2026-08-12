using Dotty.App.Controls;
using Dotty.App.Views;
using Avalonia;
using Dotty.App.ViewModels;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalSessionStartupSizingTests
{
    [Fact]
    public void Resize_BeforeStart_UpdatesBufferDimensionsUsedForStartup()
    {
        using var session = new TerminalSession();

        session.Resize(cols: 132, rows: 41);

        Assert.Equal(132, session.Adapter.Buffer.Columns);
        Assert.Equal(41, session.Adapter.Buffer.Rows);
        Assert.False(session.IsStarted);
    }

    [Fact]
    public void AddNewTab_SeedsStartupSizeFromActiveTabSession()
    {
        var viewModel = new MainViewModel();
        var activeTab = viewModel.ActiveTab!;

        activeTab.Session.Resize(cols: 136, rows: 73);

        viewModel.AddNewTab();

        var newTab = viewModel.ActiveTab!;
        Assert.NotSame(activeTab, newTab);
        Assert.Equal(136, newTab.Session.Adapter.Buffer.Columns);
        Assert.Equal(73, newTab.Session.Adapter.Buffer.Rows);
        Assert.False(newTab.Session.IsStarted);
    }

    [Fact]
    public void DuplicateTab_SeedsStartupSizeFromSourceTabSession()
    {
        var viewModel = new MainViewModel();
        var sourceTab = viewModel.ActiveTab!;

        sourceTab.Session.Resize(cols: 121, rows: 39);

        var duplicate = viewModel.DuplicateTab(sourceTab);

        Assert.NotSame(sourceTab, duplicate);
        Assert.Equal("Terminal (Copy)", duplicate.Title);
        Assert.Equal(121, duplicate.Session.Adapter.Buffer.Columns);
        Assert.Equal(39, duplicate.Session.Adapter.Buffer.Rows);
        Assert.False(duplicate.Session.IsStarted);
    }

    [Fact]
    public void TerminalCanvas_CellMetrics_AreMeasuredBeforeFirstRender()
    {
        var canvas = new TerminalCanvas
        {
            FontSize = 40
        };

        Assert.True(canvas.CellWidth > 8, "startup sizing should not use the placeholder cell width");
        Assert.True(canvas.CellHeight >= 40, "startup sizing should use measured font metrics before the first PTY start");
    }

    [Fact]
    public void TerminalCanvas_InPlaceBufferResize_InvalidatesMeasuredSize()
    {
        var buffer = new TerminalBuffer(rows: 24, columns: 80);
        var canvas = new TerminalCanvas
        {
            Buffer = buffer,
            FontSize = 20
        };

        var availableSize = new Size(4000, 4000);

        canvas.Measure(availableSize);
        var initialSize = canvas.DesiredSize;

        buffer.Resize(rows: 41, cols: 132);

        canvas.OnBufferUpdated(buffer);
        canvas.Measure(availableSize);

        Assert.True(canvas.DesiredSize.Width > initialSize.Width, "in-place column changes should invalidate measure");
        Assert.True(canvas.DesiredSize.Height > initialSize.Height, "in-place row changes should invalidate measure");
    }

    [Fact]
    public void TerminalCanvas_FollowsBottomAsScrollbackGrows()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 40);
        var canvas = new TerminalCanvas
        {
            Buffer = buffer,
            FontSize = 20
        };

        canvas.Measure(new Size(1000, 1000));
        canvas.Arrange(new Rect(0, 0, 400, canvas.CellHeight * buffer.Rows));
        canvas.OnBufferUpdated(buffer);

        // Replicates the renderer: each frame captures geometry under SyncRoot
        // and applies it once via the coalesced posted update.
        for (int i = 0; i < 20; i++)
        {
            buffer.SetCursor(buffer.Rows - 1, 0);
            buffer.WriteText("streaming output".AsSpan(), CellAttributes.Default);
            buffer.LineFeed();
            canvas.ApplyExtent(canvas.ComputeExtent(buffer.Rows, buffer.ScrollbackCount));
        }

        double maxOffset = Math.Max(0, canvas.Extent.Height - canvas.Viewport.Height);
        Assert.True(canvas.Viewport.Height > 0, "the canvas must have a measured viewport");
        Assert.InRange(Math.Abs(canvas.Offset.Y - maxOffset), 0, 0.1);
    }

    [Fact]
    public void TerminalCanvas_UserScrollUp_MidStream_CancelsFollow()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 40);
        var canvas = new TerminalCanvas
        {
            Buffer = buffer,
            FontSize = 20
        };

        canvas.Measure(new Size(1000, 1000));
        canvas.Arrange(new Rect(0, 0, 400, canvas.CellHeight * buffer.Rows));
        canvas.OnBufferUpdated(buffer);

        for (int i = 0; i < 5; i++)
        {
            buffer.SetCursor(buffer.Rows - 1, 0);
            buffer.WriteText("streaming output".AsSpan(), CellAttributes.Default);
            buffer.LineFeed();
            canvas.ApplyExtent(canvas.ComputeExtent(buffer.Rows, buffer.ScrollbackCount));
        }

        // User wheels up; the viewport leaves the bottom.
        canvas.Offset = new Vector(0, 0);

        // More output arrives; the follow must NOT yank the user back down.
        for (int i = 0; i < 5; i++)
        {
            buffer.SetCursor(buffer.Rows - 1, 0);
            buffer.WriteText("more output".AsSpan(), CellAttributes.Default);
            buffer.LineFeed();
            canvas.ApplyExtent(canvas.ComputeExtent(buffer.Rows, buffer.ScrollbackCount));
        }

        Assert.Equal(0, canvas.Offset.Y);
    }

    [Fact]
    public void SelectResizeViewport_PrefersCanvasViewportOverOuterBounds()
    {
        var viewport = TerminalView.SelectResizeViewport(
            viewBounds: new Size(1200, 800),
            gridBounds: new Size(1160, 760),
            canvasBounds: new Size(1144, 744),
            canvasViewport: new Size(1127, 744));

        Assert.Equal(new Size(1127, 744), viewport);
    }

    [Fact]
    public void CalculateTerminalSize_UsesViewportAndPadding()
    {
        var (cols, rows) = TerminalView.CalculateTerminalSize(
            viewport: new Size(1127, 744),
            padding: new Thickness(16, 10, 16, 16),
            cellWidth: 9,
            cellHeight: 18);

        Assert.Equal(121, cols);
        Assert.Equal(39, rows);
    }

    [Fact]
    public void TryGetSeededStartupBufferSize_ReturnsTrue_ForNonDefaultPreseededBuffer()
    {
        using var session = new TerminalSession();
        session.Resize(cols: 136, rows: 73);

        var hasSeed = TerminalView.TryGetSeededStartupBufferSize(session, out var cols, out var rows);

        Assert.True(hasSeed);
        Assert.Equal(136, cols);
        Assert.Equal(73, rows);
    }

    [Fact]
    public void TryGetSeededStartupBufferSize_ReturnsFalse_ForDefaultBuffer()
    {
        using var session = new TerminalSession();

        var hasSeed = TerminalView.TryGetSeededStartupBufferSize(session, out var cols, out var rows);

        Assert.False(hasSeed);
        Assert.Equal(0, cols);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void ShouldDeferPreStartSizeUpdate_IsTrue_WhenSeedExistsAndNoMeasuredSizeYet()
    {
        using var session = new TerminalSession();
        session.Resize(cols: 136, rows: 73);

        var shouldDefer = TerminalView.ShouldDeferPreStartSizeUpdate(session, lastCols: -1, lastRows: -1);

        Assert.True(shouldDefer);
    }

    [Fact]
    public void ShouldDeferPreStartSizeUpdate_IsTrue_WhenLastSizeIsDefaultAndSeedExists()
    {
        using var session = new TerminalSession();
        session.Resize(cols: 136, rows: 73);

        var shouldDefer = TerminalView.ShouldDeferPreStartSizeUpdate(session, lastCols: 80, lastRows: 24);

        Assert.True(shouldDefer);
    }

    [Fact]
    public void ShouldDeferPreStartSizeUpdate_IsFalse_WhenMeasuredSizeExists()
    {
        using var session = new TerminalSession();
        session.Resize(cols: 136, rows: 73);

        var shouldDefer = TerminalView.ShouldDeferPreStartSizeUpdate(session, lastCols: 120, lastRows: 40);

        Assert.False(shouldDefer);
    }

    [Theory]
    [InlineData(80, 24, true)]
    [InlineData(136, 73, false)]
    public void IsDefaultStartupSize_DetectsDefaultDimensions(int cols, int rows, bool expected)
    {
        Assert.Equal(expected, TerminalView.IsDefaultStartupSize(cols, rows));
    }
}
