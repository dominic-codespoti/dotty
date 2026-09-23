using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Scrollbar;
using Dotty.Runtime.Search;
using RuntimeSearchMatch = Dotty.Runtime.Search.SearchMatch;
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
    int TotalMatches,
    IReadOnlyList<RuntimeSearchMatch>? Matches);

/// <summary>
/// The GPU instances and atlas rows produced by <see cref="TerminalSceneComposer"/>.
/// </summary>
public sealed class TerminalSceneFrame
{
    public CellInstance[] Instances { get; private set; } = null!;
    public int InstanceCount { get; private set; }
    public HashSet<int> DirtyAtlasRows { get; private set; } = null!;
    public ChromeQuadInstance[] ChromeQuads { get; private set; } = null!;
    public int ChromeQuadCount { get; private set; }
    public int ScrollbarChromeStart { get; private set; }
    public int MenuInstanceStart { get; private set; }
    public int MenuChromeStart { get; private set; }
    public bool IsIncomplete { get; private set; }

    public TerminalSceneFrame(
        CellInstance[] instances,
        int instanceCount,
        HashSet<int> dirtyAtlasRows,
        ChromeQuadInstance[] chromeQuads,
        int chromeQuadCount,
        int menuInstanceStart = -1,
        int menuChromeStart = -1,
        int scrollbarChromeStart = -1,
        bool isIncomplete = false)
    {
        Update(instances, instanceCount, dirtyAtlasRows, chromeQuads, chromeQuadCount,
            menuInstanceStart, menuChromeStart, scrollbarChromeStart, isIncomplete);
    }

    internal void Update(
        CellInstance[] instances,
        int instanceCount,
        HashSet<int> dirtyAtlasRows,
        ChromeQuadInstance[] chromeQuads,
        int chromeQuadCount,
        int menuInstanceStart,
        int menuChromeStart,
        int scrollbarChromeStart,
        bool isIncomplete)
    {
        Instances = instances;
        InstanceCount = instanceCount;
        DirtyAtlasRows = dirtyAtlasRows;
        ChromeQuads = chromeQuads;
        ChromeQuadCount = chromeQuadCount;
        MenuInstanceStart = menuInstanceStart;
        MenuChromeStart = menuChromeStart;
        ScrollbarChromeStart = scrollbarChromeStart;
        IsIncomplete = isIncomplete;
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
    private SKTypeface _typeface;
    private GlyphAtlas _atlas;
    private float _fontSize;
    private CellInstance[] _frameScratch = Array.Empty<CellInstance>();
    private ChromeQuadInstance[] _chromeScratch = Array.Empty<ChromeQuadInstance>();
    private ChromeQuadInstance[] _scrollbarScratch = Array.Empty<ChromeQuadInstance>();
    private readonly HashSet<int> _dirtyAtlasRows = new();
    private TerminalSceneFrame? _cachedFrame;
    private ContextMenuModel? _cachedMenuModel;
    private IReadOnlyList<ContextMenuItem>? _cachedMenuItems;
    private ContextMenuLayout? _cachedMenuLayout;
    private float _cachedMenuX;
    private float _cachedMenuY;
    private float _cachedMenuViewportWidth;
    private float _cachedMenuViewportHeight;
    private float _cachedMenuCellWidth;
    private float _cachedMenuCellHeight;
    private ContextMenuItem[] _cachedMenuItemSnapshot = Array.Empty<ContextMenuItem>();
    private int _cachedMenuItemCount = -1;
    private long _skippedLeafFrames;
    /// <summary>Cumulative Compose calls in which at least one leaf was skipped on lock contention.</summary>
    public long SkippedLeafFrames => _skippedLeafFrames;

    public TerminalSceneComposer(
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _typeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
        _fontSize = fontSize;
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
        TabBarHitType hoveredTabHitType = TabBarHitType.None,
        ITabTitleSource? titles = null,
        ReadOnlySpan<char> status = default,
        bool statusWarning = false)
    {
        ArgumentNullException.ThrowIfNull(activeTab);
        ArgumentNullException.ThrowIfNull(tabManager);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(padding);

        var activePane = activeTab.ActivePane;
        var leaves = activeTab.PaneTree.Leaves;
        scale = Math.Max(0.1f, scale);
        float padLeft = (float)padding.Left * scale;
        float padTop = (float)padding.Top * scale;
        float padX = (float)(padding.Left + padding.Right) * scale;
        float padY = (float)(padding.Top + padding.Bottom) * scale;
        int barRows = showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, cellHeight) : 0;
        float topOffset = barRows * cellHeight * scale;
        int maxInstances = checked(Math.Max(1024, rows * columns * 2 + 1024));
        EnsureScratchCapacity(maxInstances);
        EnsureScrollbarScratchCapacity(checked(Math.Max(2, leaves.Count * 2)));
        int instanceCount = 0;
        int chromeQuadCount = 0;
        int scrollbarChromeCount = 0;
        int scrollbarChromeStart = -1;
        int menuInstanceStart = -1;
        int menuChromeStart = -1;
        bool skippedLeaf = false;
        _dirtyAtlasRows.Clear();

        float terminalWidth = Math.Max(10f, framebufferWidth - padX);
        float terminalHeight = Math.Max(10f, framebufferHeight - topOffset - padY);
        activeTab.PaneTree.Layout(terminalWidth, terminalHeight, cellWidth * scale, cellHeight * scale, dividerThickness: 2f);


        for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
        {
            var leaf = leaves[leafIndex];
            if (leaf.Columns > 0 && leaf.Rows > 0 &&
                (leaf.Session.Adapter.Buffer.Columns != leaf.Columns || leaf.Session.Adapter.Buffer.Rows != leaf.Rows))
            {
                leaf.Session.Resize(leaf.Columns, leaf.Rows);
            }

            RenderSnapshot? leafSnapshot = null;
            bool lockTaken = false;
            var leafBuffer = leaf.Session.Adapter.Buffer;
            int scrollOffset = leaf.ScrollOffset;

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
            {
                skippedLeaf = true;
                continue;
            }

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

                if (leaf.Selection.HasSelection)
                {
                    byte selectionAlpha = (byte)((selectionColor.A != 0 && selectionColor.A != 255) ? selectionColor.A : 128);

                    for (int i = 0; i < written; i++)
                    {
                        ref var instance = ref _frameScratch[startInstanceIndex + i];
                        int localColumn = instance.Col - startColumnOffset;
                        int viewRow = instance.Row - startRowOffset;
                        int logicalRow = viewRow - scrollOffset;
                        if (leaf.Selection.IsCellSelected(logicalRow, localColumn))
                        {
                            instance.BgR = selectionColor.R;
                            instance.BgG = selectionColor.G;
                            instance.BgB = selectionColor.B;
                            instance.BgA = selectionAlpha;
                        }
                    }

                    var range = leaf.Selection.GetNormalizedRange();
                    int firstLogicalRow = Math.Max(range.StartRow, -scrollOffset);
                    int lastLogicalRow = Math.Min(range.EndRow, paneRows - 1 - scrollOffset);
                    int estimatedRows = Math.Max(0, lastLogicalRow - firstLogicalRow + 1);
                    EnsureScratchCapacity(instanceCount + checked(estimatedRows * paneColumns));
                    for (int logicalRow = firstLogicalRow; logicalRow <= lastLogicalRow; logicalRow++)
                    {
                        int viewRow = logicalRow + scrollOffset;
                        int minColumn = logicalRow == range.StartRow ? range.StartColumn : 0;
                        int maxColumn = logicalRow == range.EndRow ? range.EndColumn : paneColumns - 1;
                        minColumn = Math.Max(0, minColumn);
                        maxColumn = Math.Min(paneColumns - 1, maxColumn);
                        for (int column = minColumn; column <= maxColumn; column++)
                        {
                            if (!leaf.Selection.IsCellSelected(logicalRow, column))
                                continue;

                            int targetColumn = column + startColumnOffset;
                            int targetRow = viewRow + startRowOffset;
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

                if (cursorVisible && scrollOffset == 0 && ReferenceEquals(leaf, activePane)
                    && leafSnapshot.CursorRow >= 0 && leafSnapshot.CursorRow < paneRows
                    && leafSnapshot.CursorCol >= 0 && leafSnapshot.CursorCol < paneColumns)
                {
                    int cursorRow = leafSnapshot.CursorRow + startRowOffset;
                    int cursorColumn = leafSnapshot.CursorCol + startColumnOffset;
                    bool found = false;
                    for (int i = startInstanceIndex; i < instanceCount; i++)
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
                if (ReferenceEquals(leaf, activePane)
                    && searchOverlay.Matches is { Count: > 0 } matches)
                {
                    long highlightCapacity = 0;
                    for (int i = 0; i < matches.Count; i++)
                    {
                        var match = matches[i];
                        long visibleRow = (long)match.Row + scrollOffset;
                        if (visibleRow < 0 || visibleRow >= paneRows)
                            continue;

                        int startColumn = Math.Max(0, match.StartCol);
                        int endColumn = Math.Min(paneColumns, match.EndCol);
                        if (startColumn < endColumn)
                            highlightCapacity += endColumn - startColumn;
                    }

                    if (highlightCapacity > 0)
                    {
                        EnsureScratchCapacity(checked(instanceCount + checked((int)highlightCapacity)));
                        int highlightQuads = SearchQuadBuilder.BuildHighlightQuads(
                            matches,
                            paneRows,
                            paneColumns,
                            _frameScratch.AsSpan(instanceCount),
                            logicalRowOffset: scrollOffset,
                            globalRowOffset: startRowOffset,
                            globalColumnOffset: startColumnOffset);
                        instanceCount += highlightQuads;
                    }
                }


                if (leafBuffer.ScrollbackCount > 0)
                {
                    bool emphasizedScrollbar = ReferenceEquals(leaf, activePane) && (scrollbarDragging || scrollbarHovered);
                    int scrollbarQuads = ScrollbarQuadBuilder.Build(
                        paneX: padLeft + leaf.Bounds.X,
                        paneY: padTop + topOffset + leaf.Bounds.Y,
                        paneWidth: leaf.Bounds.Width,
                        paneHeight: leaf.Bounds.Height,
                        scrollbackCount: leafBuffer.ScrollbackCount,
                        scrollOffset: scrollOffset,
                        cellHeight: cellHeight * scale,
                        theme: theme,
                        destination: _scrollbarScratch.AsSpan(scrollbarChromeCount),
                        isHoveredOrDragging: emphasizedScrollbar);
                    scrollbarChromeCount += scrollbarQuads;
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
                hoveredTabHitType,
                titles,
                status,
                statusWarning);
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

        // Scrollbars are collected separately while panes are rendered so they
        // can be appended after all base chrome/glyphs and before the menu.
        if (scrollbarChromeCount > 0)
        {
            scrollbarChromeStart = chromeQuadCount;
            EnsureChromeScratchCapacity(chromeQuadCount + scrollbarChromeCount);
            _scrollbarScratch.AsSpan(0, scrollbarChromeCount)
                .CopyTo(_chromeScratch.AsSpan(chromeQuadCount));
            chromeQuadCount += scrollbarChromeCount;
        }


        if (activeContextMenu != null && activeContextMenu.IsVisible)
        {
            menuInstanceStart = instanceCount;
            menuChromeStart = chromeQuadCount;
            var menuLayout = GetContextMenuLayout(
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
        if (skippedLeaf)
            _skippedLeafFrames++;
        if (_cachedFrame == null)
        {
            _cachedFrame = new TerminalSceneFrame(
                _frameScratch, instanceCount, _dirtyAtlasRows, _chromeScratch,
                chromeQuadCount, menuInstanceStart, menuChromeStart,
                scrollbarChromeStart, skippedLeaf);
        }
        else
        {
            _cachedFrame.Update(
                _frameScratch, instanceCount, _dirtyAtlasRows, _chromeScratch,
                chromeQuadCount, menuInstanceStart, menuChromeStart,
                scrollbarChromeStart, skippedLeaf);
        }
        return _cachedFrame;
    }
    private ContextMenuLayout GetContextMenuLayout(
        ContextMenuModel model,
        float viewportWidth,
        float viewportHeight,
        float cellWidth,
        float cellHeight)
    {
        var items = model.Items;
        bool sameItems = items.Count == _cachedMenuItemCount;
        if (sameItems)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (!ReferenceEquals(items[i], _cachedMenuItemSnapshot[i]))
                {
                    sameItems = false;
                    break;
                }
            }
        }

        if (sameItems
            && ReferenceEquals(model, _cachedMenuModel)
            && ReferenceEquals(items, _cachedMenuItems)
            && model.X == _cachedMenuX
            && model.Y == _cachedMenuY
            && viewportWidth == _cachedMenuViewportWidth
            && viewportHeight == _cachedMenuViewportHeight
            && cellWidth == _cachedMenuCellWidth
            && cellHeight == _cachedMenuCellHeight
            && _cachedMenuLayout != null)
            return _cachedMenuLayout;

        var layout = ContextMenuLayout.Calculate(model, viewportWidth, viewportHeight, cellWidth, cellHeight);
        if (_cachedMenuItemSnapshot.Length < items.Count)
        {
            int capacity = Math.Max(items.Count, _cachedMenuItemSnapshot.Length == 0 ? 8 : _cachedMenuItemSnapshot.Length * 2);
            _cachedMenuItemSnapshot = new ContextMenuItem[capacity];
        }
        for (int i = 0; i < items.Count; i++)
            _cachedMenuItemSnapshot[i] = items[i];

        _cachedMenuItemCount = items.Count;
        _cachedMenuModel = model;
        _cachedMenuItems = items;
        _cachedMenuX = model.X;
        _cachedMenuY = model.Y;
        _cachedMenuViewportWidth = viewportWidth;
        _cachedMenuViewportHeight = viewportHeight;
        _cachedMenuCellWidth = cellWidth;
        _cachedMenuCellHeight = cellHeight;
        _cachedMenuLayout = layout;
        return layout;
    }

    private void EnsureChromeScratchCapacity(int required)
    {
        if (required <= _chromeScratch.Length)
            return;

        int capacity = Math.Max(required, _chromeScratch.Length == 0 ? 64 : _chromeScratch.Length * 2);
        Array.Resize(ref _chromeScratch, capacity);
    }

    private void EnsureScrollbarScratchCapacity(int required)
    {
        if (required <= _scrollbarScratch.Length)
            return;

        int capacity = Math.Max(required, _scrollbarScratch.Length == 0 ? 16 : _scrollbarScratch.Length * 2);
        Array.Resize(ref _scrollbarScratch, capacity);
    }

    private void EnsureScratchCapacity(int required)
    {
        if (required <= _frameScratch.Length)
            return;

        int capacity = Math.Max(required, _frameScratch.Length == 0 ? 4096 : _frameScratch.Length * 2);
        Array.Resize(ref _frameScratch, capacity);
    }

}
