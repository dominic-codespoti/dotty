using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Text;
using Dotty.Runtime.Scripting;
using Dotty.Silk.Config;
using Dotty.Silk.Input;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using InputKey = Silk.NET.Input.Key;
namespace Dotty.Silk;

internal static class DottyWindowHost
{
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static GlyphAtlas _atlas = null!;
    private static SKTypeface _typeface = null!;
    private static float _cellWidth, _cellHeight, _scale = 1f;
    private const float MinRuntimeFontSize = 6f;
    private const float MaxRuntimeFontSize = 72f;
    private static double? _runtimeFontSize;
    private static double _configuredFontSize = 14d;
    private static WindowState _previousNonFullscreenState = WindowState.Normal;
    private static int _cols = 80, _rows = 24;
    private static SilkTerminalRenderer _renderer = null!;
    private static IColorScheme _activeTheme = null!;
    private static SgrColorArgb _themeForeground;
    private static SgrColorArgb _themeBackground;
    private static SgrColorArgb _themeSelectionColor;
    private static TerminalTabManager _tabManager = null!;
    private static LuaScriptHost _luaHost = null!;
    private static readonly KeybindingManager _keybindings = new();

    private static IInputContext _input = null!;
    private static IKeyboard? _keyboard;
    private static ITerminalClipboard? _clipboard;
    private static IMouse? _mouse;
    private static TerminalKeyboardController _keyboardController = null!;
    private static TerminalKeyboardDispatcher _keyboardDispatcher = null!;
    private static TerminalMouseController _mouseController = null!;
    private static TerminalSceneComposer _sceneComposer = null!;
    private static MouseHost _mouseHost = null!;

    private static bool _closed;
    private static bool? _lastWindowFocus;
    private static LeafPane? _focusedPane;
    private static readonly Action<bool> _focusStateHandler = ApplyWindowFocusState;
    private static readonly Queue<string> _pendingTitles = new(8);
    private static readonly object _pendingTitlesLock = new();
    private static int _pendingTitleCount;
    private readonly record struct ClipboardWrite(TerminalSession Session, string Text);
    private static readonly Queue<ClipboardWrite> _pendingClipboards = new(8);
    private static int _pendingClipboardCount;
    private static readonly object _pendingClipboardsLock = new();
    private readonly record struct TabTitleChange(TerminalTab Tab);
    private static readonly Queue<TabTitleChange> _pendingTabTitleChanges = new(8);
    private static int _pendingTabTitleChangeCount;
    private static readonly object _pendingTabTitleChangesLock = new();
    private readonly record struct ProcessExit(TerminalTab Tab, LeafPane Leaf);
    private static readonly Queue<ProcessExit> _pendingProcessExits = new(8);
    private static int _pendingProcessExitCount;
    private static readonly object _pendingProcessExitsLock = new();
    private static readonly ConcurrentQueue<ControlRequest> _pendingControlCommands = new();
    private static WindowLifecycleCoordinator _lifecycle = new();
    private static DesktopControlServer? _controlServer;
    private static readonly Dictionary<TerminalSession, Action> _renderSubscriptions = new();
    private static readonly Dictionary<TerminalSession, Action> _renderCallbackCache = new();
    private static readonly Dictionary<TerminalSession, Action<string>> _clipboardSubscriptions = new();
    private static readonly Dictionary<TerminalTab, (Action TopologyChanged, Action<LeafPane, LeafPane> ActivePaneChanged)> _paneSubscriptions = new();
    private sealed class TabTitleCache
    {
        public string RawTitle;
        public int Index;
        public bool IsActive;
        public readonly ReusableTextBuffer FormattedTitle = new();
        public bool HasFormattedTitle;
        public TabTitleCache(string rawTitle, int index, bool isActive)
        {
            RawTitle = rawTitle;
            Index = index;
            IsActive = isActive;
        }
    }

    private static readonly Dictionary<TerminalTab, TabTitleCache> _luaTabTitleCache = new();
    private sealed class HostTabTitleSource : ITabTitleSource
    {
        public ReadOnlySpan<char> GetTitle(TerminalTab tab, int index)
        {
            var cache = GetOrRefreshTabTitle(tab, index);
            return cache.HasFormattedTitle
                ? cache.FormattedTitle.Span
                : (tab.Title ?? "Terminal").AsSpan();
        }
    }

    private static readonly ITabTitleSource _tabTitleSource = new HostTabTitleSource();
    private static readonly ReusableTextBuffer _luaStatusText = new();
    private static readonly ReusableTextBuffer _previousLuaStatusText = new();
    private static readonly ReusableTextBuffer _luaStatusWarningText = new(128);
    private static string? _lastLuaError;
    private static bool _luaStatusWarning;
    private static bool _cursorBlinkVisible = true;
    private static long _lastCursorBlinkTimestampMs;
    private static long _lastLuaStatusRefreshTimestampMs;
    private static LeafPane[] _visibleLeaves = Array.Empty<LeafPane>();
    private static int _visibleLeafCount;
    private static readonly HashSet<TerminalSession> _sessionScratch = new();
    private static readonly HashSet<TerminalSession> _visibleSessionScratch = new();
    private static readonly HashSet<LeafPane> _visibleLeafScratch = new();
    private static readonly List<TerminalSession> _staleSessionScratch = new();
    private static readonly List<LeafPane> _staleLeafScratch = new();
    private static readonly Dictionary<LeafPane, ulong> _committedGenerations = new();
    private static ulong[] _frameGenerations = Array.Empty<ulong>();
    private static bool[] _frameGenerationValid = Array.Empty<bool>();
    private static int _committedFramebufferWidth = -1;
    private static int _committedFramebufferHeight = -1;
    private static int _committedAtlasVersion = int.MinValue;
    private static long _lastPresentTimestampMs;
    private static readonly ReusableTextBuffer _lastWindowTitle = new(128);
    private static char[] _windowTitleScratch = new char[128];
    private static byte[] _windowTitleUtf8 = new byte[128];
    private static global::Silk.NET.GLFW.Glfw? _glfwApi;
    private static nint _glfwSetWindowTitleProc;
    private static bool _glfwTitleProcResolved;
    /// <summary>
    /// Idle-frame throttle (ms). The Silk render loop is unthrottled and clean
    /// frames skip SwapBuffers — without this the loop spins at 100% of one core
    /// when idle. Kept at 1ms: a larger sleep risks pushing presents across
    /// vblank boundaries during interactive bursts (observed as typing stutter
    /// at 4ms); 1ms holds idle near ~3% with no visible hitch risk. Deliberately
    /// unconditional — gating the sleep on recent-frame history adds pacing
    /// state to the hottest path for little observable benefit.
    /// </summary>
    private const int IdleFrameSleepMs = 1;
    /// <summary>
    /// While a PTY consumer has queued output, coalesce repaint requests to
    /// roughly 30 FPS. Interactive writes have no backlog by the time they
    /// request a frame, so they retain normal latency.
    /// </summary>
    private const int BackloggedFrameIntervalMs = 30;

    private static bool _showTabBar = true;
    private static ContextMenuModel? _activeContextMenu;
    private sealed record ControlRequest(string Command, TaskCompletionSource<string> Completion);
    public static void Run()
    {
        _closed = false;
        _lastWindowFocus = null;
        _focusedPane = null;
        _lastPresentTimestampMs = 0;
        _lifecycle = new WindowLifecycleCoordinator();
        // Select GLFW directly instead of Silk's reflection-based backend discovery.
        global::Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
        InputWindowExtensions.ShouldLoadFirstPartyPlatforms(false);
        global::Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var options = WindowOptions.Default with
        {
            Title = "Dotty (Silk)",
            VSync = false,
            ShouldSwapAutomatically = false,
            API = new global::Silk.NET.Windowing.GraphicsAPI(
                global::Silk.NET.Windowing.ContextAPI.OpenGL,
                global::Silk.NET.Windowing.ContextProfile.Core,
                global::Silk.NET.Windowing.ContextFlags.Default,
                new global::Silk.NET.Windowing.APIVersion(3, 3)),
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.FocusChanged += OnWindowFocusChanged;
        _window.Closing += OnClosing;
        _window.Run();
    }

    private static void OnLoad()
    {
        try
        {
            OnLoadCore();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(GraphicsCapabilities.DescribeInitializationFailure(exception));
            _window.Close();
        }
    }

    private static void OnLoadCore()
    {
        _runtimeFontSize = null;
        WindowPresentationGate.Invalidate(WindowFrameReason.Initial);
        _lastCursorBlinkTimestampMs = GetClockMilliseconds();
        UserConfigService.CallbackDispatcher = action => _lifecycle.TryEnqueue(action);
        UserConfigService.ConfigChanged += OnConfigChanged;
        UserConfigService.Load();
        _tabManager = new TerminalTabManager();
        _tabManager.ProcessExited += OnTabProcessExited;
        _tabManager.ActiveTabChanged += OnActiveTabChanged;
        _tabManager.TabAdded += OnTabAdded;
        _tabManager.TabClosed += OnTabClosed;
        _tabManager.TabTitleChanged += OnTabTitleChanged;
        var luaServices = new DottyLuaServices(
            action => { _lifecycle.TryEnqueue(action); },
            CreateLuaTab,
            SplitLuaPane,
            action => _keyboardDispatcher?.Actions.TryExecute(action) ?? false,
            () => ApplyConfigOnly(UserConfigService.Current),
            InvalidateLuaPresentation);
        _luaHost = new LuaScriptHost(luaServices, _tabManager);
        _luaHost.ScriptFileChanged += UserConfigService.RequestReload;
        _luaHost.Evaluate(UserConfigService.Current, LuaScriptHost.GetConfigLuaPath());
        InvalidateLuaPresentation();
        _showTabBar = UserConfigService.Current.TabBar.Show;
        _activeTheme = SilkConfig.LoadActiveTheme();
        (_themeForeground, _themeBackground) = SilkConfig.InitializeTheme();
        _themeSelectionColor = SilkConfig.ResolveSelectionColor(_activeTheme);

        _gl = _window.CreateOpenGL();
        string openGlVersion = _gl.GetStringS(StringName.Version);
        if (!GraphicsCapabilities.IsOpenGlVersionSupported(openGlVersion))
            throw new PlatformNotSupportedException(GraphicsCapabilities.DescribeUnsupportedVersion(openGlVersion));

        ResolveFontAndMetrics();
        _atlas = GlyphAtlasService.GetOrCreateAtlas(_typeface, _cellFontSizePx());
        GlyphAtlasService.AcquireAtlas(_atlas);
        _renderer = new SilkTerminalRenderer(_gl, _atlas);
        _sceneComposer = new TerminalSceneComposer(_atlas, _typeface, _cellFontSizePx());

        _mouseHost = new MouseHost();
        _keyboardDispatcher = new TerminalKeyboardDispatcher(_mouseHost);
        _keyboardController = new TerminalKeyboardController(
            keyPressed: _keyboardDispatcher.HandleKeyDown,
            textReceived: _keyboardDispatcher.HandleText,
            activity: OnKeyboardActivity);
        _mouseController = new TerminalMouseController(_mouseHost);

        _input = _window.CreateInput();
        if (_input.Keyboards.Count > 0)
        {
            _keyboard = _input.Keyboards[0];
            _clipboard = new KeyboardClipboard(_keyboard);
            _keyboard.KeyDown += OnKeyboardDown;
            _keyboard.KeyUp += OnKeyboardUp;
            _keyboard.KeyChar += OnKeyboardChar;
        }

        if (_input.Mice.Count > 0)
        {
            _mouse = _input.Mice[0];
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.MouseMove += OnMouseMove;
            _mouse.Scroll += OnMouseScroll;
        }

        _tabManager.CreateTab(cols: _cols, rows: _rows);
        _keybindings.RegisterDefaults();
        _keybindings.ApplyCustomBindings(UserConfigService.Current.Keybindings);
        int barRows = _showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;
        float topOffset = barRows * _cellHeight * _scale;
        _window.Size = new Vector2D<int>((int)(_cols * _cellWidth), (int)(_rows * _cellHeight + topOffset / _scale));
        StartControlServer();
        _luaHost.NotifyGuiStartup();
    }

    private static void OnActiveTabChanged(TerminalTab? tab)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        if (_lastWindowFocus == true)
        {
            _focusedPane?.Session.SendFocusReport(focused: false);
            _focusedPane = tab?.ActivePane;
            _focusedPane?.Session.SendFocusReport(focused: true);
        }
        else
        {
            _focusedPane = null;
        }

        RefreshSessionSubscriptions();
        RefreshLuaTabTitles();
        RefreshLuaStatus();
        if (tab == null)
        {
            if (!_closed) _window.Close();
            return;
        }
        EnqueuePendingTitle(tab.Title);
    }

    private static void OnTabProcessExited(TerminalTab tab, LeafPane leaf, int exitCode)
    {
        lock (_pendingProcessExitsLock)
        {
            if (_closed)
                return;
            _pendingProcessExits.Enqueue(new ProcessExit(tab, leaf));
            Volatile.Write(ref _pendingProcessExitCount, _pendingProcessExits.Count);
        }
    }

    private static void OnTabTitleChanged(TerminalTab tab, string title)
    {
        lock (_pendingTabTitleChangesLock)
        {
            if (_closed)
                return;
            _pendingTabTitleChanges.Enqueue(new TabTitleChange(tab));
            Volatile.Write(ref _pendingTabTitleChangeCount, _pendingTabTitleChanges.Count);
        }

        if (tab == _tabManager?.ActiveTab)
            EnqueuePendingTitle(title);
    }

    private static void EnqueuePendingTitle(string title)
    {
        lock (_pendingTitlesLock)
        {
            if (_closed)
                return;
            _pendingTitles.Enqueue(title);
            Volatile.Write(ref _pendingTitleCount, _pendingTitles.Count);
        }
    }

    private static void OnTabAdded(TerminalTab tab)
    {
        SubscribePaneTree(tab);
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        RefreshSessionSubscriptions();
        RefreshLuaTabTitles();
        RefreshLuaStatus();
    }

    private static void OnTabClosed(TerminalTab tab)
    {
        UnsubscribePaneTree(tab);
        _luaTabTitleCache.Remove(tab);
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        RefreshSessionSubscriptions();
        RefreshLuaTabTitles();
        RefreshLuaStatus();
        if (_tabManager?.ActiveTab is { } activeTab)
            EnqueuePendingTitle(activeTab.Title);
    }

    private static void OnPaneTopologyChanged()
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        RefreshSessionSubscriptions();
    }

    private static void OnActivePaneChanged(LeafPane oldPane, LeafPane newPane)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        var activeTab = _tabManager?.ActiveTab;
        if (!_closed && _lastWindowFocus == true &&
            activeTab != null && ReferenceEquals(activeTab.ActivePane, newPane))
        {
            oldPane.Session.SendFocusReport(focused: false);
            newPane.Session.SendFocusReport(focused: true);
            _focusedPane = newPane;
        }
    }

    private static void SubscribePaneTree(TerminalTab tab)
    {
        if (_paneSubscriptions.ContainsKey(tab))
            return;

        var handlers = (TopologyChanged: (Action)OnPaneTopologyChanged,
            ActivePaneChanged: (Action<LeafPane, LeafPane>)OnActivePaneChanged);
        tab.PaneTree.TopologyChanged += handlers.TopologyChanged;
        tab.PaneTree.ActivePaneChanged += handlers.ActivePaneChanged;
        _paneSubscriptions.Add(tab, handlers);
    }

    private static void UnsubscribePaneTree(TerminalTab tab)
    {
        if (!_paneSubscriptions.Remove(tab, out var handlers))
            return;

        tab.PaneTree.TopologyChanged -= handlers.TopologyChanged;
        tab.PaneTree.ActivePaneChanged -= handlers.ActivePaneChanged;
    }

    private static void RefreshSessionSubscriptions()
    {
        RefreshClipboardSubscriptions();
        RefreshVisibleSessionSubscriptions();
    }

    private static void RefreshClipboardSubscriptions()
    {
        _sessionScratch.Clear();
        var tabs = _tabManager?.Tabs;
        if (tabs != null)
        {
            for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
            {
                var leaves = tabs[tabIndex].PaneTree.Leaves;
                for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
                    _sessionScratch.Add(leaves[leafIndex].Session);
            }
        }

        foreach (var session in _sessionScratch)
        {
            if (_clipboardSubscriptions.ContainsKey(session))
                continue;

            Action<string> callback = CreateClipboardHandler(session);
            session.ClipboardWriteRequested += callback;
            _clipboardSubscriptions.Add(session, callback);
        }

        _staleSessionScratch.Clear();
        foreach (var pair in _clipboardSubscriptions)
        {
            if (!_sessionScratch.Contains(pair.Key))
                _staleSessionScratch.Add(pair.Key);
        }
        for (int i = 0; i < _staleSessionScratch.Count; i++)
        {
            var session = _staleSessionScratch[i];
            session.ClipboardWriteRequested -= _clipboardSubscriptions[session];
            _clipboardSubscriptions.Remove(session);
            _renderCallbackCache.Remove(session);
        }
    }

    private static Action<string> CreateClipboardHandler(TerminalSession session) =>
        text => EnqueueClipboardWrite(session, text);

    private static void EnqueueClipboardWrite(TerminalSession session, string text)
    {
        lock (_pendingClipboardsLock)
        {
            if (_closed)
                return;
            _pendingClipboards.Enqueue(new ClipboardWrite(session, text));
            Volatile.Write(ref _pendingClipboardCount, _pendingClipboards.Count);
        }

    }
    private static void RefreshVisibleSessionSubscriptions()
    {
        var leaves = _tabManager?.ActiveTab?.PaneTree.Leaves;
        if (leaves == null)
        {
            Array.Clear(_visibleLeaves, 0, _visibleLeafCount);
            Array.Clear(_frameGenerationValid, 0, _visibleLeafCount);
            _visibleSessionScratch.Clear();
            _visibleLeafScratch.Clear();
            _visibleLeafCount = 0;
            foreach (var pair in _renderSubscriptions)
                pair.Key.RenderScheduled -= pair.Value;
            _renderSubscriptions.Clear();
            _committedGenerations.Clear();
            return;
        }

        int previousLeafCount = _visibleLeafCount;
        _visibleSessionScratch.Clear();
        _visibleLeafScratch.Clear();
        if (_visibleLeaves.Length < leaves.Count)
            Array.Resize(ref _visibleLeaves, leaves.Count);
        if (previousLeafCount > leaves.Count)
        {
            Array.Clear(_visibleLeaves, leaves.Count, previousLeafCount - leaves.Count);
            Array.Clear(_frameGenerationValid, leaves.Count, previousLeafCount - leaves.Count);
        }

        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            _visibleLeaves[i] = leaf;
            _visibleLeafScratch.Add(leaf);
            if (_visibleSessionScratch.Add(leaf.Session) && !_renderSubscriptions.ContainsKey(leaf.Session))
            {
                if (!_renderCallbackCache.TryGetValue(leaf.Session, out var callback))
                {
                    callback = OnSessionRenderScheduled;
                    _renderCallbackCache.Add(leaf.Session, callback);
                }
                leaf.Session.RenderScheduled += callback;
                _renderSubscriptions.Add(leaf.Session, callback);
            }
        }
        _visibleLeafCount = leaves.Count;

        _staleSessionScratch.Clear();
        foreach (var pair in _renderSubscriptions)
        {
            if (!_visibleSessionScratch.Contains(pair.Key))
                _staleSessionScratch.Add(pair.Key);
        }
        for (int i = 0; i < _staleSessionScratch.Count; i++)
        {
            var session = _staleSessionScratch[i];
            session.RenderScheduled -= _renderSubscriptions[session];
            _renderSubscriptions.Remove(session);
        }

        _staleLeafScratch.Clear();
        foreach (var pair in _committedGenerations)
        {
            if (!_visibleLeafScratch.Contains(pair.Key))
                _staleLeafScratch.Add(pair.Key);
        }
        for (int i = 0; i < _staleLeafScratch.Count; i++)
            _committedGenerations.Remove(_staleLeafScratch[i]);

        if (_frameGenerations.Length < _visibleLeafCount)
        {
            Array.Resize(ref _frameGenerations, _visibleLeafCount);
            Array.Resize(ref _frameGenerationValid, _visibleLeafCount);
        }
    }

    private static void OnSessionRenderScheduled() =>
        WindowPresentationGate.Invalidate(WindowFrameReason.Content);
    private static float _cellFontSizePx()
    {
        float size = EffectiveFontSize();
        float scale = float.IsFinite(_scale) ? Math.Clamp(_scale, 0.1f, 16f) : 1f;
        return size * scale;
    }

    private static float EffectiveFontSize()
    {
        double configured = double.IsFinite(_configuredFontSize)
            ? _configuredFontSize
            : UserConfigService.Current.Font.Size;
        double size = _runtimeFontSize ?? configured;
        return float.IsFinite((float)size) ? Math.Clamp((float)size, 1f, 512f) : 14f;
    }

    private static void OnConfigChanged(DottyUserConfig config)
    {
        if (_closed)
            return;

        _luaHost.Evaluate(config, LuaScriptHost.GetConfigLuaPath());
        InvalidateLuaPresentation();
        ApplyConfigOnly(config);
    }
    private static void InvalidateLuaPresentation()
    {
        _luaTabTitleCache.Clear();
        _lastLuaStatusRefreshTimestampMs = 0;
        _lastLuaError = null;
        _previousLuaStatusText.Clear();
        _luaStatusWarning = false;
        RefreshLuaTabTitles();
        RefreshLuaStatus();
        WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
    }

    private static void RefreshLuaTabTitles()
    {
        if (_tabManager == null)
            return;

        var tabs = _tabManager.Tabs;
        for (int i = 0; i < tabs.Count; i++)
            GetOrRefreshTabTitle(tabs[i], i);
    }

    private static TabTitleCache GetOrRefreshTabTitle(TerminalTab tab, int index)
    {
        string rawTitle = tab.Title ?? "Terminal";
        if (_luaTabTitleCache.TryGetValue(tab, out var cached))
        {
            bool contentChanged = cached.Index != index ||
                !string.Equals(cached.RawTitle, rawTitle, StringComparison.Ordinal);
            if (!contentChanged && cached.IsActive == tab.IsActive)
                return cached;

            cached.RawTitle = rawTitle;
            cached.Index = index;
            cached.IsActive = tab.IsActive;
        }
        else
        {
            cached = new TabTitleCache(rawTitle, index, tab.IsActive);
            _luaTabTitleCache.Add(tab, cached);
        }

        cached.HasFormattedTitle =
            _luaHost.Hooks.TryFormatTabTitle(tab, index, cached.FormattedTitle);
        return cached;
    }

    private static ReadOnlySpan<char> GetLuaStatusText() =>
        _luaStatusWarning ? _luaStatusWarningText.Span : _luaStatusText.Span;

    private static void RefreshLuaStatus()
    {
        if (_luaHost == null)
            return;

        _previousLuaStatusText.Set(GetLuaStatusText());
        bool warning = false;
        string? error = _luaHost.LastError;
        if (error == null)
        {
            if (!_luaHost.Hooks.TryFormatStatus(_luaStatusText))
                _luaStatusText.Clear();
            error = _luaHost.LastError;
        }

        if (error != null)
        {
            UpdateLuaStatusWarning(error);
            warning = true;
        }
        else
        {
            _lastLuaError = null;
        }

        bool changed = _luaStatusWarning != warning ||
            !_previousLuaStatusText.Span.SequenceEqual(
                warning ? _luaStatusWarningText.Span : _luaStatusText.Span);
        _luaStatusWarning = warning;
        _lastLuaStatusRefreshTimestampMs = GetClockMilliseconds();
        if (changed)
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
    }

    private static void UpdateLuaStatusWarning(string error)
    {
        if (string.Equals(_lastLuaError, error, StringComparison.Ordinal))
            return;

        _lastLuaError = error;
        ReadOnlySpan<char> firstLine = error.AsSpan();
        int lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
            firstLine = firstLine[..lineEnd];
        const int maxWarningLength = 120;
        bool truncated = firstLine.Length > maxWarningLength;
        if (truncated)
            firstLine = firstLine[..(maxWarningLength - 1)];

        ReadOnlySpan<char> prefix = "⚠ Lua: ";
        Span<char> warning = stackalloc char[127];
        prefix.CopyTo(warning);
        firstLine.CopyTo(warning[prefix.Length..]);
        int warningLength = prefix.Length + firstLine.Length;
        if (truncated)
            warning[warningLength++] = '…';
        _luaStatusWarningText.Set(warning[..warningLength]);
    }

    private static void UpdateWindowTitle(string fallbackTitle)
    {
        int tabCount = _tabManager?.Count ?? 1;
        int activeIndex = _tabManager?.ActiveIndex ?? -1;
        var activeTab = _tabManager?.ActiveTab;
        if (activeTab == null)
        {
            AssignWindowTitle(BuildWindowTitle(fallbackTitle.AsSpan(), activeIndex, tabCount));
            return;
        }

        var cache = GetOrRefreshTabTitle(activeTab, activeIndex);
        ReadOnlySpan<char> title = cache.HasFormattedTitle
            ? cache.FormattedTitle.Span
            : (activeTab.Title ?? "Terminal").AsSpan();
        AssignWindowTitle(BuildWindowTitle(title, activeIndex, tabCount));
    }

    private static ReadOnlySpan<char> BuildWindowTitle(ReadOnlySpan<char> title, int activeIndex, int tabCount)
    {
        Span<char> prefix = stackalloc char[32];
        int prefixLength = 0;
        if (tabCount > 1)
        {
            prefix[prefixLength++] = '[';
            if ((activeIndex + 1).TryFormat(prefix[prefixLength..], out int indexLength))
                prefixLength += indexLength;
            prefix[prefixLength++] = '/';
            if (tabCount.TryFormat(prefix[prefixLength..], out int countLength))
                prefixLength += countLength;
            prefix[prefixLength++] = ']';
            prefix[prefixLength++] = ' ';
        }

        int titleLength = prefixLength + title.Length;
        if (_windowTitleScratch.Length < titleLength)
            Array.Resize(ref _windowTitleScratch, titleLength);
        prefix[..prefixLength].CopyTo(_windowTitleScratch);
        title.CopyTo(_windowTitleScratch.AsSpan(prefixLength));
        return _windowTitleScratch.AsSpan(0, titleLength);
    }

    private static unsafe void AssignWindowTitle(ReadOnlySpan<char> title)
    {
        if (title.SequenceEqual(_lastWindowTitle.Span))
            return;

        var nativeGlfwHandle = _window.Native?.Glfw;
        if (nativeGlfwHandle.HasValue && nativeGlfwHandle.Value != 0)
        {
            if (!_glfwTitleProcResolved)
            {
                _glfwTitleProcResolved = true;
                _glfwApi = global::Silk.NET.GLFW.Glfw.GetApi();
                _glfwSetWindowTitleProc = _glfwApi.GetProcAddress("glfwSetWindowTitle");
            }

            if (_glfwSetWindowTitleProc != 0)
            {
                int byteLength = Encoding.UTF8.GetByteCount(title);
                int requiredLength = byteLength + 1;
                if (_windowTitleUtf8.Length < requiredLength)
                    Array.Resize(ref _windowTitleUtf8, requiredLength);
                int written = Encoding.UTF8.GetBytes(title, _windowTitleUtf8.AsSpan(0, byteLength));
                _windowTitleUtf8[written] = 0;
                fixed (byte* utf8Title = _windowTitleUtf8)
                {
                    var setWindowTitle = (delegate* unmanaged[Cdecl]<global::Silk.NET.GLFW.WindowHandle*, byte*, void>)_glfwSetWindowTitleProc;
                    setWindowTitle((global::Silk.NET.GLFW.WindowHandle*)nativeGlfwHandle.Value, utf8Title);
                }
                _lastWindowTitle.Set(title);
                return;
            }
        }

        _window.Title = new string(title);
        _lastWindowTitle.Set(title);
    }

    private static bool TryFindOwningPane(TerminalSession session, out TerminalTab tab, out LeafPane pane)
    {
        var tabs = _tabManager.Tabs;
        for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
        {
            var candidateTab = tabs[tabIndex];
            var leaves = candidateTab.PaneTree.Leaves;
            for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
            {
                var candidatePane = leaves[leafIndex];
                if (!ReferenceEquals(candidatePane.Session, session))
                    continue;

                tab = candidateTab;
                pane = candidatePane;
                return true;
            }
        }

        tab = null!;
        pane = null!;
        return false;
    }
    private static void ApplyConfigOnly(DottyUserConfig config)
    {
        if (_closed) return;
        _runtimeFontSize = null;
        _showTabBar = config.TabBar.Show;
        WindowPresentationGate.Invalidate(WindowFrameReason.ThemeConfig);
        _keybindings.RegisterDefaults();
        _keybindings.ApplyCustomBindings(config.Keybindings);
        ResolveFontAndMetrics();
        RefreshFontResources();
        var size = _window.FramebufferSize;
        if (size.X > 0 && size.Y > 0) ApplyFramebufferLayout(size);
        _activeTheme = SilkConfig.LoadActiveTheme();
        (_themeForeground, _themeBackground) = SilkConfig.InitializeTheme();
        _themeSelectionColor = SilkConfig.ResolveSelectionColor(_activeTheme);
    }
    private static TerminalTab CreateLuaTab(string? workingDirectory, string? shell)
    {
        var tab = _tabManager.CreateTab(cols: _cols, rows: _rows, workingDirectory: workingDirectory, shell: shell);
        SilkConfig.ApplyThemeToAdapter(tab.Session.Adapter);
        return tab;
    }

    private static LeafPane SplitLuaPane(TerminalTab tab, LeafPane target, SplitDirection direction, string? workingDirectory, string? shell)
    {
        var pane = tab.PaneTree.Split(target, direction, workingDirectory, shell);
        SilkConfig.ApplyThemeToAdapter(pane.Session.Adapter);
        return pane;
    }


    private static void ResolveFontAndMetrics()
    {
        var config = UserConfigService.Current;
        _configuredFontSize = double.IsFinite(config.Font.Size) ? config.Font.Size : 14d;
        float rawScale = _window.FramebufferSize.X / (float)MathF.Max(1, _window.Size.X);
        _scale = float.IsFinite(rawScale) ? Math.Clamp(rawScale, 0.1f, 16f) : 1f;
        _typeface = FontMetricsService.ResolveTypeface(config.Font.Family);
        (_cellWidth, _cellHeight) = FontMetricsService.MeasureCell(
            _typeface,
            EffectiveFontSize(),
            config.Font.LineHeight,
            _scale);
    }

    private static void RefreshFontResources()
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Atlas);
        var newAtlas = GlyphAtlasService.GetOrCreateAtlas(_typeface, _cellFontSizePx());
        if (!ReferenceEquals(newAtlas, _atlas))
        {
            var oldAtlas = _atlas;
            _atlas = newAtlas;
            GlyphAtlasService.AcquireAtlas(newAtlas);
            _renderer?.SetAtlas(newAtlas);
            _sceneComposer?.UpdateResources(newAtlas, _typeface, _cellFontSizePx());
            if (oldAtlas != null)
                GlyphAtlasService.ReleaseAtlas(oldAtlas);
        }
        else
        {
            _sceneComposer?.UpdateResources(_atlas, _typeface, _cellFontSizePx());
        }
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Resize);
        if (size.X <= 0 || size.Y <= 0) return;

        float previousScale = _scale;
        ResolveFontAndMetrics();
        if (MathF.Abs(previousScale - _scale) > 0.01f)
            RefreshFontResources();

        ApplyFramebufferLayout(size);
    }

    private static void ApplyFramebufferLayout(Vector2D<int> size)
    {
        _gl.Viewport(size);
        var config = UserConfigService.Current;
        var pad = config.Window.Padding;
        float padX = (float)(pad.Left + pad.Right) * _scale;
        float padY = (float)(pad.Top + pad.Bottom) * _scale;

        int barRows = _showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;
        float topOffset = barRows * _cellHeight * _scale;

        _cols = Math.Max(1, (int)((size.X - padX) / (_cellWidth * _scale)));
        _rows = Math.Max(1, (int)((size.Y - topOffset - padY) / (_cellHeight * _scale)));
        _tabManager?.ResizeAll(_cols, _rows);
    }
    private static void StartControlServer()
    {
        string? configuredPort = Environment.GetEnvironmentVariable("DOTTY_TEST_PORT");
        if (string.IsNullOrWhiteSpace(configuredPort))
            return;
        if (!int.TryParse(configuredPort, out int port) || port is < 0 or > 65535)
            throw new InvalidOperationException("DOTTY_TEST_PORT must be an integer between 0 and 65535.");

        _controlServer = new DesktopControlServer(port, QueueControlCommand);
        _controlServer.Start();
        Console.WriteLine($"DOTTY_TEST_PORT={_controlServer.Port}");
    }

    private static Task<string> QueueControlCommand(string command)
    {
        if (_closed)
            return Task.FromResult("ERROR host is closed");

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlCommands.Enqueue(new ControlRequest(command, completion));
        return completion.Task;
    }

    private static void DrainControlCommands()
    {
        while (_pendingControlCommands.TryDequeue(out var request))
        {
            string response;
            try
            {
                response = ExecuteControlCommand(request.Command);
            }
            catch (Exception exception)
            {
                response = $"ERROR {exception.Message}";
            }
            request.Completion.TrySetResult(response);
        }
    }

    private static string ExecuteControlCommand(string command)
    {
        if (string.Equals(command, "WAIT_FOR_IDLE", StringComparison.OrdinalIgnoreCase))
            return "IDLE";
        if (string.Equals(command, "DUMP", StringComparison.OrdinalIgnoreCase))
            return BuildControlDump();
        if (string.Equals(command, "GET_STATE", StringComparison.OrdinalIgnoreCase))
            return BuildControlState();
        if (string.Equals(command, "ALLOC", StringComparison.OrdinalIgnoreCase))
        {
            // Snapshot before formatting so the response string is not counted.
            long total = GC.GetTotalAllocatedBytes(precise: true);
            int gen0 = GC.CollectionCount(0);
            return $"{{\"totalAllocatedBytes\":{total},\"gen0Collections\":{gen0}}}";
        }
        if (string.Equals(command, "STATS", StringComparison.OrdinalIgnoreCase))
        {
            int tabCount = _tabManager?.Count ?? 0;
            int activeIndex = _tabManager?.ActiveIndex ?? -1;
            long skipped = _sceneComposer?.SkippedLeafFrames ?? 0;
            return $"{{\"tabs\":{tabCount},\"activeTab\":{activeIndex},\"skippedLeafFrames\":{skipped}}}";
        }
        if (string.Equals(command, "SHUTDOWN", StringComparison.OrdinalIgnoreCase))
        {
            _window.Close();
            return "OK";
        }

        int separator = command.IndexOf(':');
        if (separator <= 0)
            return "ERROR unknown command";

        string name = command[..separator].Trim().ToUpperInvariant();
        string payload = command[(separator + 1)..];
        var activeTab = _tabManager?.ActiveTab;
        if (activeTab == null)
            return "ERROR no active terminal";

        return name switch
        {
            "TYPE" => SendControlText(activeTab, payload),
            "KEY" => SendControlKey(activeTab, payload),
            "RESIZE" => ResizeFromControl(payload),
            _ => "ERROR unknown command",
        };
    }

    private static string SendControlText(TerminalTab activeTab, string text)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
        _keyboardDispatcher.HandleText(text);
        return "OK";
    }

    private static string SendControlKey(TerminalTab activeTab, string keyName)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
        string normalized = keyName.Trim().ToLowerInvariant();
        Span<byte> bytes = stackalloc byte[64];
        int byteCount;
        switch (normalized)
        {
            case "ctrlc":
            case "control-c":
                bytes[0] = 0x03;
                byteCount = 1;
                break;
            case "enter":
            case "return":
                bytes[0] = 0x0d;
                byteCount = 1;
                break;
            case "tab":
                bytes[0] = 0x09;
                byteCount = 1;
                break;
            case "escape":
            case "esc":
                bytes[0] = 0x1b;
                byteCount = 1;
                break;
            case "backspace":
                bytes[0] = 0x7f;
                byteCount = 1;
                break;
            default:
                if (!Enum.TryParse<InputKey>(keyName, ignoreCase: true, out var key))
                    return "ERROR unknown key";
                byteCount = SilkKeyMapper.Encode(
                    key,
                    ctrl: false,
                    shift: false,
                    alt: false,
                    keypadAppMode: activeTab.Session.Adapter.KeypadApplicationMode,
                    destination: bytes,
                    kittyMode: activeTab.Session.Adapter.KittyKeyboardMode,
                    applicationCursorKeys: activeTab.Session.Adapter.ApplicationCursorKeysEnabled);
                if (byteCount == 0)
                    return "ERROR unsupported key";
                break;
        }

        activeTab.Session.WriteInput(bytes[..byteCount]);
        return "OK";
    }

    private static string ResizeFromControl(string payload)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Resize);
        int separator = payload.IndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(payload[..separator], out int columns) ||
            !int.TryParse(payload[(separator + 1)..], out int rows) ||
            columns <= 0 ||
            rows <= 0)
        {
            return "ERROR dimensions must be positive integers";
        }

        _tabManager!.ResizeAll(columns, rows);
        _cols = columns;
        _rows = rows;
        return "OK";
    }

    private static string BuildControlDump()
    {
        var activeTab = _tabManager?.ActiveTab;
        if (activeTab == null)
            return "DUMP EMPTY";

        var buffer = activeTab.Session.Adapter.Buffer;
        string response = string.Empty;
        buffer.WithSyncRoot(() =>
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible();
            var result = new StringBuilder();
            result.AppendLine($"R={snapshot.Rows} C={snapshot.Columns} CUR={snapshot.CursorRow},{snapshot.CursorCol}");
            for (int row = 0; row < snapshot.Rows; row++)
                result.AppendLine(snapshot.GetVisibleRowText(row));
            result.Append("END");
            response = result.ToString();
        });
        return response;
    }

    private static string BuildControlState()
    {
        var activeTab = _tabManager?.ActiveTab;
        if (activeTab == null)
            return "ERROR no active terminal";

        var buffer = activeTab.Session.Adapter.Buffer;
        return $"{{\"rows\":{buffer.Rows},\"cols\":{buffer.Columns},\"cursorRow\":{buffer.CursorRow},\"cursorCol\":{buffer.CursorCol},\"scrollbackLines\":{buffer.ScrollbackCount},\"isAlternateScreen\":{(buffer.IsAlternateScreenActive ? "true" : "false")},\"title\":{QuoteJson(activeTab.Title)}}}";
    }
    private static string QuoteJson(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (character < ' ')
                        result.Append($"\\u{(int)character:x4}");
                    else
                        result.Append(character);
                    break;
            }
        }
        result.Append('"');
        return result.ToString();
    }

    private static void DrainWindowEvents()
    {
        DrainControlCommands();
        _lifecycle.Drain();

        bool titlesChanged = false;
        while (TryDequeueTabTitleChange(out var titleChange))
        {
            _luaTabTitleCache.Remove(titleChange.Tab);
            titlesChanged = true;
        }
        if (titlesChanged && !_closed)
        {
            RefreshLuaTabTitles();
            RefreshLuaStatus();
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
        }

        while (TryDequeueProcessExit(out var processExit))
        {
            if (!_closed)
                _tabManager?.CloseExitedPane(processExit.Tab, processExit.Leaf);
        }

        while (TryDequeuePendingTitle(out var title))
        {
            if (!_closed)
                UpdateWindowTitle(title);
        }

        while (TryDequeueClipboardWrite(out var request))
        {
            if (_closed || _keyboard is null)
                continue;

            if (!TryFindOwningPane(request.Session, out var ownerTab, out var ownerPane))
                continue;
            if (!_luaHost.Hooks.AllowClipboardWrite(ownerPane, ownerTab, request.Text))
                continue;

            _clipboard?.SetText(request.Text);
        }
    }

    private static bool TryDequeuePendingTitle(out string title)
    {
        if (Volatile.Read(ref _pendingTitleCount) == 0)
        {
            title = string.Empty;
            return false;
        }

        lock (_pendingTitlesLock)
        {
            if (_pendingTitles.Count == 0)
            {
                Volatile.Write(ref _pendingTitleCount, 0);
                title = string.Empty;
                return false;
            }

            title = _pendingTitles.Dequeue();
            Volatile.Write(ref _pendingTitleCount, _pendingTitles.Count);
            return true;
        }
    }

    private static bool TryDequeueTabTitleChange(out TabTitleChange change)
    {
        if (Volatile.Read(ref _pendingTabTitleChangeCount) == 0)
        {
            change = default;
            return false;
        }

        lock (_pendingTabTitleChangesLock)
        {
            if (_pendingTabTitleChanges.Count == 0)
            {
                Volatile.Write(ref _pendingTabTitleChangeCount, 0);
                change = default;
                return false;
            }

            change = _pendingTabTitleChanges.Dequeue();
            Volatile.Write(ref _pendingTabTitleChangeCount, _pendingTabTitleChanges.Count);
            return true;
        }
    }

    private static bool TryDequeueProcessExit(out ProcessExit processExit)
    {
        if (Volatile.Read(ref _pendingProcessExitCount) == 0)
        {
            processExit = default;
            return false;
        }

        lock (_pendingProcessExitsLock)
        {
            if (_pendingProcessExits.Count == 0)
            {
                Volatile.Write(ref _pendingProcessExitCount, 0);
                processExit = default;
                return false;
            }

            processExit = _pendingProcessExits.Dequeue();
            Volatile.Write(ref _pendingProcessExitCount, _pendingProcessExits.Count);
            return true;
        }
    }

    private static bool TryDequeueClipboardWrite(out ClipboardWrite request)
    {
        if (Volatile.Read(ref _pendingClipboardCount) == 0)
        {
            request = default;
            return false;
        }

        lock (_pendingClipboardsLock)
        {
            if (_pendingClipboards.Count == 0)
            {
                Volatile.Write(ref _pendingClipboardCount, 0);
                request = default;
                return false;
            }

            request = _pendingClipboards.Dequeue();
            Volatile.Write(ref _pendingClipboardCount, _pendingClipboards.Count);
            return true;
        }
    }
    private static void OnRender(double delta)
    {
        if (_closed)
            return;

        try
        {
            OnRenderCore(delta);
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                Console.Error.WriteLine(GraphicsCapabilities.DescribeInitializationFailure(exception));
                _window.Close();
            }
        }
    }

    private static void OnRenderCore(double delta)
    {
        _keyboardController?.Tick();
        DrainWindowEvents();
        if (_closed)
            return;
        if (_mouseController?.TickSelectionAutoscroll() == true)
            WindowPresentationGate.Invalidate(WindowFrameReason.Selection);
        var tabManager = _tabManager;
        var activeTab = tabManager.ActiveTab;
        long now = GetClockMilliseconds();
        if (now - _lastLuaStatusRefreshTimestampMs >= 1000)
            RefreshLuaStatus();
        if (!WindowPresentationGate.ShouldPresent(activeTab?.Session.Adapter))
            return;

        int framebufferWidth = _window.FramebufferSize.X;
        int framebufferHeight = _window.FramebufferSize.Y;
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
            return;

        UpdateCursorBlink(now, activeTab != null);

        WindowFrameReason pendingReasons = WindowPresentationGate.PendingReasons;
        if ((pendingReasons & WindowFrameReason.TabOrPane) != 0)
            RefreshVisibleSessionSubscriptions();

        bool generationDirty = false;
        bool compareCommittedGenerations =
            pendingReasons == WindowFrameReason.None &&
            framebufferWidth == _committedFramebufferWidth &&
            framebufferHeight == _committedFramebufferHeight &&
            _atlas.ContentVersion == _committedAtlasVersion;
        LeafPane[] visibleLeaves = _visibleLeaves;
        if (activeTab != null)
        {
            for (int i = 0; i < _visibleLeafCount; i++)
            {
                if (!TryReadGeneration(visibleLeaves[i].Session.Adapter.Buffer, out ulong generation))
                {
                    _frameGenerationValid[i] = false;
                    generationDirty = true;
                    continue;
                }

                _frameGenerations[i] = generation;
                _frameGenerationValid[i] = true;
                if (compareCommittedGenerations &&
                    (!_committedGenerations.TryGetValue(visibleLeaves[i], out ulong committed) || committed != generation))
                {
                    generationDirty = true;
                }
            }
        }
        else
        {
            Array.Clear(_frameGenerationValid, 0, _visibleLeafCount);
        }
        bool dirty = pendingReasons != WindowFrameReason.None ||
            generationDirty ||
            framebufferWidth != _committedFramebufferWidth ||
            framebufferHeight != _committedFramebufferHeight ||
            _atlas.ContentVersion != _committedAtlasVersion;
        if (!dirty)
        {
            // No SwapBuffers here: swapping without a fresh render presents the
            // stale back buffer (one full frame behind, or uninitialized garbage
            // at startup) — a periodic fullscreen flash to old content every
            // time the keepalive would have fired. Holding the front buffer is
            // always correct when nothing changed.
            Thread.Sleep(IdleFrameSleepMs);
            return;
        }
        bool contentOnly = (pendingReasons & ~WindowFrameReason.Content) == WindowFrameReason.None;
        if (contentOnly &&
            _lastPresentTimestampMs != 0 &&
            now - _lastPresentTimestampMs < BackloggedFrameIntervalMs)
        {
            for (int i = 0; i < _visibleLeafCount; i++)
            {
                if (visibleLeaves[i].Session.OutputBacklogged)
                {
                    Thread.Sleep(IdleFrameSleepMs);
                    return;
                }
            }
        }
        WindowFrameReason consumedReasons = WindowPresentationGate.Consume();
        try
        {
            if (activeTab == null)
            {
                _renderer.Render(
                    ReadOnlySpan<CellInstance>.Empty,
                    ReadOnlySpan<ChromeQuadInstance>.Empty,
                    _atlas.Width,
                    _atlas.Height,
                    framebufferWidth,
                    framebufferHeight,
                    _cellWidth * _scale,
                    _cellHeight * _scale,
                    0.85f,
                    0.7f,
                    0.04f,
                    _themeBackground,
                    false);
                _window.SwapBuffers();
                _lastPresentTimestampMs = GetClockMilliseconds();
                CommitFrameStamps(visibleLeaves, framebufferWidth, framebufferHeight);
                return;
            }

            var theme = _activeTheme;
            var padding = UserConfigService.Current.Window.Padding;
            float padLeft = (float)padding.Left * _scale;
            float padTop = (float)padding.Top * _scale;
            int barRows = _showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;

            var frame = _sceneComposer.Compose(
                activeTab,
                tabManager,
                theme,
                _themeForeground,
                _themeSelectionColor,
                framebufferWidth,
                framebufferHeight,
                _cellWidth,
                _cellHeight,
                _scale,
                _rows,
                _cols,
                padding,
                _showTabBar,
                _cursorBlinkVisible,
                _mouseController?.IsScrollbarHovered ?? false,
                _mouseController?.IsDraggingScrollbar ?? false,
                new SearchOverlayRenderState(
                    _keyboardDispatcher?.SearchActive ?? false,
                    _keyboardDispatcher?.SearchQuery ?? string.Empty,
                    _keyboardDispatcher?.ActiveMatchIndex ?? -1,
                    _keyboardDispatcher?.SearchMatches?.Count ?? 0,
                    _keyboardDispatcher?.SearchMatches),
                _activeContextMenu,
                _mouseController?.HoveredTabIndex ?? -1,
                _mouseController?.HoveredTabHitType ?? TabBarHitType.None,
                _tabTitleSource,
                GetLuaStatusText(),
                _luaStatusWarning);

            if (frame.IsIncomplete)
            {
                // A leaf lost the buffer-lock race mid-burst; its quads are missing.
                // Presenting would flash background where live content belongs, so
                // hold the previous front buffer and retry next frame. Requeue the
                // consumed reasons so the retry still has work to do.
                WindowPresentationGate.Requeue(consumedReasons);
                return;
            }
            _renderer.Render(
                frame.AsSpan(),
                frame.AsChromeSpan(),
                _atlas.Width,
                _atlas.Height,
                framebufferWidth,
                framebufferHeight,
                _cellWidth * _scale,
                _cellHeight * _scale,
                0.85f,
                0.7f,
                0.04f,
                _themeBackground,
                true,
                padLeft,
                padTop,
                barRows,
                frame.ScrollbarChromeStart,
                frame.MenuInstanceStart,
                frame.MenuChromeStart);
            _window.SwapBuffers();
            _lastPresentTimestampMs = GetClockMilliseconds();
            CommitFrameStamps(visibleLeaves, framebufferWidth, framebufferHeight);
        }
        catch
        {
            WindowPresentationGate.Requeue(consumedReasons);
            throw;
        }
    }

    private static void UpdateCursorBlink(long now, bool hasActiveTab)
    {
        if (!hasActiveTab)
            return;

        var cursorConfig = UserConfigService.Current.Cursor;
        if (cursorConfig.Blink)
        {
            int blinkInterval = Math.Max(100, cursorConfig.BlinkIntervalMs);
            if (now - _lastCursorBlinkTimestampMs >= blinkInterval)
            {
                _cursorBlinkVisible = !_cursorBlinkVisible;
                _lastCursorBlinkTimestampMs = now;
                WindowPresentationGate.Invalidate(WindowFrameReason.CursorBlink);
            }
        }
        else if (!_cursorBlinkVisible)
        {
            _cursorBlinkVisible = true;
            _lastCursorBlinkTimestampMs = now;
            WindowPresentationGate.Invalidate(WindowFrameReason.CursorBlink);
        }
    }

    private static bool TryReadGeneration(TerminalBuffer buffer, out ulong generation)
    {
        bool lockTaken = false;
        try
        {
            System.Threading.Monitor.TryEnter(buffer.SyncRoot, 0, ref lockTaken);
            if (!lockTaken)
            {
                generation = 0;
                return false;
            }

            generation = buffer.Generation;
            return true;
        }
        finally
        {
            if (lockTaken)
                System.Threading.Monitor.Exit(buffer.SyncRoot);
        }
    }
    private static void CommitFrameStamps(LeafPane[] visibleLeaves, int framebufferWidth, int framebufferHeight)
    {
        if (ReferenceEquals(visibleLeaves, _visibleLeaves))
        {
            for (int i = 0; i < _visibleLeafCount; i++)
            {
                if (_frameGenerationValid[i] &&
                    TryReadGeneration(visibleLeaves[i].Session.Adapter.Buffer, out ulong after) &&
                    _frameGenerations[i] == after)
                {
                    _committedGenerations[visibleLeaves[i]] = after;
                }
            }
        }
        else
        {
            WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        }

        _committedFramebufferWidth = framebufferWidth;
        _committedFramebufferHeight = framebufferHeight;
        _committedAtlasVersion = _atlas.ContentVersion;
    }

    private static void OnKeyboardDown(IKeyboard keyboard, InputKey key, int scancode)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.TabOrPane);
        _keyboardController.HandleKeyDown(key, scancode);
    }

    private static void OnKeyboardUp(IKeyboard keyboard, InputKey key, int scancode)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input);
        _keyboardController.HandleKeyUp(key, scancode);
    }

    private static void OnKeyboardChar(IKeyboard keyboard, char character)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
        _keyboardController.HandleKeyChar(character);
    }

    private static void OnKeyboardActivity()
    {
        _cursorBlinkVisible = true;
        _lastCursorBlinkTimestampMs = GetClockMilliseconds();
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.CursorBlink);
    }

    private static void OnWindowFocusChanged(bool focused)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input);
        _luaHost?.NotifyWindowFocusChanged(focused);
        RefreshLuaStatus();
        if (!focused)
        {
            _keyboardController?.ResetState();
            _mouseController?.ResetState();
        }
        WindowFocusRouter.Route(
            ref _lastWindowFocus,
            focused,
            _closed,
            _focusStateHandler);
    }

    private static void ApplyWindowFocusState(bool focused)
    {
        if (focused && _tabManager?.ActiveTab is { } activeTab)
        {
            _focusedPane = activeTab.ActivePane;
            _focusedPane.Session.SendFocusReport(focused: true);
        }
        else
        {
            _focusedPane?.Session.SendFocusReport(focused: false);
            _focusedPane = null;
        }
    }

    private static long GetClockMilliseconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseDown(mouse, button);
    }

    private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseMove(mouse, position);
    }

    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseUp(mouse, button);
    }

    private static void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseScroll(mouse, wheel);
    }

    private static void CopySelectionToClipboard()
    {
        var activePane = _tabManager?.ActiveTab?.ActivePane;
        if (activePane == null || !activePane.Selection.HasSelection) return;

        var text = activePane.Selection.GetSelectedText(activePane.Session.Adapter.Buffer);
        if (!string.IsNullOrEmpty(text) && _keyboard != null)
        {
            _clipboard?.SetText(text);
        }
        activePane.Selection.ClearSelection();
    }

    private static void PasteClipboardToSession()
    {
        var activePane = _tabManager?.ActiveTab?.ActivePane;
        if (activePane != null)
            PasteClipboardToPane(activePane);
    }

    private static void PasteClipboardToPane(LeafPane targetPane)
    {
        if (_keyboard == null) return;

        var text = _clipboard?.GetText();
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = ClipboardPasteRouter.Encode(text, targetPane.Session.Adapter);
            targetPane.Session.WriteInput(bytes);
        }
    }

    private static void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        _lifecycle.Close();
        _controlServer?.Dispose();
        _controlServer = null;
        while (_pendingControlCommands.TryDequeue(out var request))
            request.Completion.TrySetResult("ERROR host is closed");
        _window.FocusChanged -= OnWindowFocusChanged;
        UserConfigService.ConfigChanged -= OnConfigChanged;
        if (_tabManager != null)
        {
            _tabManager.ProcessExited -= OnTabProcessExited;
            _tabManager.ActiveTabChanged -= OnActiveTabChanged;
            _tabManager.TabAdded -= OnTabAdded;
            _tabManager.TabClosed -= OnTabClosed;
            _tabManager.TabTitleChanged -= OnTabTitleChanged;
            var tabs = _tabManager.Tabs;
            for (int i = 0; i < tabs.Count; i++)
                UnsubscribePaneTree(tabs[i]);
        }
        foreach (var pair in _clipboardSubscriptions)
            pair.Key.ClipboardWriteRequested -= pair.Value;
        _clipboardSubscriptions.Clear();
        foreach (var pair in _renderSubscriptions)
            pair.Key.RenderScheduled -= pair.Value;
        _renderSubscriptions.Clear();
        _renderCallbackCache.Clear();
        _paneSubscriptions.Clear();
        _sessionScratch.Clear();
        _staleSessionScratch.Clear();
        _visibleSessionScratch.Clear();
        _visibleLeafScratch.Clear();
        _staleLeafScratch.Clear();
        Array.Clear(_visibleLeaves, 0, _visibleLeafCount);
        _visibleLeafCount = 0;
        _committedGenerations.Clear();
        lock (_pendingTitlesLock)
        {
            _pendingTitles.Clear();
            Volatile.Write(ref _pendingTitleCount, 0);
        }
        lock (_pendingClipboardsLock)
        {
            _pendingClipboards.Clear();
            Volatile.Write(ref _pendingClipboardCount, 0);
        }
        lock (_pendingTabTitleChangesLock)
        {
            _pendingTabTitleChanges.Clear();
            Volatile.Write(ref _pendingTabTitleChangeCount, 0);
        }
        lock (_pendingProcessExitsLock)
        {
            _pendingProcessExits.Clear();
            Volatile.Write(ref _pendingProcessExitCount, 0);
        }
        _luaTabTitleCache.Clear();
        _keyboardDispatcher?.Dispose();
        _keyboardDispatcher = null!;
        UserConfigService.Shutdown();
        _luaHost.Dispose();

        _tabManager?.Dispose();
        _input?.Dispose();
        _clipboard = null;
        _renderer?.Dispose();
        if (_atlas != null)
        {
            GlyphAtlasService.ReleaseAtlas(_atlas);
            _atlas = null!;
        }
    }
    private sealed class KeyboardClipboard : ITerminalClipboard
    {
        private readonly IKeyboard _keyboard;

        public KeyboardClipboard(IKeyboard keyboard) =>
            _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));

        public string? GetText() => _keyboard.ClipboardText;
        public void SetText(string text) => _keyboard.ClipboardText = text;
        public bool HasText => !string.IsNullOrEmpty(GetText());
    }

    private sealed class MouseHost : ITerminalMouseHost, ITerminalKeyboardHost
    {
        public TerminalTabManager TabManager => _tabManager;
        public TerminalTab? ActiveTab => _tabManager?.ActiveTab;
        public LuaScriptHost LuaHost => _luaHost;
        public KeybindingManager Keybindings => _keybindings;
        public int Rows => _rows;
        public ContextMenuModel? ActiveContextMenu
        {
            get => _activeContextMenu;
            set => _activeContextMenu = value;
        }

        public TerminalMouseGeometry Geometry
        {
            get
            {
                var size = _window.FramebufferSize;
                var padding = UserConfigService.Current.Window.Padding;
                int barRows = _showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;
                return new TerminalMouseGeometry(
                    Scale: _scale,
                    CellWidth: _cellWidth,
                    CellHeight: _cellHeight,
                    PaddingLeft: (float)padding.Left * _scale,
                    PaddingTop: (float)padding.Top * _scale,
                    TopOffset: barRows * _cellHeight * _scale,
                    FramebufferWidth: size.X,
                    FramebufferHeight: size.Y,
                    Columns: _cols,
                    Rows: _rows,
                    ShowTabBar: _showTabBar,
                    StatusReservedWidth: TabBarQuadBuilder.MeasureStatusWidth(
                        GetLuaStatusText(),
                        _cellWidth * _scale,
                        _typeface,
                        _cellFontSizePx(),
                        _atlas,
                        _luaStatusWarning));
            }
        }
        public bool Ctrl => _keyboardController?.Ctrl ?? false;
        public bool Shift => _keyboardController?.Shift ?? false;
        public bool Alt => _keyboardController?.Alt ?? false;
        public bool AltGr => _keyboardController?.AltGr ?? false;
        public bool Super => _keyboardController?.Super ?? false;

        public void CopySelection() => CopySelectionToClipboard();
        public void PasteClipboard() => PasteClipboardToSession();
        public void PasteClipboard(LeafPane targetPane) => PasteClipboardToPane(targetPane);
        public bool TryExecuteAction(TerminalAction action) =>
            _keyboardDispatcher?.Actions.TryExecute(action) ?? false;

        public void ToggleFullscreen()
        {
            if (_window.WindowState == WindowState.Fullscreen)
            {
                _window.WindowState = _previousNonFullscreenState;
            }
            else
            {
                _previousNonFullscreenState = _window.WindowState;
                _window.WindowState = WindowState.Fullscreen;
            }
        }

        public void ZoomIn() => SetRuntimeZoom(EffectiveFontSize() + 1f);
        public void ZoomOut() => SetRuntimeZoom(EffectiveFontSize() - 1f);
        public void ResetZoom()
        {
            _runtimeFontSize = null;
            RefreshZoomResources();
        }

        private static void SetRuntimeZoom(float size)
        {
            _runtimeFontSize = Math.Clamp(size, MinRuntimeFontSize, MaxRuntimeFontSize);
            RefreshZoomResources();
        }

        private static void RefreshZoomResources()
        {
            ResolveFontAndMetrics();
            RefreshFontResources();
            var size = _window.FramebufferSize;
            if (size.X > 0 && size.Y > 0)
                ApplyFramebufferLayout(size);
            WindowPresentationGate.Invalidate(WindowFrameReason.Resize | WindowFrameReason.Input);
        }

        public void DuplicateTab(TerminalTab activeTab)
        {
            var newTab = _tabManager.CreateTab(
                cols: _cols,
                rows: _rows,
                workingDirectory: activeTab.WorkingDirectory);
            SilkConfig.ApplyThemeToAdapter(newTab.Session.Adapter);
        }

        public void CloseOtherTabs(TerminalTab activeTab)
        {
            for (int i = _tabManager.Count - 1; i >= 0; i--)
            {
                var tab = _tabManager.Tabs[i];
                if (!ReferenceEquals(tab, activeTab))
                    _tabManager.CloseTab(tab);
            }
            _tabManager.SelectTab(activeTab);
        }

        public void Quit() => _window.Close();

        public void CreateTab(TerminalTab activeTab)
        {
            var newTab = _tabManager.CreateTab(
                cols: _cols,
                rows: _rows,
                workingDirectory: activeTab.WorkingDirectory);
            SilkConfig.ApplyThemeToAdapter(newTab.Session.Adapter);
        }

        public void ClearTerminal(TerminalTab activeTab)
        {
            Span<byte> clear = stackalloc byte[1];
            clear[0] = 0x0c;
            activeTab.Session.WriteInput(clear);
        }

        public void WriteInput(TerminalTab activeTab, ReadOnlySpan<byte> bytes) =>
            activeTab.Session.WriteInput(bytes);

        public void OpenHyperlink(string url)
        {
            if (!_luaHost.Hooks.TryOpenUrl(url))
            {
                _ = new DefaultHyperlinkHandler().OpenUrlAsync(url);
            }
        }

        public void SetPointerCursor(StandardCursor cursor)
        {
            if (_mouse != null)
            {
                _mouse.Cursor.StandardCursor = cursor;
            }
        }
    }
}
