using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Rendering;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Scrollbar;
using Dotty.Runtime.Search;
using RuntimeSearchMatch = Dotty.Runtime.Search.SearchMatch;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Dotty.Terminal.Graphics;
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
    IReadOnlyList<RuntimeSearchMatch>? Matches,
    int QueryCursorIndex = -1);

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
    /// <summary>
    /// Chrome quads <c>[0, UnderlayChromeCount)</c> are drawn below the cell backgrounds (pane backgrounds, pane
    /// dividers and the active-pane ring); the rest of the base chrome is drawn between the background and glyph passes.
    /// </summary>
    public int UnderlayChromeCount { get; private set; }
    public int ScrollbarChromeStart { get; private set; }
    public int MenuInstanceStart { get; private set; }
    public int MenuChromeStart { get; private set; }
    public bool IsIncomplete { get; private set; }
    /// <summary>Framebuffer-pixel text caret anchor for native IME candidate UI.</summary>
    public SKRect ImeCaretBounds { get; private set; }
    public bool ImeCaretBoundsValid { get; private set; }

    public TerminalSceneFrame(
        CellInstance[] instances,
        int instanceCount,
        HashSet<int> dirtyAtlasRows,
        ChromeQuadInstance[] chromeQuads,
        int chromeQuadCount,
        int menuInstanceStart = -1,
        int menuChromeStart = -1,
        int scrollbarChromeStart = -1,
        bool isIncomplete = false,
        SKRect imeCaretBounds = default,
        bool imeCaretBoundsValid = false,
        ImageDrawList? imageDraws = null,
        int underlayChromeCount = 0)
    {
        ImageDraws = imageDraws ?? new ImageDrawList();
        Update(instances, instanceCount, dirtyAtlasRows, chromeQuads, chromeQuadCount,
            menuInstanceStart, menuChromeStart, scrollbarChromeStart, isIncomplete,
            imeCaretBounds, imeCaretBoundsValid, underlayChromeCount);
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
        bool isIncomplete,
        SKRect imeCaretBounds,
        bool imeCaretBoundsValid,
        int underlayChromeCount)
    {
        Instances = instances;
        InstanceCount = instanceCount;
        DirtyAtlasRows = dirtyAtlasRows;
        ChromeQuads = chromeQuads;
        ChromeQuadCount = chromeQuadCount;
        UnderlayChromeCount = underlayChromeCount;
        MenuInstanceStart = menuInstanceStart;
        MenuChromeStart = menuChromeStart;
        ScrollbarChromeStart = scrollbarChromeStart;
        IsIncomplete = isIncomplete;
        ImeCaretBounds = imeCaretBounds;
        ImeCaretBoundsValid = imeCaretBoundsValid;
    }


    public ReadOnlySpan<CellInstance> AsSpan() => new(Instances, 0, InstanceCount);
    public ReadOnlySpan<ChromeQuadInstance> AsChromeSpan() => new(ChromeQuads, 0, ChromeQuadCount);

    /// <summary>
    /// Image quads of the composed frame, grouped by layer. The draw list is reused frame to frame and every draw
    /// holds a pixel lease: call <see cref="ReleaseImageLeases"/> once the frame was rendered (or abandoned).
    /// </summary>
    public ImageDrawList ImageDraws { get; }

    /// <summary>
    /// Earliest animation deadline over the composed leaves, in <see cref="TimeProvider.GetTimestamp"/> ticks of
    /// <see cref="TimeProvider.System"/>; 0 = no animated image is visible.
    /// </summary>
    public long NextImageDeadlineTicks => ImageDraws.NextDeadlineTicks;

    /// <summary>Releases the pixel leases of this frame's image draws (idempotent).</summary>
    public void ReleaseImageLeases() => ImageDraws.ReleaseLeases();
}

/// <summary>
/// Composes the terminal scene into host-neutral GPU cell instances.
/// Window lifecycle, input, and OpenGL submission remain outside this class.
/// </summary>
public sealed class TerminalSceneComposer : IDisposable
{
    private SKTypeface _typeface;
    private GlyphAtlas _atlas;
    private float _fontSize;
    private CellInstance[] _frameScratch = Array.Empty<CellInstance>();
    private ChromeQuadInstance[] _chromeScratch = Array.Empty<ChromeQuadInstance>();
    private ChromeQuadInstance[] _scrollbarScratch = Array.Empty<ChromeQuadInstance>();
    private readonly HashSet<int> _dirtyAtlasRows = new();
    private TerminalSceneFrame? _cachedFrame;
    private readonly ImePreeditQuadBuilder _preeditQuadBuilder = new();
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
    private sealed class LeafRowCache
    {
        public SgrColorArgb Foreground;
        public SgrColorArgb Background;
        public bool HasColors;
        public LeafPane? Owner;
        public readonly QuadRowCache Cache = new();
    }

    private LeafRowCache[] _leafRowCaches = Array.Empty<LeafRowCache>();
    private bool _hasRowCacheConfiguration;
    private IColorScheme? _cachedRowCacheTheme;
    private GlyphAtlas? _cachedRowCacheAtlas;
    private SKTypeface? _cachedRowCacheTypeface;
    private float _cachedRowCacheFontSize;
    private int _cachedRowCacheAtlasVersion;
    private int _cachedRowCacheFramebufferWidth;
    private int _cachedRowCacheFramebufferHeight;
    private int _cachedRowCacheRows;
    private int _cachedRowCacheColumns;
    private float _cachedRowCacheCellWidth;
    private float _cachedRowCacheCellHeight;
    private float _cachedRowCacheScale;
    private double _cachedRowCachePadLeft;
    private double _cachedRowCachePadTop;
    private double _cachedRowCachePadRight;
    private double _cachedRowCachePadBottom;
    private SgrColorArgb _cachedRowCacheForeground;
    private SgrColorArgb _cachedRowCacheBackground;
    private SgrColorArgb _cachedRowCacheSelectionColor;
    private long _cachedRowHits;
    private long _cachedRowMisses;
    private long _skippedLeafFrames;
    /// <summary>Cumulative Compose calls in which at least one leaf was skipped on lock contention.</summary>
    public long SkippedLeafFrames => _skippedLeafFrames;
    public long CachedRowHits => _cachedRowHits;
    public long CachedRowMisses => _cachedRowMisses;

    /// <summary>
    /// True when the last composed frame drew a blinking cursor (DECSCUSR blink bit, or <c>cursor.blink</c> while the
    /// application keeps the default style) or visible SGR 5/6 text. The host runs the shared blink timer only while
    /// this is set.
    /// </summary>
    public bool BlinkActive { get; private set; }

    /// <summary>Faint opacity and blink phase of the current Compose call (see <see cref="QuadRowCache.ApplyTextOptions"/>).</summary>
    private TextAttributeOptions _textOptions = TextAttributeOptions.Default;

    private readonly ImageDrawList _imageDraws = new();

    /// <summary>Scratch output of <see cref="PlaceholderResolver"/>: filled and drained within one leaf, never holds pixels across frames.</summary>
    private readonly List<KittyRenderPlacement> _placeholderQuads = new();

    /// <summary>Per-cell "has an instance" scratch for the selection overlay; grows, never shrinks.</summary>
    private bool[] _selectionCoverage = Array.Empty<bool>();

    /// <summary>A leaf's effective default colours for one frame: OSC 10/11 overrides, then the DECSCNM swap.</summary>
    private readonly record struct LeafColors(SgrColorArgb Foreground, SgrColorArgb Background);

    /// <summary>Per-leaf colours resolved once per frame (index = leaf index); grows, never shrinks.</summary>
    private LeafColors[] _leafColors = Array.Empty<LeafColors>();

    /// <summary>
    /// Test seam: invoked for every captured leaf snapshot (no buffer lock held) before its rows and images are
    /// composed, so tests can attach synthetic graphics (<c>BeginGraphics</c>/<c>AttachGraphics</c>).
    /// </summary>
    internal Action<LeafPane, RenderSnapshot>? LeafSnapshotCaptured { get; set; }

    /// <summary>Releases the pixel leases held by the last composed frame's image draw list.</summary>
    public void Dispose() => _imageDraws.Clear();

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
        bool statusWarning = false,
        float captionButtonsWidth = 0f,
        bool isMaximized = false,
        ImePreeditState? preeditState = null,
        bool windowFocused = true)
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
        SKRect imeCaretBounds = default;
        bool imeCaretBoundsValid = false;
        _dirtyAtlasRows.Clear();
        BlinkActive = false;
        // cursorVisible is the shared blink phase, so SGR 5/6 text blinks in step with the cursor.
        _textOptions = new TextAttributeOptions(
            TextAttributeOptions.NormalizeFaintOpacity(UserConfigService.Current.Font.FaintOpacity), cursorVisible);
        // The previous frame has been consumed by now: drop its pixel leases before taking new ones.
        _imageDraws.Clear();
        int atlasVersionAtStart = _atlas.ContentVersion;
        ConfigureRowCaches(theme, themeForeground, new SgrColorArgb(theme.Background), selectionColor,
            framebufferWidth, framebufferHeight, rows, columns, cellWidth, cellHeight, scale, padding, leaves.Count, atlasVersionAtStart);

        float terminalWidth = Math.Max(10f, framebufferWidth - padX);
        float terminalHeight = Math.Max(10f, framebufferHeight - topOffset - padY);
        var panes = UserConfigService.Current.Panes;
        float dividerThickness = PaneDecorationBuilder.ResolveThickness(panes.DividerThickness, scale);
        activeTab.PaneTree.Layout(terminalWidth, terminalHeight, cellWidth * scale, cellHeight * scale, dividerThickness);

        // Underlay chrome (drawn below the cell backgrounds): pane backgrounds that differ from the window
        // background (OSC 11, DECSCNM), then dividers, then the active-pane ring.
        if (_leafColors.Length < leaves.Count)
            Array.Resize(ref _leafColors, Math.Max(leaves.Count, Math.Max(2, _leafColors.Length * 2)));
        EnsureChromeScratchCapacity(leaves.Count + PaneDecorationBuilder.MaxQuadCount(leaves.Count));
        var themeBackground = new SgrColorArgb(theme.Background);
        float gridCellWidth = cellWidth * scale;
        float gridCellHeight = cellHeight * scale;
        for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
        {
            var leaf = leaves[leafIndex];
            var colors = ResolveLeafColors(leaf.Session.Adapter, themeForeground, themeBackground);
            _leafColors[leafIndex] = colors;
            if ((colors.Background.Argb & 0x00FFFFFFu) != (themeBackground.Argb & 0x00FFFFFFu))
                _chromeScratch[chromeQuadCount++] = MakePaneBackgroundQuad(
                    leaf, colors.Background, padLeft, padTop + topOffset, gridCellWidth, gridCellHeight);
        }

        var chromePalette = ChromeStyleUtils.ResolvePalette(theme);
        chromeQuadCount += PaneDecorationBuilder.Build(
            activeTab.PaneTree.Root,
            activePane.Bounds,
            padLeft,
            padTop + topOffset,
            terminalWidth,
            terminalHeight,
            dividerThickness,
            panes.ActiveBorder,
            chromePalette.Border,
            chromePalette.Accent,
            _chromeScratch.AsSpan(chromeQuadCount));
        int underlayChromeCount = chromeQuadCount;


        for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
        {
            var leaf = leaves[leafIndex];
            if (leaf.Columns > 0 && leaf.Rows > 0 &&
                (leaf.Session.Adapter.Buffer.Columns != leaf.Columns || leaf.Session.Adapter.Buffer.Rows != leaf.Rows))
            {
                leaf.Session.Resize(leaf.Columns, leaf.Rows, leaf.PixelWidth, leaf.PixelHeight);
            }

            // A resize can shrink the history (reflow, or a smaller byte-capped capacity): a stale offset past the
            // oldest line would make the snapshot wrap around the ring, so pull it back before capturing.
            if (leaf.ScrollOffset > 0)
                leaf.ScrollTo(leaf.ScrollOffset, leaf.Session.Adapter.Buffer.ScrollbackCount);

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
                LeafSnapshotCaptured?.Invoke(leaf, leafSnapshot);
                int startInstanceIndex = instanceCount;
                int paneRows = leafSnapshot.Rows;
                int paneColumns = leafSnapshot.Columns;
                EnsureScratchCapacity(instanceCount + checked(paneRows * paneColumns * 2 + 1024));
                var leafForeground = _leafColors[leafIndex].Foreground;
                var leafBackground = _leafColors[leafIndex].Background;
                int written = AppendCachedLeafRows(
                    leafIndex, leaf, leafSnapshot, _frameScratch.AsSpan(startInstanceIndex),
                    paneRows, paneColumns, leafForeground, leafBackground);
                int startColumnOffset = (int)Math.Round(leaf.Bounds.X / (cellWidth * scale));
                int startRowOffset = (int)Math.Round(leaf.Bounds.Y / (cellHeight * scale)) + barRows;
                for (int i = 0; i < written; i++)
                {
                    ref var instance = ref _frameScratch[startInstanceIndex + i];
                    instance.Col += (ushort)startColumnOffset;
                    instance.Row += (ushort)startRowOffset;
                }
                instanceCount += written;

                if (leafSnapshot.Graphics is { } graphics)
                {
                    long animationDeadline = graphics.NextAnimationDeadlineTicks;
                    if (animationDeadline != 0
                        && (_imageDraws.NextDeadlineTicks == 0 || animationDeadline < _imageDraws.NextDeadlineTicks))
                        _imageDraws.NextDeadlineTicks = animationDeadline;

                    bool hasPlaceholders = graphics.Virtuals.Length > 0;
                    if (graphics.Placements.Length > 0 || hasPlaceholders)
                    {
                        // startRowOffset already contains the tab-bar rows, so padTop is NOT combined with
                        // topOffset here (the cell pass uses the same origin: padTop + row * cellH).
                        float imageCellWidth = cellWidth * scale;
                        float imageCellHeight = cellHeight * scale;
                        float gridLeft = padLeft + startColumnOffset * imageCellWidth;
                        float gridTop = padTop + startRowOffset * imageCellHeight;
                        var imageGeometry = new ImageSceneGeometry(
                            padLeft, padTop, startColumnOffset, startRowOffset, imageCellWidth, imageCellHeight,
                            ClipLeft: Math.Max(0f, gridLeft),
                            ClipTop: Math.Max(0f, gridTop),
                            ClipRight: Math.Min((float)framebufferWidth, gridLeft + paneColumns * imageCellWidth),
                            ClipBottom: Math.Min((float)framebufferHeight, gridTop + paneRows * imageCellHeight));
                        if (graphics.Placements.Length > 0)
                            ImageQuadBuilder.Append(_imageDraws, graphics.Placements, in imageGeometry);
                        if (hasPlaceholders)
                        {
                            // Outside the buffer lock, on the snapshot copy; the quads borrow pixels the snapshot's
                            // leases keep alive and the builder retains its own.
                            _placeholderQuads.Clear();
                            PlaceholderResolver.Resolve(leafSnapshot, graphics, imageCellWidth, imageCellHeight, _placeholderQuads);
                            if (_placeholderQuads.Count > 0)
                                ImageQuadBuilder.Append(_imageDraws, CollectionsMarshal.AsSpan(_placeholderQuads), in imageGeometry);
                            _placeholderQuads.Clear();
                        }
                    }
                }

                // Content-anchored: the selection is converted against the rows this snapshot captured.
                if (leaf.Selection.HasSelection
                    && leaf.Selection.TryGetRange(leafSnapshot.ViewTopAbsoluteRow, out var range, out bool blockSelection))
                {
                    byte selectionAlpha = (byte)((selectionColor.A != 0 && selectionColor.A != 255) ? selectionColor.A : 128);
                    int coverageLength = SelectionQuadBuilder.CoverageLength(range, paneRows, paneColumns, scrollOffset);
                    EnsureScratchCapacity(instanceCount + coverageLength);
                    if (_selectionCoverage.Length < coverageLength)
                        _selectionCoverage = new bool[Math.Max(coverageLength, _selectionCoverage.Length * 2)];
                    instanceCount += SelectionQuadBuilder.Apply(
                        _frameScratch.AsSpan(startInstanceIndex),
                        written,
                        range,
                        blockSelection,
                        paneRows,
                        paneColumns,
                        scrollOffset,
                        startColumnOffset,
                        startRowOffset,
                        selectionColor,
                        selectionAlpha,
                        leafForeground,
                        _selectionCoverage);
                }

                // cursorVisible is the shared blink phase: it hides only a blinking cursor in its off phase.
                if (leafSnapshot.CursorVisible && scrollOffset == 0 && ReferenceEquals(leaf, activePane)
                    && leafSnapshot.CursorRow >= 0 && leafSnapshot.CursorRow < paneRows
                    && leafSnapshot.CursorCol >= 0 && leafSnapshot.CursorCol < paneColumns)
                {
                    var cursorConfig = UserConfigService.Current.Cursor;
                    var (cursorShape, cursorBlinks) = CursorGeometry.ResolveStyle(
                        leafSnapshot.CursorStyleIsDefault, leafSnapshot.CursorShape, leafSnapshot.CursorBlinking,
                        CursorGeometry.ParseShape(cursorConfig.Shape), cursorConfig.Blink);
                    if (cursorBlinks)
                        BlinkActive = true;

                    // A continuation cell resolves to its wide lead, and the cursor then spans both columns.
                    ReadOnlySpan<CellHot> cursorCells = leafSnapshot.GetRowCells(leafSnapshot.CursorRow);
                    int leadColumn = leafSnapshot.CursorCol;
                    if (leadColumn > 0 && leadColumn < cursorCells.Length && cursorCells[leadColumn].IsContinuation)
                        leadColumn--;
                    int cursorSpan = leadColumn + 1 < paneColumns && leadColumn < cursorCells.Length
                        && cursorCells[leadColumn].Width == 2 ? 2 : 1;
                    int cursorRow = leafSnapshot.CursorRow + startRowOffset;
                    int cursorColumn = leadColumn + startColumnOffset;
                    float cursorCellWidth = cellWidth * scale;
                    float cursorCellHeight = cellHeight * scale;
                    CursorPlan cursorPlan = CursorGeometry.Plan(
                        cursorShape,
                        windowFocused,
                        CursorGeometry.ParseUnfocusedStyle(cursorConfig.UnfocusedStyle),
                        visible: !cursorBlinks || cursorVisible,
                        padLeft + cursorColumn * cursorCellWidth,
                        padTop + cursorRow * cursorCellHeight,
                        cursorCellWidth,
                        cursorCellHeight,
                        cursorSpan,
                        scale);
                    if (cursorPlan.RectCount > 0)
                    {
                        // Base chrome is drawn between cell backgrounds and glyphs, so the text stays on top.
                        EnsureChromeScratchCapacity(chromeQuadCount + cursorPlan.RectCount);
                        for (int i = 0; i < cursorPlan.RectCount; i++)
                            _chromeScratch[chromeQuadCount++] = MakeSolidQuad(cursorPlan[i], leafForeground);
                    }
                    if (cursorPlan.RecolorsText)
                    {
                        var textMode = CursorGeometry.ParseTextMode(cursorConfig.Text);
                        for (int i = startInstanceIndex; i < instanceCount; i++)
                        {
                            ref var instance = ref _frameScratch[i];
                            if (instance.Row != cursorRow || instance.Col != cursorColumn)
                                continue;

                            var cellForeground = SgrColorArgb.FromRgb(instance.FgR, instance.FgG, instance.FgB);
                            var cellBackground = instance.BgA != 0
                                ? SgrColorArgb.FromRgb(instance.BgR, instance.BgG, instance.BgB)
                                : leafBackground;
                            var text = CursorGeometry.ResolveTextColor(textMode, cellForeground, cellBackground, leafForeground);
                            instance.FgR = text.R;
                            instance.FgG = text.G;
                            instance.FgB = text.B;
                            break;
                        }
                    }
                }
                if (ReferenceEquals(leaf, activePane) && scrollOffset == 0 &&
                    leafSnapshot.CursorRow >= 0 && leafSnapshot.CursorRow < paneRows &&
                    leafSnapshot.CursorCol >= 0 && leafSnapshot.CursorCol < paneColumns &&
                    (activeContextMenu == null || !activeContextMenu.IsVisible))
                {
                    int cursorGlobalRow = leafSnapshot.CursorRow + startRowOffset;
                    int cursorGlobalColumn = leafSnapshot.CursorCol + startColumnOffset;
                    float scaledCellWidth = cellWidth * scale;
                    float scaledCellHeight = cellHeight * scale;
                    float paneLeft = padLeft + leaf.Bounds.X;
                    float paneTop = padTop + topOffset + leaf.Bounds.Y;
                    float paneRight = paneLeft + leaf.Bounds.Width;
                    float paneBottom = paneTop + leaf.Bounds.Height;
                    float cursorX = padLeft + cursorGlobalColumn * scaledCellWidth;
                    float cursorY = padTop + cursorGlobalRow * scaledCellHeight;
                    if (cursorX >= paneLeft && cursorX <= paneRight && cursorY >= paneTop && cursorY + scaledCellHeight <= paneBottom &&
                        cursorX >= 0 && cursorX <= framebufferWidth && cursorY >= 0 && cursorY + scaledCellHeight <= framebufferHeight)
                    {
                        imeCaretBounds = new SKRect(cursorX, cursorY, cursorX + Math.Min(2f, scaledCellWidth), cursorY + scaledCellHeight);
                        imeCaretBoundsValid = true;
                        if (preeditState is { IsActive: true } composition && !searchOverlay.IsActive)
                        {
                            int availableColumns = Math.Min(paneColumns - leafSnapshot.CursorCol,
                                Math.Max(0, columns - cursorGlobalColumn));
                            if (availableColumns > 0)
                            {
                                EnsureScratchCapacity(checked(instanceCount + composition.Text.Length));
                                int preeditCount = _preeditQuadBuilder.Build(
                                    in composition, cursorGlobalColumn, cursorGlobalRow, availableColumns,
                                    _frameScratch.AsSpan(instanceCount), _atlas, _typeface, _fontSize,
                                    _dirtyAtlasRows, out int caretOffset);
                                instanceCount += preeditCount;
                                float caretLeft = cursorX + caretOffset * scaledCellWidth;
                                float caretRight = Math.Min(caretLeft + 2f, paneRight);
                                if (caretLeft >= paneLeft && caretLeft < paneRight && caretRight > caretLeft)
                                {
                                    imeCaretBounds = new SKRect(caretLeft, cursorY, caretRight, cursorY + scaledCellHeight);
                                    imeCaretBoundsValid = true;
                                    EnsureChromeScratchCapacity(chromeQuadCount + 1);
                                    _chromeScratch[chromeQuadCount++] = MakeImeCaretQuad(imeCaretBounds, themeForeground);
                                }
                            }
                        }
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
        _atlas.PublishPendingGlyphs();
        if (_atlas.ContentVersion != atlasVersionAtStart)
            InvalidateLeafRowCaches();

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
                statusWarning,
                captionButtonsWidth,
                isMaximized);
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
            if (searchOverlay.QueryCursorIndex >= 0 && (activeContextMenu == null || !activeContextMenu.IsVisible))
            {
                float scaledWidth = cellWidth * scale;
                float scaledHeight = cellHeight * scale;
                int inputStart = (int)(overlayLayout.InputBoxRect.X / scaledWidth);
                int inputRow = (int)(overlayLayout.InputBoxRect.Y / scaledHeight);
                int inputColumns = Math.Max(0, (int)(overlayLayout.InputBoxRect.Width / scaledWidth));
                int queryCursor = Math.Clamp(searchOverlay.QueryCursorIndex, 0, Math.Min(searchOverlay.Query.Length, inputColumns));
                int caretColumn = inputStart + queryCursor;
                float caretX = caretColumn * scaledWidth;
                float caretY = inputRow * scaledHeight;
                float inputRight = (inputStart + inputColumns) * scaledWidth;
                if (caretColumn >= 0 && caretColumn <= columns && caretX <= inputRight && caretY >= 0 && caretY + scaledHeight <= framebufferHeight)
                {
                    imeCaretBounds = new SKRect(caretX, caretY, Math.Min(caretX + 2f, inputRight), caretY + scaledHeight);
                    imeCaretBoundsValid = imeCaretBounds.Width > 0;
                    if (imeCaretBoundsValid && preeditState is { IsActive: true } composition && queryCursor < inputColumns)
                    {
                        int visibleColumns = Math.Min(inputColumns - queryCursor, Math.Max(0, columns - caretColumn));
                        EnsureScratchCapacity(checked(instanceCount + composition.Text.Length));
                        int preeditCount = _preeditQuadBuilder.Build(
                            in composition, caretColumn, inputRow, visibleColumns,
                            _frameScratch.AsSpan(instanceCount), _atlas, _typeface, _fontSize,
                            _dirtyAtlasRows, out int caretOffset);
                        instanceCount += preeditCount;
                        float compositionCaretX = caretX + caretOffset * scaledWidth;
                        float caretRight = Math.Min(compositionCaretX + 2f, inputRight);
                        if (compositionCaretX < inputRight && caretRight > compositionCaretX)
                        {
                            imeCaretBounds = new SKRect(compositionCaretX, caretY, caretRight, caretY + scaledHeight);
                            EnsureChromeScratchCapacity(chromeQuadCount + 1);
                            _chromeScratch[chromeQuadCount++] = MakeImeCaretQuad(imeCaretBounds, themeForeground);
                        }
                    }
                }
                else
                {
                    imeCaretBoundsValid = false;
                }
            }
            else
            {
                imeCaretBoundsValid = false;
            }
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
                scrollbarChromeStart, skippedLeaf, imeCaretBounds, imeCaretBoundsValid, _imageDraws,
                underlayChromeCount);
        }
        else
        {
            _cachedFrame.Update(
                _frameScratch, instanceCount, _dirtyAtlasRows, _chromeScratch,
                chromeQuadCount, menuInstanceStart, menuChromeStart,
                scrollbarChromeStart, skippedLeaf, imeCaretBounds, imeCaretBoundsValid, underlayChromeCount);
        }
        return _cachedFrame;
    }

    private static LeafColors ResolveLeafColors(TerminalAdapter adapter, SgrColorArgb themeForeground, SgrColorArgb themeBackground)
    {
        var foreground = adapter.DefaultForegroundOverrideArgb is uint foregroundOverride
            ? new SgrColorArgb(foregroundOverride) : themeForeground;
        var background = adapter.DefaultBackgroundOverrideArgb is uint backgroundOverride
            ? new SgrColorArgb(backgroundOverride) : themeBackground;
        return adapter.ReverseVideo ? new LeafColors(background, foreground) : new LeafColors(foreground, background);
    }

    /// <summary>
    /// Opaque quad painting a pane's effective default background below its cells (default-background cells have
    /// no background of their own). Covers the pane rect and its cell grid, which snaps to whole cells and can start
    /// up to half a cell before the rect; the window padding keeps the theme background.
    /// </summary>
    private static ChromeQuadInstance MakePaneBackgroundQuad(
        LeafPane leaf, SgrColorArgb color, float originX, float originY, float cellWidth, float cellHeight)
    {
        var bounds = leaf.Bounds;
        float gridLeft = originX + (int)Math.Round(bounds.X / cellWidth) * cellWidth;
        float gridTop = originY + (int)Math.Round(bounds.Y / cellHeight) * cellHeight;
        float left = MathF.Floor(Math.Min(originX + bounds.X, gridLeft) + 0.5f);
        float top = MathF.Floor(Math.Min(originY + bounds.Y, gridTop) + 0.5f);
        float right = MathF.Floor(Math.Max(originX + bounds.Right, gridLeft + leaf.Columns * cellWidth) + 0.5f);
        float bottom = MathF.Floor(Math.Max(originY + bounds.Bottom, gridTop + leaf.Rows * cellHeight) + 0.5f);
        float r = color.R / 255f;
        float g = color.G / 255f;
        float b = color.B / 255f;
        return new ChromeQuadInstance
        {
            X = left,
            Y = top,
            W = Math.Max(0f, right - left),
            H = Math.Max(0f, bottom - top),
            TopR = r,
            TopG = g,
            TopB = b,
            TopA = 1f,
            BottomR = r,
            BottomG = g,
            BottomB = b,
            BottomA = 1f
        };
    }
    private static ChromeQuadInstance MakeImeCaretQuad(SKRect rect, SgrColorArgb color)
        => MakeSolidQuad(new CursorRect(rect.Left, rect.Top, rect.Width, rect.Height), color);

    /// <summary>Opaque chrome quad; chrome colours are 0-1 floats (straight alpha).</summary>
    private static ChromeQuadInstance MakeSolidQuad(CursorRect rect, SgrColorArgb color)
    {
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        return new ChromeQuadInstance
        {
            X = rect.X,
            Y = rect.Y,
            W = rect.Width,
            H = rect.Height,
            TopR = r,
            TopG = g,
            TopB = b,
            TopA = 1f,
            BottomR = r,
            BottomG = g,
            BottomB = b,
            BottomA = 1f
        };
    }
    private void ConfigureRowCaches(
        IColorScheme theme,
        SgrColorArgb foreground,
        SgrColorArgb background,
        SgrColorArgb selection,
        int framebufferWidth,
        int framebufferHeight,
        int rows,
        int columns,
        float cellWidth,
        float cellHeight,
        float scale,
        PaddingUserConfig padding,
        int leafCount,
        int atlasVersion)
    {
        if (_leafRowCaches.Length < leafCount)
            Array.Resize(ref _leafRowCaches, Math.Max(leafCount, Math.Max(2, _leafRowCaches.Length * 2)));

        bool changed = !_hasRowCacheConfiguration
            || !ReferenceEquals(_cachedRowCacheTheme, theme)
            || !ReferenceEquals(_cachedRowCacheAtlas, _atlas)
            || !ReferenceEquals(_cachedRowCacheTypeface, _typeface)
            || !_cachedRowCacheFontSize.Equals(_fontSize)
            || _cachedRowCacheAtlasVersion != atlasVersion
            || _cachedRowCacheFramebufferWidth != framebufferWidth
            || _cachedRowCacheFramebufferHeight != framebufferHeight
            || _cachedRowCacheRows != rows
            || _cachedRowCacheColumns != columns
            || !_cachedRowCacheCellWidth.Equals(cellWidth)
            || !_cachedRowCacheCellHeight.Equals(cellHeight)
            || !_cachedRowCacheScale.Equals(scale)
            || _cachedRowCachePadLeft != padding.Left
            || _cachedRowCachePadTop != padding.Top
            || _cachedRowCachePadRight != padding.Right
            || _cachedRowCachePadBottom != padding.Bottom
            || _cachedRowCacheForeground != foreground
            || _cachedRowCacheBackground != background
            || _cachedRowCacheSelectionColor != selection;
        for (int i = 0; i < leafCount; i++)
        {
            LeafRowCache slot = _leafRowCaches[i] ??= new LeafRowCache();
            if (changed)
                slot.Cache.InvalidateAll();
        }

        _hasRowCacheConfiguration = true;
        _cachedRowCacheTheme = theme;
        _cachedRowCacheAtlas = _atlas;
        _cachedRowCacheTypeface = _typeface;
        _cachedRowCacheFontSize = _fontSize;
        _cachedRowCacheAtlasVersion = atlasVersion;
        _cachedRowCacheFramebufferWidth = framebufferWidth;
        _cachedRowCacheFramebufferHeight = framebufferHeight;
        _cachedRowCacheRows = rows;
        _cachedRowCacheColumns = columns;
        _cachedRowCacheCellWidth = cellWidth;
        _cachedRowCacheCellHeight = cellHeight;
        _cachedRowCacheScale = scale;
        _cachedRowCachePadLeft = padding.Left;
        _cachedRowCachePadTop = padding.Top;
        _cachedRowCachePadRight = padding.Right;
        _cachedRowCachePadBottom = padding.Bottom;
        _cachedRowCacheForeground = foreground;
        _cachedRowCacheBackground = background;
        _cachedRowCacheSelectionColor = selection;
    }

    private int AppendCachedLeafRows(
        int leafIndex,
        LeafPane leaf,
        RenderSnapshot snapshot,
        Span<CellInstance> destination,
        int rows,
        int columns,
        SgrColorArgb foreground,
        SgrColorArgb background)
    {
        var slot = _leafRowCaches[leafIndex];
        if (!ReferenceEquals(slot.Owner, leaf))
        {
            slot.Owner = leaf;
            slot.Cache.InvalidateAll();
        }
        if (!slot.HasColors || slot.Foreground != foreground || slot.Background != background)
        {
            slot.Cache.InvalidateAll();
            slot.Foreground = foreground;
            slot.Background = background;
            slot.HasColors = true;
        }

        var cache = slot.Cache;
        cache.EnsureGeometry(rows, columns, 0f, 0f);
        cache.ApplyTextOptions(in _textOptions);
        cache.BeginFrame();

        for (int row = 0; row < rows; row++)
        {
            ReadOnlySpan<CellHot> hot = snapshot.GetRowCells(row);
            ReadOnlySpan<ColdCell> cold = snapshot.GetRowColdCells(row);
            // Scrolls bump generations region-wide; physical row identity follows the content through ring rotation.
            int rowIdentity = snapshot.RowMap[row];
            for (int source = 0; source < rows; source++)
            {
                ref readonly var previous = ref cache.GetPreviousEntryRef(source);
                if (!previous.Valid
                    || previous.RowIdentity != rowIdentity
                    || previous.SourceCells.Length < columns
                    || previous.SourceColdCells.Length < columns
                    || !hot.SequenceEqual(previous.SourceCells.AsSpan(0, columns))
                    || !cold.SequenceEqual(previous.SourceColdCells.AsSpan(0, columns)))
                    continue;

                if (cache.TryMapRow(row, source))
                    break;
            }
        }

        cache.ApplyMappings();
        for (int row = 0; row < rows; row++)
        {
            ref var entry = ref cache.GetEntryRef(row);
            for (int i = 0; i < entry.InstanceCount; i++)
                entry.Instances[i].Row = (ushort)row;
            entry.RowIdentity = snapshot.RowMap[row];
            entry.Generation = snapshot.RowGenerations[row];
        }

        int written = 0;
        for (int row = 0; row < rows; row++)
        {
            ref var entry = ref cache.GetEntryRef(row);
            if (!entry.Valid)
            {
                ReadOnlySpan<CellHot> hot = snapshot.GetRowCells(row);
                ReadOnlySpan<ColdCell> cold = snapshot.GetRowColdCells(row);
                cache.EnsureEntryCapacity(ref entry, columns);
                int count = QuadFrameBuilder.BuildRow(
                    snapshot, row, _atlas, _typeface, _fontSize,
                    entry.Instances.AsSpan(), _dirtyAtlasRows, columns, foreground, background,
                    in _textOptions, out bool hasBlinkingText);
                entry.HasBlinkingText = hasBlinkingText;
                hot.CopyTo(entry.SourceCells);
                cold.CopyTo(entry.SourceColdCells);
                entry.InstanceCount = count;
                entry.RowIdentity = snapshot.RowMap[row];
                entry.Generation = snapshot.RowGenerations[row];
                entry.Valid = true;
                _cachedRowMisses++;
            }
            else
            {
                entry.RowIdentity = snapshot.RowMap[row];
                entry.Generation = snapshot.RowGenerations[row];
                _cachedRowHits++;
            }
            entry.Instances.AsSpan(0, entry.InstanceCount).CopyTo(destination[written..]);
            written += entry.InstanceCount;
            if (entry.HasBlinkingText)
                BlinkActive = true;
        }
        return written;
    }

    private void InvalidateLeafRowCaches()
    {
        for (int i = 0; i < _leafRowCaches.Length; i++)
            _leafRowCaches[i]?.Cache.InvalidateAll();
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
