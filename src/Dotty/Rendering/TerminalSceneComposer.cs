using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scrollbar;
using Dotty.Runtime.Search;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// State for the optional search overlay included in a composed frame.
/// </summary>
public readonly record struct SearchOverlayRenderState(
    bool IsActive,
    string Query,
    int ActiveMatchIndex,
    int TotalMatches);

/// <summary>
/// The GPU instances and atlas rows produced by <see cref="TerminalSceneComposer"/>.
/// </summary>
public sealed class TerminalSceneFrame
{
    public CellInstance[] Instances { get; }
    public int InstanceCount { get; }
    public HashSet<int> DirtyAtlasRows { get; }
    public ChromeQuadInstance[] ChromeQuads { get; }
    public int ChromeQuadCount { get; }
    /// <summary>Original cell-instance index at which context-menu glyphs begin.</summary>
    public int MenuInstanceStart { get; }
    /// <summary>Chrome-quad index at which context-menu chrome begins.</summary>
    public int MenuChromeStart { get; }

    public TerminalSceneFrame(
        CellInstance[] instances,
        int instanceCount,
        HashSet<int> dirtyAtlasRows,
        ChromeQuadInstance[] chromeQuads,
        int chromeQuadCount,
        int menuInstanceStart = -1,
        int menuChromeStart = -1)
    {
        Instances = instances;
        InstanceCount = instanceCount;
        DirtyAtlasRows = dirtyAtlasRows;
        ChromeQuads = chromeQuads;
        ChromeQuadCount = chromeQuadCount;
        MenuInstanceStart = menuInstanceStart;
        MenuChromeStart = menuChromeStart;
    }

    public ReadOnlySpan<CellInstance> AsSpan() => new(Instances, 0, InstanceCount);
    public ReadOnlySpan<ChromeQuadInstance> AsChromeSpan() => new(ChromeQuads, 0, ChromeQuadCount);
}

/// <summary>
/// Composes the terminal scene into host-neutral GPU cell instances.
/// Window lifecycle, input, and OpenGL submission remain outside this class.
/// </summary>
public sealed class TerminalSceneComposer
{
    private readonly TextSelectionService _selectionService;
    private GlyphAtlas _atlas;
    private SKTypeface _typeface;
    private float _fontSize;
    private CellInstance[] _frameScratch = Array.Empty<CellInstance>();
    private ChromeQuadInstance[] _chromeScratch = Array.Empty<ChromeQuadInstance>();
    private readonly HashSet<int> _dirtyAtlasRows = new();

    public TerminalSceneComposer(
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        TextSelectionService selectionService)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _typeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
        _fontSize = fontSize;
        _selectionService = selectionService ?? throw new ArgumentNullException(nameof(selectionService));
    }

    public void UpdateResources(GlyphAtlas atlas, SKTypeface typeface, float fontSize)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _typeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
        _fontSize = fontSize;
    }

    public TerminalSceneFrame Compose(
        TerminalTab activeTab,
        TerminalTabManager tabManager,
        IColorScheme theme,
        SgrColorArgb themeForeground,
        SgrColorArgb selectionColor,
        int framebufferWidth,
        int framebufferHeight,
        float cellWidth,
        float cellHeight,
        float scale,
        int rows,
        int columns,
        PaddingUserConfig padding,
        bool showTabBar,
        bool cursorVisible,
        bool scrollbarHovered,
        bool scrollbarDragging,
        SearchOverlayRenderState searchOverlay,
        ContextMenuModel? activeContextMenu,
        int hoveredTabIndex = -1,
        TabBarHitType hoveredTabHitType = TabBarHitType.None)
    {
        ArgumentNullException.ThrowIfNull(activeTab);
        ArgumentNullException.ThrowIfNull(tabManager);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(padding);

        scale = Math.Max(0.1f, scale);
        float padLeft = (float)padding.Left * scale;
        float padTop = (float)padding.Top * scale;
        float padX = (float)(padding.Left + padding.Right) * scale;
        float padY = (float)(padding.Top + padding.Bottom) * scale;
        int barRows = showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, cellHeight) : 0;
        float topOffset = barRows * cellHeight * scale;
        int maxInstances = checked(Math.Max(1024, rows * columns * 2 + 1024));
        EnsureScratchCapacity(maxInstances);
        int instanceCount = 0;
        int chromeQuadCount = 0;
        int menuInstanceStart = -1;
        int menuChromeStart = -1;
        _dirtyAtlasRows.Clear();

        float terminalWidth = Math.Max(10f, framebufferWidth - padX);
        float terminalHeight = Math.Max(10f, framebufferHeight - topOffset - padY);
        activeTab.PaneTree.Layout(terminalWidth, terminalHeight, cellWidth * scale, cellHeight * scale, dividerThickness: 2f);


        foreach (var leaf in activeTab.PaneTree.Leaves)
        {
            if (leaf.Columns > 0 && leaf.Rows > 0 &&
                (leaf.Session.Adapter.Buffer.Columns != leaf.Columns || leaf.Session.Adapter.Buffer.Rows != leaf.Rows))
            {
                leaf.Session.Resize(leaf.Columns, leaf.Rows);
            }

            RenderSnapshot? leafSnapshot = null;
            bool lockTaken = false;
            var leafBuffer = leaf.Session.Adapter.Buffer;
            int scrollOffset = ReferenceEquals(leaf, activeTab.ActivePane) ? activeTab.ScrollOffset : 0;

            try
            {
                leafBuffer.ReaderWaiting = true;
                global::System.Threading.Monitor.TryEnter(leafBuffer.SyncRoot, 4, ref lockTaken);
                if (lockTaken)
                {
                    leafBuffer.MarkRender();
                    leafSnapshot = leafBuffer.CaptureRenderSnapshotVisible(sbStart: 0, sbEnd: -1, scrollOffset: scrollOffset);
                }
            }
            finally
            {
                if (lockTaken) global::System.Threading.Monitor.Exit(leafBuffer.SyncRoot);
                leafBuffer.ReaderWaiting = false;
            }

            if (leafSnapshot == null)
                continue;

            using (leafSnapshot)
            {
                int startInstanceIndex = instanceCount;
                int paneRows = leafSnapshot.Rows;
                int paneColumns = leafSnapshot.Columns;
                EnsureScratchCapacity(instanceCount + checked(paneRows * paneColumns * 2 + 1024));

                int written = QuadFrameBuilder.Build(
                    leafSnapshot,
                    _atlas,
                    _typeface,
                    _fontSize,
                    _frameScratch.AsSpan(startInstanceIndex),
                    _dirtyAtlasRows,
                    paneRows,
                    paneColumns,
                    themeForeground,
                    new SgrColorArgb(theme.Background));
                int startColumnOffset = (int)Math.Round(leaf.Bounds.X / (cellWidth * scale));
                int startRowOffset = (int)Math.Round(leaf.Bounds.Y / (cellHeight * scale)) + barRows;
                for (int i = 0; i < written; i++)
                {
                    ref var instance = ref _frameScratch[startInstanceIndex + i];
                    instance.Col += (ushort)startColumnOffset;
                    instance.Row += (ushort)startRowOffset;
                }
                instanceCount += written;

                if (ReferenceEquals(leaf, activeTab.ActivePane) && _selectionService.HasSelection)
                {
                    byte selectionAlpha = (byte)((selectionColor.A != 0 && selectionColor.A != 255) ? selectionColor.A : 128);

                    for (int i = 0; i < written; i++)
                    {
                        ref var instance = ref _frameScratch[startInstanceIndex + i];
                        int localColumn = instance.Col - startColumnOffset;
                        int localRow = instance.Row - startRowOffset - scrollOffset;
                        if (_selectionService.IsCellSelected(localRow, localColumn))
                        {
                            instance.BgR = selectionColor.R;
                            instance.BgG = selectionColor.G;
                            instance.BgB = selectionColor.B;
                            instance.BgA = selectionAlpha;
                        }
                    }

                    var range = _selectionService.GetNormalizedRange();
                    int minRow = Math.Max(0, range.StartRow);
                    int maxRow = Math.Min(paneRows - 1, range.EndRow);
                    EnsureScratchCapacity(instanceCount + Math.Max(0, (maxRow - minRow + 1) * paneColumns));
                    for (int row = minRow; row <= maxRow; row++)
                    {
                        int minColumn = row == range.StartRow ? range.StartColumn : 0;
                        int maxColumn = row == range.EndRow ? range.EndColumn : paneColumns - 1;
                        for (int column = minColumn; column <= maxColumn; column++)
                        {
                            int logicalRow = row - scrollOffset;
                            if (!_selectionService.IsCellSelected(logicalRow, column))
                                continue;

                            int targetColumn = column + startColumnOffset;
                            int targetRow = row + startRowOffset;
                            bool covered = false;
                            for (int i = startInstanceIndex; i < instanceCount; i++)
                            {
                                if (_frameScratch[i].Col == targetColumn && _frameScratch[i].Row == targetRow)
                                {
                                    covered = true;
                                    break;
                                }
                            }

                            if (!covered)
                            {
                                EnsureScratchCapacity(instanceCount + 1);
                                _frameScratch[instanceCount++] = new CellInstance
                                {
                                    Col = (ushort)targetColumn,
                                    Row = (ushort)targetRow,
                                    BgR = selectionColor.R,
                                    BgG = selectionColor.G,
                                    BgB = selectionColor.B,
                                    BgA = selectionAlpha,
                                    Flags = 0
                                };
                            }
                        }
                    }
                }

                if (cursorVisible && ReferenceEquals(leaf, activeTab.ActivePane)
                    && leafSnapshot.CursorRow >= 0 && leafSnapshot.CursorRow < paneRows
                    && leafSnapshot.CursorCol >= 0 && leafSnapshot.CursorCol < paneColumns)
                {
                    int cursorRow = leafSnapshot.CursorRow + startRowOffset;
                    int cursorColumn = leafSnapshot.CursorCol + startColumnOffset;
                    bool found = false;
                    for (int i = 0; i < instanceCount; i++)
                    {
                        ref var instance = ref _frameScratch[i];
                        if (instance.Row != cursorRow || instance.Col != cursorColumn)
                            continue;

                        if (leafSnapshot.CursorShape == TerminalCursorShape.Block)
                        {
                            instance.BgR = themeForeground.R;
                            instance.BgG = themeForeground.G;
                            instance.BgB = themeForeground.B;
                            instance.BgA = 128;
                        }
                        else if (leafSnapshot.CursorShape == TerminalCursorShape.Underline)
                        {
                            instance.Flags |= CellFlags.Underline;
                        }
                        found = true;
                        break;
                    }

                    if (!found)
                    {
                        EnsureScratchCapacity(instanceCount + 1);
                        _frameScratch[instanceCount++] = new CellInstance
                        {
                            Col = (ushort)cursorColumn,
                            Row = (ushort)cursorRow,
                            BgR = themeForeground.R,
                            BgG = themeForeground.G,
                            BgB = themeForeground.B,
                            BgA = 128,
                        };
                    }
                }

                if (leafBuffer.ScrollbackCount > 0)
                {
                    bool emphasizedScrollbar = ReferenceEquals(leaf, activeTab.ActivePane) && (scrollbarDragging || scrollbarHovered);
                    EnsureScratchCapacity(instanceCount + paneRows + 1);
                    int scrollbarQuads = ScrollbarQuadBuilder.Build(
                        startColumnOffset,
                        startRowOffset,
                        paneColumns,
                        paneRows,
                        leafBuffer.ScrollbackCount,
                        activeTab.ScrollOffset,
                        theme,
                        _frameScratch.AsSpan(instanceCount),
                        isHoveredOrDragging: emphasizedScrollbar);
                    instanceCount += scrollbarQuads;
                }
            }
        }

        if (showTabBar && tabManager.Count > 0)
        {
            EnsureScratchCapacity(instanceCount + 2048);
            EnsureChromeScratchCapacity(chromeQuadCount + tabManager.Count * 4 + 8);
            int tabQuads = TabBarQuadBuilder.Build(
                tabManager,
                _atlas,
                _typeface,
                _fontSize,
                theme,
                framebufferWidth,
                cellWidth * scale,
                cellHeight * scale,
                _frameScratch.AsSpan(instanceCount),
                _chromeScratch.AsSpan(chromeQuadCount),
                out int chromeQuadsWritten,
                barRows * cellHeight * scale,
                hoveredTabIndex,
                hoveredTabHitType);
            instanceCount += tabQuads;
            chromeQuadCount += chromeQuadsWritten;
        }

        if (searchOverlay.IsActive)
        {
            var overlayLayout = SearchOverlayLayout.Compute(
                framebufferWidth,
                framebufferHeight,
                searchOverlay.Query,
                searchOverlay.ActiveMatchIndex,
                searchOverlay.TotalMatches);
            EnsureScratchCapacity(instanceCount + 1024);
            int overlayQuads = SearchQuadBuilder.BuildOverlayQuads(
                in overlayLayout,
                cellWidth * scale,
                cellHeight * scale,
                _atlas,
                _typeface,
                _fontSize,
                _frameScratch.AsSpan(instanceCount),
                _dirtyAtlasRows);
            instanceCount += overlayQuads;
        }

        if (activeContextMenu != null && activeContextMenu.IsVisible)
        {
            menuInstanceStart = instanceCount;
            menuChromeStart = chromeQuadCount;
            var menuLayout = ContextMenuLayout.Calculate(
                activeContextMenu,
                framebufferWidth,
                framebufferHeight,
                cellWidth * scale,
                cellHeight * scale);
            EnsureScratchCapacity(instanceCount + 1024);
            EnsureChromeScratchCapacity(chromeQuadCount + activeContextMenu.Items.Count * 2 + 8);
            int menuQuads = ContextMenuQuadBuilder.Build(
                activeContextMenu,
                menuLayout,
                _atlas,
                _typeface,
                _fontSize,
                theme,
                cellWidth * scale,
                cellHeight * scale,
                _frameScratch.AsSpan(instanceCount),
                _chromeScratch.AsSpan(chromeQuadCount),
                out int menuChromeWritten,
                padLeft,
                padTop);
            instanceCount += menuQuads;
            chromeQuadCount += menuChromeWritten;
        }

        // The frame is consumed synchronously by the OpenGL host before the next
        // Compose call, so reuse the scratch buffers and avoid per-frame copies.
        return new TerminalSceneFrame(
            _frameScratch,
            instanceCount,
            _dirtyAtlasRows,
            _chromeScratch,
            chromeQuadCount,
            menuInstanceStart,
            menuChromeStart);
    }

    private void EnsureChromeScratchCapacity(int required)
    {
        if (required <= _chromeScratch.Length)
            return;

        int capacity = Math.Max(required, _chromeScratch.Length == 0 ? 64 : _chromeScratch.Length * 2);
        Array.Resize(ref _chromeScratch, capacity);
    }

    private void EnsureScratchCapacity(int required)
    {
        if (required <= _frameScratch.Length)
            return;

        int capacity = Math.Max(required, _frameScratch.Length == 0 ? 4096 : _frameScratch.Length * 2);
        Array.Resize(ref _frameScratch, capacity);
    }

}
