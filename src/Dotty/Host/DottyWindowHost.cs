using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Hosting;
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

internal static partial class DottyWindowHost
{
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static GlyphAtlas _atlas = null!;
    private static SKTypeface _typeface = null!;
    private static readonly FontChainResolutionCache _fontChains = new(static options => new FontFallbackChain(options, SkiaFontCatalog.Shared));
    private static FontFallbackChain _fontChain = null!;
    // Cell size in whole device pixels (layout, renderer, VT reports) and its logical view
    // (device / ContentScale, used only where config lengths are logical, e.g. tab-bar rows).
    private static int _cellWidthPx = 1, _cellHeightPx = 1;
    // Full cell metrics (baseline, decoration strokes) behind _cellWidthPx/_cellHeightPx.
    private static CellMetrics _cellMetrics;
    private static float _cellWidth = 1f, _cellHeight = 1f;
    private static DisplayMetrics _display = DisplayMetrics.Identity;
    private static DisplayPlatform _displayPlatform;
    private static int _contentScaleChangePending;
    private const float MinRuntimeFontSize = 6f;
    private const float MaxRuntimeFontSize = 72f;
    private static double? _runtimeFontSize;
    private static double _configuredFontSize = DottyDefaults.FontSize;
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
    private static ClipboardPasteRouter? _pasteRouter;
    private static IPrimarySelection? _primary;
    private static ClipboardPasteRouter? _primaryPasteRouter;
    private static IMouse? _mouse;
    private static TerminalKeyboardController _keyboardController = null!;
    private static TerminalKeyboardDispatcher _keyboardDispatcher = null!;
    private static TerminalMouseController _mouseController = null!;
    private static TerminalSceneComposer _sceneComposer = null!;
    private static NativeTextInputBridge? _nativeTextInput;
    private static bool _initialWindowInputFocus;
    private static MouseHost _mouseHost = null!;

    private static bool _closed;
    private static bool? _lastWindowFocus;
    private static LeafPane? _focusedPane;
    private static readonly Action<bool> _focusStateHandler = ApplyWindowFocusState;
    private static readonly Queue<string> _pendingTitles = new(8);
    private static readonly object _pendingTitlesLock = new();
    private static int _pendingTitleCount;
    private readonly record struct ClipboardWrite(TerminalSession Session, Dotty.Abstractions.Clipboard.ClipboardSelection Selection, string Text, int Bytes);
    private const int MaxOsc52WriteBytes = 786_432;
    private const int MaxPendingClipboardBytes = 4 * 1024 * 1024;
    private static int _pendingClipboardBytes;
    private static readonly Queue<ClipboardWrite> _pendingClipboards = new(8);
    private static int _pendingClipboardCount;
    private static readonly object _pendingClipboardsLock = new();
    private readonly record struct TabTitleChange(TerminalTab Tab);
    private static readonly Queue<TabTitleChange> _pendingTabTitleChanges = new(8);
    private static int _pendingTabTitleChangeCount;
    private static readonly object _pendingTabTitleChangesLock = new();
    private readonly record struct ProcessExit(TerminalTab Tab, LeafPane Leaf, int ExitCode);
    private static readonly Queue<ProcessExit> _pendingProcessExits = new(8);
    private static int _pendingProcessExitCount;
    private static readonly object _pendingProcessExitsLock = new();
    private static readonly ConcurrentQueue<ControlRequest> _pendingControlCommands = new();
    private static WindowLifecycleCoordinator _lifecycle = new();
    private static DesktopControlServer? _controlServer;
    private static readonly Dictionary<TerminalSession, Action> _renderSubscriptions = new();
    private static readonly Dictionary<TerminalSession, Action> _renderCallbackCache = new();
    private static readonly Dictionary<TerminalSession, Action<Dotty.Abstractions.Clipboard.ClipboardWriteRequest>> _clipboardSubscriptions = new();
    private static readonly Dictionary<TerminalSession, Action> _paletteSubscriptions = new();
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
    private static CancellationTokenSource? _systemThemeRefreshCancellation;
    private static Task? _systemThemeRefreshTask;
    private static int _themeApplyPending;
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
    private static long _lastInteractionTimestampMs;
    // Synchronized-update (CSI 2026) hold telemetry. nvim wraps every redraw
    // in a hold; while held, presents are withheld and the buffer advances
    // underneath. A long hold surfaces as frozen-then-jump. These counters
    // identify whether a reported freeze is a long producer hold (nvim-side
    // slowness) or withheld presents after the hold released (renderer-side).
    private static bool _syncHeld;
    // Next animated-image frame deadline of the last composed frame, in TimeProvider.System timestamp ticks (the
    // clock IKittyGraphicsHost.NowTicks reads); 0 = no animated image is visible.
    private static long _nextImageDeadlineTicks;
    private static long _syncHoldStartTimestampMs;
    private static long _syncHoldMaxDurationMs;
    private static long _syncHoldCount;
    private static long _syncHoldOver250MsCount;
    private static long _presentCount;
    private static ulong _presentedActiveGeneration;
    private static readonly ReusableTextBuffer _lastWindowTitle = new(128);
    private static char[] _windowTitleScratch = new char[128];
    private static byte[] _windowTitleUtf8 = new byte[128];
    private static global::Silk.NET.GLFW.Glfw? _glfwApi;
    private static string _windowBackend = "unknown";
    private static nint _glfwSetWindowTitleProc;
    private static bool _glfwTitleProcResolved;
    private const int LuaStatusRefreshIntervalMs = 1000;

    // Depth of the Silk frame action. A GLFW refresh callback re-enters it from
    // inside event dispatch, where glfwWaitEventsTimeout must not be called.
    private static int _frameDepth;

    private static bool _showTabBar = true;
    private static bool _customFrameActive;
    private static int _customFrameHitTestWidth;
    private static int _customFrameHitTestTabCount;
    private static int _customFrameHitTestActiveIndex = -1;
    private static float _customFrameHitTestBarHeight;
    private static float _customFrameHitTestStatusWidth;
    private static float _customFrameHitTestCaptionButtonsWidth;
    private static bool _customFrameHitTestGeometryValid;
    private static unsafe readonly delegate* managed<int, int, int> _customFrameHitTestCallback = &HitTestCustomFrame;
    private static unsafe readonly delegate* managed<int, void> _customFrameHoverCallback = &OnCustomFrameButtonHover;
    private static ContextMenuModel? _activeContextMenu;
    private sealed record ControlRequest(string Command, TaskCompletionSource<string> Completion);
    private static DottyLaunchOptions _launchOptions = new();
    private static TerminalSession? _initialCommandSession;
    private static int _exitCode;
    private static bool _windowRevealed;
    private static long _revealDeadlineMs = long.MaxValue;
    private const long RevealFallbackMs = 500;

    public static int Run(DottyLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        _initialCommandSession = null;
        _exitCode = 0;
        _windowRevealed = false;
        _revealDeadlineMs = long.MaxValue;
        _closed = false;
        Volatile.Write(ref _themeApplyPending, 0);
        _lastWindowFocus = null;
        _focusedPane = null;
        _lastPresentTimestampMs = 0;
        _lastInteractionTimestampMs = 0;
        _lifecycle = new WindowLifecycleCoordinator();
        UserConfigService.CallbackDispatcher = action => _lifecycle.TryEnqueue(action);
        UserConfigService.ConfigChanged += OnConfigChanged;
        UserConfigService.Load();
        if (UserConfigService.Current.ThemeAuto)
            SilkConfig.RefreshSystemTheme();
        SetCustomFrameMode(UserConfigService.Current.Window.Decorations);

        // Select GLFW directly instead of Silk's reflection-based backend discovery.
        global::Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
        InputWindowExtensions.ShouldLoadFirstPartyPlatforms(false);
        global::Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var options = WithStartOptions(WindowOptions.Default) with
        {
            // Shown by RevealWindowWhenReady once the first frame is presented, so the user
            // never sees an unpainted or wrongly sized window.
            IsVisible = false,
            VSync = false,
            ShouldSwapAutomatically = false,
            API = new global::Silk.NET.Windowing.GraphicsAPI(
                global::Silk.NET.Windowing.ContextAPI.OpenGL,
                global::Silk.NET.Windowing.ContextProfile.Core,
                global::Silk.NET.Windowing.ContextFlags.Default,
                new global::Silk.NET.Windowing.APIVersion(3, 3)),
        };

        try
        {
            _window = Window.Create(options);
        }
        catch
        {
            UserConfigService.ConfigChanged -= OnConfigChanged;
            UserConfigService.Shutdown();
            throw;
        }
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.FocusChanged += OnWindowFocusChanged;
        _window.Closing += OnClosing;
        _glfwApi ??= global::Silk.NET.GLFW.Glfw.GetApi();
        GlfwErrorLog.Install(_glfwApi);
        HostWake.Initialize(static () => _glfwApi!.PostEmptyEvent(), Environment.CurrentManagedThreadId);
        RequestRobustContext();
        RequestDesktopIdentityHints();
        try
        {
            _window.Initialize();
            _window.Run(RunFrame);
            _window.DoEvents();
            _window.Reset();
        }
        finally
        {
            HostWake.Shutdown();
        }
        return _exitCode;
    }
    private static bool IsTabBarVisible => _showTabBar || _customFrameActive;

    private static void SetCustomFrameMode(string decorations)
    {
        bool enabled = OperatingSystem.IsWindows() &&
            string.Equals(decorations, "custom", StringComparison.OrdinalIgnoreCase);
        if (_customFrameActive == enabled)
            return;

        _customFrameActive = enabled;
        _customFrameHitTestGeometryValid = false;
        if (_window != null)
        {
            if (enabled)
                InstallCustomFrame();
            else
                Win32WindowFrame.Uninstall();
        }
        WindowPresentationGate.Invalidate(WindowFrameReason.ThemeConfig);
    }

    private static unsafe void InstallCustomFrame()
    {
        Win32WindowFrame.Install(
            _window.Native!.Win32!.Value.Hwnd,
            _customFrameHitTestCallback,
            _customFrameHoverCallback);
    }

    private static void SetWindowClientSize(Vector2D<int> size)
    {
        if (_customFrameActive)
            Win32WindowFrame.SetClientSize(size.X, size.Y);
        else
            _window.Size = size;
    }

    // The window is created hidden. Called after every frame iteration: shows the window once
    // the first frame has been presented, or RevealFallbackMs after load if no frame could be
    // presented yet (a synchronized-update hold, an empty framebuffer), so it never stays hidden.
    // Costs one bool test per frame once shown.
    private static void RevealWindowWhenReady()
    {
        if (!_windowRevealed &&
            (_lastPresentTimestampMs != 0 || GetClockMilliseconds() >= _revealDeadlineMs))
        {
            RevealWindow();
        }
    }

    // One-shot show. The render-failure path calls it directly so a failing first frame never
    // leaves the window hidden. Invalidates a frame first because Wayland drops swaps on a hidden
    // surface (vendor/glfw/source/src/egl_context.c, swapBuffersEGL), so the first visible content
    // needs a present after the show; showing can re-enter the frame action through the GLFW
    // refresh callback, which then already sees the pending frame.
    private static void RevealWindow()
    {
        if (_windowRevealed || _closed)
            return;

        _windowRevealed = true;
        WindowPresentationGate.Invalidate(WindowFrameReason.Initial);
        _window.IsVisible = true;
        ApplyStartWindowState();
    }

    private static void PublishCustomFrameHitTestGeometry(
        int framebufferWidth,
        float barHeight,
        float statusWidth)
    {
        _customFrameHitTestWidth = framebufferWidth;
        _customFrameHitTestBarHeight = barHeight;
        _customFrameHitTestStatusWidth = statusWidth;
        _customFrameHitTestCaptionButtonsWidth = _customFrameActive
            ? Math.Min(framebufferWidth,
                TabBarLayout.CaptionButtonWidth * TabBarLayout.CaptionButtonCount * _display.ContentScale)
            : 0f;
        _customFrameHitTestTabCount = _tabManager?.Count ?? 0;
        _customFrameHitTestActiveIndex = _tabManager?.ActiveIndex ?? -1;
        _customFrameHitTestGeometryValid =
            _customFrameActive && framebufferWidth > 0 && barHeight > 0f;
    }

    private static int HitTestCustomFrame(int x, int y)
    {
        if (!_customFrameHitTestGeometryValid)
            return (int)TabBarHitType.None;

        return (int)TabBarHitTester.HitTest(
            x,
            y,
            _customFrameHitTestWidth,
            _customFrameHitTestTabCount,
            _customFrameHitTestActiveIndex,
            out _,
            _customFrameHitTestBarHeight,
            _customFrameHitTestStatusWidth,
            _customFrameHitTestCaptionButtonsWidth);
    }

    private static void OnCustomFrameButtonHover(int hitType)
    {
        if (_closed || _mouseController == null)
            return;

        _mouseController.SetCaptionButtonHover((TabBarHitType)hitType);
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
    }


    private static void OnLoad()
    {
        try
        {
            OnLoadCore();
        }
        catch (Exception exception)
        {
            _exitCode = 1;
            Console.Error.WriteLine(GraphicsCapabilities.DescribeInitializationFailure(exception));
            LogGraphicsInfoOnInitFailure();
            _window.Close();
        }
    }

    private static void OnLoadCore()
    {
        _runtimeFontSize = null;
        WindowPresentationGate.Invalidate(WindowFrameReason.Initial);
        _lastCursorBlinkTimestampMs = GetClockMilliseconds();
        _tabManager = new TerminalTabManager();
        _tabManager.ProcessExited += OnTabProcessExited;
        _tabManager.ActiveTabChanged += OnActiveTabChanged;
        _tabManager.TabAdded += OnTabAdded;
        _tabManager.TabClosed += OnTabClosed;
        _tabManager.TabTitleChanged += OnTabTitleChanged;
        AttachBellNotifications(_tabManager);
        var luaServices = new DottyLuaServices(
            action => { _lifecycle.TryEnqueue(action); },
            CreateLuaTab,
            SplitLuaPane,
            action => _keyboardDispatcher?.Actions.TryExecute(action) ?? false,
            () => _lifecycle.TryEnqueue(() => ApplyConfigOnly(UserConfigService.Current)),
            InvalidateLuaPresentation);
        _luaHost = new LuaScriptHost(luaServices, _tabManager);
        _luaHost.ScriptFileChanged += UserConfigService.RequestReload;
        _luaHost.Evaluate(UserConfigService.Current, LuaScriptHost.GetConfigLuaPath());
        InvalidateLuaPresentation();
        _showTabBar = UserConfigService.Current.TabBar.Show;
        _activeTheme = SilkConfig.LoadActiveTheme();
        (_themeForeground, _themeBackground) = SilkConfig.InitializeTheme(theme: _activeTheme);
        _themeSelectionColor = SilkConfig.ResolveSelectionColor(_activeTheme);

        _gl = _window.CreateOpenGL();
        CaptureGraphicsInfo(_gl);
        if (!GraphicsCapabilities.IsOpenGlVersionSupported(_glVersion))
            throw new PlatformNotSupportedException(GraphicsCapabilities.DescribeUnsupportedVersion(_glVersion));
        ConfigureGlDebug(_gl);
        ConfigureFramePacing();
        ConfigureRenderRecovery();

        _glfwApi ??= global::Silk.NET.GLFW.Glfw.GetApi();
        NativeGlfwMetadata.Initialize(_glfwApi);
        _displayPlatform = NativeGlfwMetadata.TryGetPlatform(out int platform) ? (DisplayPlatform)platform : DisplayPlatform.Unknown;
        ApplyLoadedWindowOptions();
        ResolveFontAndMetrics();
        InstallContentScaleCallback();
        _atlas = GlyphAtlasService.GetOrCreateAtlas(_typeface, _cellFontSizePx(), fallbackChain: _fontChain, rasterPolicy: CurrentRasterPolicy());
        GlyphAtlasService.AcquireAtlas(_atlas);
        _renderer = new SilkTerminalRenderer(_gl, _atlas);
        _sceneComposer = new TerminalSceneComposer(_atlas, _typeface, _cellFontSizePx()) { BaselineShift = _cellMetrics.BaselineShift };
        ApplyLiveWindowOptions(UserConfigService.Current);

        _mouseHost = new MouseHost();
        _keyboardDispatcher = new TerminalKeyboardDispatcher(_mouseHost);
        _windowBackend = _displayPlatform switch
        {
            DisplayPlatform.Win32 => "windows",
            DisplayPlatform.Cocoa => "macos",
            DisplayPlatform.Wayland => "wayland",
            DisplayPlatform.X11 => "x11",
            DisplayPlatform.Null => "null",
            _ => "unknown"
        };
        _keyboardController = new TerminalKeyboardController(
            keyEventReceived: _keyboardDispatcher.HandleKeyEvent,
            textReceived: _keyboardDispatcher.HandleCommittedText,
            activity: OnKeyboardActivity,
            primaryCodepointProvider: static (key, scancode) =>
                scancode >= 0 && NativeGlfwMetadata.TryGetPrimaryCodePoint((int)key, scancode, out uint scalar) ? (int)scalar : 0);
        _mouseController = new TerminalMouseController(_mouseHost);

        _primary = new NativeSelection(_glfwApi!);
        _input = _window.CreateInput();
        if (_input.Keyboards.Count > 0)
        {
            _keyboard = _input.Keyboards[0];
            _clipboard = new KeyboardClipboard(_keyboard);
            _pasteRouter = new ClipboardPasteRouter(
                _clipboard,
                static () => UserConfigService.Current.Clipboard,
                static prompt => _mouseHost.ShowPrompt(prompt),
                static pane => IsPaneAlive(pane));
            _primaryPasteRouter = new ClipboardPasteRouter(
                new PrimarySelectionClipboard(_primary),
                static () => UserConfigService.Current.Clipboard,
                static prompt => _mouseHost.ShowPrompt(prompt),
                static pane => IsPaneAlive(pane));
        }
        InstallNativeKeyboardCallbacks();
        _nativeTextInput = new NativeTextInputBridge(_glfwApi!, _window.Native?.Glfw ?? 0, OnNativePreeditChanged);
        _initialWindowInputFocus = NativeGlfwMetadata.IsWindowFocused(_window.Native?.Glfw ?? 0);
        _nativeTextInput.SetFocus(_initialWindowInputFocus);

        if (_input.Mice.Count > 0)
        {
            _mouse = _input.Mice[0];
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.MouseMove += OnMouseMove;
            _mouse.Scroll += OnMouseScroll;
        }

        var initialTab = _tabManager.CreateTab(
            cols: _cols, rows: _rows,
            workingDirectory: _launchOptions.WorkingDirectory,
            shell: _launchOptions.Shell,
            command: _launchOptions.Command,
            shellIsExecutable: _launchOptions.Shell != null);
        SilkConfig.ApplyConfigToAdapter(initialTab.Session.Adapter, _activeTheme);
        if (_launchOptions.Command != null)
            _initialCommandSession = initialTab.Session;
        ApplyKeybindings(UserConfigService.Current);
        if (_customFrameActive)
            InstallCustomFrame();
        var (clientWidth, clientHeight) = ClientSizeFor(_cols, _rows);
        SetWindowClientSize(new Vector2D<int>(clientWidth, clientHeight));
        _revealDeadlineMs = GetClockMilliseconds() + RevealFallbackMs;
        VtHostBridge.Attach(_window, _tabManager,
            static () => new VtHostLayout(_cellWidthPx, _cellHeightPx, CurrentBarRows(), CurrentDevicePadding(), _display.WindowUnitsToDevice, _cols, _rows),
            static (width, height) => SetWindowClientSize(new Vector2D<int>(width, height)));
        LogGraphicsInfoIfRequested();
        StartControlServer();
        StartSystemThemeRefresher();
        _luaHost.NotifyGuiStartup();
    }

    private static void OnActiveTabChanged(TerminalTab? tab)
    {
        _nativeTextInput?.Cancel();
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
            _pendingProcessExits.Enqueue(new ProcessExit(tab, leaf, exitCode));
            Volatile.Write(ref _pendingProcessExitCount, _pendingProcessExits.Count);
        }
        HostWake.Request();
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
        HostWake.Request();

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
        HostWake.Request();
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
        _nativeTextInput?.Cancel();
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
        VtHostBridge.OnSessionsChanged();
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

            // Split panes are created outside the six ApplyConfigToAdapter sites; give them the OSC 8 ingest switch,
            // the vt switches and the Kitty graphics settings on first sight.
            HyperlinkIngestConfig.Apply(session.Adapter, UserConfigService.Current.Hyperlinks);
            VtAdapterConfig.Apply(session.Adapter, UserConfigService.Current.Vt);
            SilkConfig.ApplyGraphicsToAdapter(session.Adapter);

            Action<Dotty.Abstractions.Clipboard.ClipboardWriteRequest> callback = CreateClipboardHandler(session);
            session.ClipboardWriteRequested += callback;
            _clipboardSubscriptions.Add(session, callback);
        }

        foreach (var session in _sessionScratch)
        {
            if (_clipboardReadSubscriptions.ContainsKey(session))
                continue;

            Action<Dotty.Abstractions.Clipboard.ClipboardReadRequest> readCallback = CreateClipboardReadHandler(session);
            session.ClipboardReadRequested += readCallback;
            _clipboardReadSubscriptions.Add(session, readCallback);
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

        _staleSessionScratch.Clear();
        foreach (var pair in _clipboardReadSubscriptions)
        {
            if (!_sessionScratch.Contains(pair.Key))
                _staleSessionScratch.Add(pair.Key);
        }
        for (int i = 0; i < _staleSessionScratch.Count; i++)
        {
            var session = _staleSessionScratch[i];
            session.ClipboardReadRequested -= _clipboardReadSubscriptions[session];
            _clipboardReadSubscriptions.Remove(session);
            _clipboardReads.RemoveSession(session);
        }
    }

    private static Action<Dotty.Abstractions.Clipboard.ClipboardWriteRequest> CreateClipboardHandler(TerminalSession session) =>
        request => EnqueueClipboardWrite(session, request);

    private static Action<Dotty.Abstractions.Clipboard.ClipboardReadRequest> CreateClipboardReadHandler(TerminalSession session) =>
        request => _clipboardReads.OnRequested(session, request);

    private static void EnqueueClipboardWrite(TerminalSession session, Dotty.Abstractions.Clipboard.ClipboardWriteRequest request)
    {
        // Primary is honored only when it is the system's (X11); elsewhere primary-only writes are dropped so they
        // never clobber the clipboard.
        var selection = request.Selection & Dotty.Abstractions.Clipboard.ClipboardSelection.Clipboard;
        if (_primary is { IsSystemSelection: true })
            selection |= request.Selection & Dotty.Abstractions.Clipboard.ClipboardSelection.Primary;
        if (selection == Dotty.Abstractions.Clipboard.ClipboardSelection.None)
            return;
        if (!UserConfigService.Current.Clipboard.AllowOsc52Write)
            return;
        string text = request.Text;
        if (text.Length > MaxOsc52WriteBytes)
            return;
        int bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxOsc52WriteBytes)
            return;
        lock (_pendingClipboardsLock)
        {
            if (_closed)
                return;
            // Coalesce: latest write of a session replaces its pending one.
            if (_pendingClipboards.Count > 0)
            {
                bool replaced = false;
                int n = _pendingClipboards.Count;
                for (int i = 0; i < n; i++)
                {
                    var item = _pendingClipboards.Dequeue();
                    if (!replaced && ReferenceEquals(item.Session, session))
                    {
                        _pendingClipboardBytes -= item.Bytes;
                        replaced = true;
                        continue;
                    }
                    _pendingClipboards.Enqueue(item);
                }
            }
            if (_pendingClipboards.Count >= 32 || _pendingClipboardBytes + bytes > MaxPendingClipboardBytes)
            {
                Volatile.Write(ref _pendingClipboardCount, _pendingClipboards.Count);
                return;
            }
            _pendingClipboards.Enqueue(new ClipboardWrite(session, selection, text, bytes));
            _pendingClipboardBytes += bytes;
            Volatile.Write(ref _pendingClipboardCount, _pendingClipboards.Count);
        }
        HostWake.Request();

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
            if (!_paletteSubscriptions.ContainsKey(leaf.Session))
            {
                leaf.Session.PaletteChanged += OnSessionPaletteChanged;
                _paletteSubscriptions.Add(leaf.Session, OnSessionPaletteChanged);
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
            if (_paletteSubscriptions.Remove(session))
                session.PaletteChanged -= OnSessionPaletteChanged;
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

    private static void OnSessionPaletteChanged() =>
        WindowPresentationGate.Invalidate(WindowFrameReason.ThemeConfig);
    // font.size and zoom are points; the atlas and chrome text want device pixels.
    private static float _cellFontSizePx() => CellMetricsCalculator.PointsToPixels(EffectiveFontSize(), _display.ContentScale);

    private static float EffectiveFontSize()
    {
        double configured = double.IsFinite(_configuredFontSize)
            ? _configuredFontSize
            : UserConfigService.Current.Font.Size;
        double size = _runtimeFontSize ?? configured;
        return float.IsFinite((float)size) ? Math.Clamp((float)size, 1f, 512f) : (float)DottyDefaults.FontSize;
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
                FormatShellCommandStatus();
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

    private static void FormatShellCommandStatus()
    {
        var adapter = _tabManager?.ActiveTab?.Session.Adapter;
        if (adapter == null)
        {
            _luaStatusText.Clear();
            return;
        }

        var command = adapter.GetShellCommandSnapshot();
        if (command.State == ShellCommandState.Running)
        {
            _luaStatusText.Set("running");
        }
        else if (command.State == ShellCommandState.Exited)
        {
            Span<char> text = stackalloc char[16];
            "exit ".AsSpan().CopyTo(text);
            command.ExitCode.TryFormat(text[5..], out int written);
            _luaStatusText.Set(text[..(5 + written)]);
        }
        else
        {
            _luaStatusText.Clear();
        }
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
        // After a driver reset the title carries the "rendering stopped" notice instead.
        if (RenderingStopped)
            return;

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
                _glfwApi ??= global::Silk.NET.GLFW.Glfw.GetApi();
                _glfwSetWindowTitleProc = _glfwApi.Context.GetProcAddress("glfwSetWindowTitle");
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
    // Rejected chords and actions are logged, never dropped silently (+validate-config lists them too).
    private static void ApplyKeybindings(DottyUserConfig config)
    {
        _keybindings.RegisterDefaults();
        _keybindings.ApplyCustomBindings(config.Keybindings);
        foreach (var diagnostic in _keybindings.Diagnostics)
            Console.Error.WriteLine("dotty: " + diagnostic);
    }

    private static void ApplyConfigOnly(DottyUserConfig config)
    {
        if (_closed) return;
        _runtimeFontSize = null;
        SetCustomFrameMode(config.Window.Decorations);
        _showTabBar = config.TabBar.Show;
        WindowPresentationGate.Invalidate(WindowFrameReason.ThemeConfig);
        ApplyKeybindings(config);
        ResolveFontAndMetrics();
        RefreshFontResources();
        var size = _window.FramebufferSize;
        if (size.X > 0 && size.Y > 0) ApplyFramebufferLayout(size);
        SilkConfig.ClearThemeCache();
        _activeTheme = SilkConfig.LoadActiveTheme();
        foreach (var tab in _tabManager.Tabs)
            foreach (var pane in tab.PaneTree.Leaves)
                SilkConfig.ApplyConfigToAdapter(pane.Session.Adapter, _activeTheme);
        (_themeForeground, _themeBackground) = SilkConfig.InitializeTheme(theme: _activeTheme);
        _themeSelectionColor = SilkConfig.ResolveSelectionColor(_activeTheme);
        ApplyLiveWindowOptions(config);
    }
    private static void StartSystemThemeRefresher()
    {
        _systemThemeRefreshCancellation = new CancellationTokenSource();
        var token = _systemThemeRefreshCancellation.Token;
        _systemThemeRefreshTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    // Polling spawns a helper process; skip it while the window is iconified (nothing is drawn).
                    if (_closed || !UserConfigService.Current.ThemeAuto || Volatile.Read(ref _windowIconified)) continue;
                    if (!SilkConfig.RefreshSystemTheme()) continue;
                    if (Interlocked.CompareExchange(ref _themeApplyPending, 1, 0) != 0) continue;
                    if (!_lifecycle.TryEnqueue(() =>
                    {
                        Volatile.Write(ref _themeApplyPending, 0);
                        if (!_closed) ApplyConfigOnly(UserConfigService.Current);
                    }))
                        Volatile.Write(ref _themeApplyPending, 0);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, token);
    }


    private static TerminalTab CreateLuaTab(string? workingDirectory, string? shell)
    {
        var activeSession = _tabManager.ActiveTab?.Session;
        var tab = _tabManager.CreateTab(cols: _cols, rows: _rows,
            workingDirectory: workingDirectory ?? activeSession?.InheritableWorkingDirectory,
            shell: shell ?? activeSession?.LaunchShell ?? _launchOptions.Shell,
            shellIsExecutable: shell is null && (activeSession?.LaunchShellIsExecutable ?? (_launchOptions.Shell is not null)));
        SilkConfig.ApplyConfigToAdapter(tab.Session.Adapter);
        return tab;
    }

    private static LeafPane SplitLuaPane(TerminalTab tab, LeafPane target, SplitDirection direction, string? workingDirectory, string? shell)
    {
        var pane = tab.PaneTree.Split(target, direction, workingDirectory ?? target.Session.InheritableWorkingDirectory, shell);
        SilkConfig.ApplyConfigToAdapter(pane.Session.Adapter);
        return pane;
    }


    // Config, zoom and load path. The fallback chain (and with it the primary typeface) is rebuilt only when the
    // font model (font.family/boldFamily/italicFamily/boldItalicFamily/weight/features) changes, so the typeface
    // object identity (which keys the glyph atlas and the composer row cache) is stable.
    private static void ResolveFontAndMetrics()
    {
        var config = UserConfigService.Current;
        _configuredFontSize = double.IsFinite(config.Font.Size) ? config.Font.Size : DottyDefaults.FontSize;
        var font = config.Font;
        var fontErrors = new List<string>();
        var fontOptions = FontOptions.Create(font.Family, font.BoldFamily, font.ItalicFamily, font.BoldItalicFamily,
            font.Weight, font.Features, fontErrors);
        var chain = _fontChains.Resolve(fontOptions);
        if (!ReferenceEquals(chain, _fontChain))
        {
            foreach (string error in fontErrors)
                Console.Error.WriteLine("dotty: " + error);
            _fontChain = chain;
        }
        _typeface = chain.PrimaryTypeface;
        UpdateScaleAndCellMetrics();
    }

    // Resize and content-scale path: re-reads the display metrics. MeasureCell is cached by value,
    // so an unchanged scale costs a dictionary hit and no Skia work.
    private static void UpdateScaleAndCellMetrics()
    {
        nint window = _window.Native?.Glfw ?? 0;
        NativeGlfwMetadata.TryGetWindowContentScale(window, out float contentScaleX, out float contentScaleY);
        var windowSize = _window.Size;
        var framebufferSize = _window.FramebufferSize;
        _display = DisplayScale.Compute(
            _displayPlatform,
            windowSize.X, windowSize.Y,
            framebufferSize.X, framebufferSize.Y,
            contentScaleX, contentScaleY,
            _display);
        var font = UserConfigService.Current.Font;
        _cellMetrics = FontMetricsService.MeasureCell(_typeface, new CellMetricsOptions(
            _cellFontSizePx(),
            font.LineHeight,
            _display.ContentScale,
            CellAdjustment.Parse(font.AdjustCellWidth),
            CellAdjustment.Parse(font.AdjustCellHeight),
            CurrentRasterPolicy()));
        _cellWidthPx = _cellMetrics.CellWidth;
        _cellHeightPx = _cellMetrics.CellHeight;
        if (_sceneComposer != null)
            _sceneComposer.BaselineShift = _cellMetrics.BaselineShift;
        _cellWidth = _cellWidthPx / _display.ContentScale;
        _cellHeight = _cellHeightPx / _display.ContentScale;
    }

    /// <summary><c>font.hinting</c>/<c>font.antialias</c>: one policy for cell metrics and the glyph atlas.</summary>
    private static RasterPolicy CurrentRasterPolicy()
    {
        var font = UserConfigService.Current.Font;
        return RasterPolicy.FromConfig(font.Hinting, font.Antialias);
    }

    private static int CurrentBarRows() =>
        IsTabBarVisible ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;

    private static DevicePadding CurrentDevicePadding()
    {
        var padding = UserConfigService.Current.Window.Padding;
        return DevicePadding.FromLogical(padding.Left, padding.Top, padding.Right, padding.Bottom, _display.ContentScale);
    }

    /// <summary>Window client size (window units) that holds the given grid at the current metrics.</summary>
    private static (int Width, int Height) ClientSizeFor(int columns, int rows) =>
        GridFit.ClientSize(columns, rows, _cellWidthPx, _cellHeightPx, CurrentBarRows(), CurrentDevicePadding(), _display.WindowUnitsToDevice);

    private static unsafe void InstallContentScaleCallback() =>
        NativeGlfwMetadata.TrySetWindowContentScaleCallback(_window.Native?.Glfw ?? 0, &OnNativeContentScaleChanged);

    // GLFW event thread: a monitor move or display-scale change, possibly without a framebuffer
    // resize (macOS, Wayland). Coalesce and rebuild metrics and atlas on the UI thread.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeContentScaleChanged(nint window, float x, float y)
    {
        if (Interlocked.Exchange(ref _contentScaleChangePending, 1) != 0)
            return;
        if (!_lifecycle.TryEnqueue(ApplyContentScaleChange))
            Volatile.Write(ref _contentScaleChangePending, 0);
    }

    private static void ApplyContentScaleChange()
    {
        Volatile.Write(ref _contentScaleChangePending, 0);
        if (_closed)
            return;
        WindowPresentationGate.Invalidate(WindowFrameReason.Resize);
        var previous = _display;
        UpdateScaleAndCellMetrics();
        if (DisplayScale.ContentScaleChanged(previous, _display))
            RefreshFontResources();
        var size = _window.FramebufferSize;
        if (size.X > 0 && size.Y > 0)
            ApplyFramebufferLayout(size);
    }

    private static void RefreshFontResources()
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Atlas);
        var newAtlas = GlyphAtlasService.GetOrCreateAtlas(_typeface, _cellFontSizePx(), fallbackChain: _fontChain, rasterPolicy: CurrentRasterPolicy());
        ApplyConfiguredSymbolMap(newAtlas);
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

    // Parses font.symbolMap entries into the atlas fallback chain. A fresh
    // map replaces the previous one; an empty list clears overrides.
    private static void ApplyConfiguredSymbolMap(GlyphAtlas atlas)
    {
        if (atlas == null)
            return;
        var entries = UserConfigService.Current.Font.SymbolMap;
        if (entries == null || entries.Count == 0)
        {
            var chain = atlas.FallbackChain;
            if (chain != null)
                chain.SymbolMap = null;
            return;
        }
        var map = SymbolMap.Parse(entries);
        var target = atlas.FallbackChain ?? _fontChain;
        target.SymbolMap = map.Count > 0 ? map : null;
        atlas.FallbackChain = target;
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Resize);
        if (size.X <= 0 || size.Y <= 0) return;

        var previous = _display;
        UpdateScaleAndCellMetrics();
        if (DisplayScale.ContentScaleChanged(previous, _display))
            RefreshFontResources();

        ApplyFramebufferLayout(size);
    }

    private static void ApplyFramebufferLayout(Vector2D<int> size)
    {
        _gl.Viewport(size);
        (_cols, _rows) = GridFit.Compute(size.X, size.Y, _cellWidthPx, _cellHeightPx, CurrentBarRows(), CurrentDevicePadding());
        // Physical text-area pixels: the same integer cell size the layout and the VT metrics use.
        _tabManager?.ResizeAll(_cols, _rows, _cols * _cellWidthPx, _rows * _cellHeightPx);
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
        HostWake.Request();
        return completion.Task;
    }

    private static void DrainControlCommands()
    {
        while (_pendingControlCommands.TryDequeue(out var request))
        {
            if (TryDeferControlCommand(request.Command, request.Completion))
                continue;
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
        if (string.Equals(command, "GL", StringComparison.OrdinalIgnoreCase))
            return BuildGraphicsInfoJson();
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
        if (string.Equals(command, "MEMORY", StringComparison.OrdinalIgnoreCase))
        {
            // Ownership capacities and counters are not resident-memory or live-object measurements; the image
            // texture figure is an estimate of uploaded texture bytes, not driver RSS.
            var lastGc = GC.GetGCMemoryInfo();
            return MemoryReport.ToJson(new MemoryReportInputs(
                ManagedHeapEstimateBytes: GC.GetTotalMemory(forceFullCollection: false),
                LastGcHeapSizeBytes: lastGc.HeapSizeBytes,
                LastGcCommittedBytes: lastGc.TotalCommittedBytes,
                LastGcIndex: lastGc.Index,
                Buffers: BufferMemoryMetrics.GetSnapshot(),
                ArenaBackend: BufferMemoryMetrics.ArenaBackend,
                AtlasBitmapBytes: GlyphAtlasService.TotalBytes,
                AtlasCount: GlyphAtlasService.AtlasCount,
                R8TexturePayloadBytesEstimate: _renderer?.R8TexturePayloadBytes ?? 0,
                Rgba8TexturePayloadBytesEstimate: _renderer?.Rgba8TexturePayloadBytes ?? 0,
                IntrinsicColorProbeScratchBytes: SilkTerminalRenderer.IntrinsicColorProbeScratchBytes,
                Graphics: Dotty.Terminal.Graphics.GraphicsMemoryMetrics.GetSnapshot(),
                ImageTextureBytesEstimate: _renderer?.EstimatedImageTextureBytes ?? 0));
        }
        if (string.Equals(command, "IME", StringComparison.OrdinalIgnoreCase))
        {
            var preedit = _nativeTextInput?.Preedit ?? ImePreeditState.Empty;
            return $"{{\"backend\":{QuoteJson(_windowBackend)},\"preedit\":{QuoteJson(preedit.Text ?? string.Empty)},\"caretUtf16\":{preedit.CaretIndex},\"selectionStartUtf16\":{preedit.SelectionStart},\"selectionLengthUtf16\":{preedit.SelectionLength}}}";
        }
        if (string.Equals(command, "STATS", StringComparison.OrdinalIgnoreCase))
        {
            int tabCount = _tabManager?.Count ?? 0;
            int activeIndex = _tabManager?.ActiveIndex ?? -1;
            long skipped = _sceneComposer?.SkippedLeafFrames ?? 0;
            var session = _tabManager?.ActiveTab?.Session;
            long bytesRead = session?.PtyBytesRead ?? 0;
            long bytesParsed = session?.PtyBytesParsed ?? 0;
            int pendingChunks = session?.PendingOutputChunks ?? 0;
            string modelGeneration = session != null && TryReadGeneration(session.Adapter.Buffer, out ulong generation)
                ? generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            bool syncActive = session?.Adapter.SynchronizedUpdateActive ?? false;
            var json = new StringBuilder(1024);
            json.Append($"{{\"windowBackend\":{QuoteJson(_windowBackend)},\"tabs\":{tabCount},\"activeTab\":{activeIndex},\"skippedLeafFrames\":{skipped},\"syncHeld\":{(_syncHeld ? "true" : "false")},\"syncHoldCount\":{_syncHoldCount},\"syncHoldMaxMs\":{_syncHoldMaxDurationMs},\"syncHoldOver250Ms\":{_syncHoldOver250MsCount},\"syncActive\":{(syncActive ? "true" : "false")},\"ptyBytesRead\":{bytesRead},\"ptyBytesParsed\":{bytesParsed},\"pendingOutputChunks\":{pendingChunks},\"modelGeneration\":{modelGeneration},\"presentedGeneration\":{_presentedActiveGeneration},\"presentCount\":{_presentCount},\"frameTimings\":");
            AppendFrameTimingsJson(json);
            AppendFramePacingJson(json);
            AppendRenderRecoveryJson(json);
            return json.Append('}').ToString();
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
            "KEY" => SendControlKey(payload),
            "RESIZE" => ResizeFromControl(payload),
            "ACTION" => ExecuteControlAction(payload),
            _ => "ERROR unknown command",
        };
    }

    private static string ExecuteControlAction(string name)
    {
        if (!ActionRegistry.TryParse(name, out var action, out _) || action.Action == TerminalAction.None)
            return "ERROR unknown action";
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.TabOrPane);
        return _keyboardDispatcher.Actions.TryExecute(action) ? "OK" : "ERROR action unavailable";
    }

    private static string SendControlText(TerminalTab activeTab, string text)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
        _keyboardDispatcher.HandleText(text);
        return "OK";
    }

    private static string SendControlKey(string keyName)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
        string normalized = keyName.Trim().ToLowerInvariant();
        if (normalized is "ctrlc" or "control-c")
        {
            _keyboardController.HandleKeyDown(InputKey.ControlLeft, -1);
            _keyboardController.HandleKeyDown(InputKey.C, -1);
            _keyboardController.HandleKeyUp(InputKey.C, -1);
            _keyboardController.HandleKeyUp(InputKey.ControlLeft, -1);
            return "OK";
        }
        normalized = normalized switch { "return" => "Enter", "esc" => "Escape", _ => normalized };
        if (!Enum.TryParse<InputKey>(normalized, ignoreCase: true, out var key) || !Enum.IsDefined(key))
            return "ERROR unknown key";
        _keyboardController.HandleKeyDown(key, -1);
        _keyboardController.HandleKeyUp(key, -1);
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

        var (width, height) = ClientSizeFor(columns, rows);
        SetWindowClientSize(new Vector2D<int>(width, height));
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
        var logicalSize = _window.Size;
        var framebufferSize = _window.FramebufferSize;
        var command = activeTab.Session.Adapter.GetShellCommandSnapshot();
        string commandState = command.State switch { ShellCommandState.Running => "running", ShellCommandState.Exited => "exited", _ => "idle" };
        return $"{{\"windowState\":{(int)_window.WindowState},\"logicalWidth\":{logicalSize.X},\"logicalHeight\":{logicalSize.Y},\"framebufferWidth\":{framebufferSize.X},\"framebufferHeight\":{framebufferSize.Y},\"rows\":{buffer.Rows},\"cols\":{buffer.Columns},\"cursorRow\":{buffer.CursorRow},\"cursorCol\":{buffer.CursorCol},\"scrollbackLines\":{buffer.ScrollbackCount},\"isAlternateScreen\":{(buffer.IsAlternateScreenActive ? "true" : "false")},\"paneCount\":{activeTab.PaneTree.Leaves.Count},\"workingDirectory\":{QuoteJson(activeTab.Session.CurrentWorkingDirectory ?? string.Empty)},\"title\":{QuoteJson(activeTab.Title)},\"shellCommandState\":{QuoteJson(commandState)},\"shellExitCode\":{command.ExitCode},\"statusText\":{QuoteJson(GetLuaStatusText())}}}";
    }
    private static string QuoteJson(ReadOnlySpan<char> value)
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
        // Key events and the characters they produce arrive in the same GLFW event batch; key events still
        // waiting for a character after the batch are encoded from the key now.
        _keyboardDispatcher?.FlushDeferredKeys();
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
            {
                if (ReferenceEquals(processExit.Leaf.Session, _initialCommandSession))
                    _exitCode = processExit.ExitCode;
                _tabManager?.CloseExitedPane(processExit.Tab, processExit.Leaf);
            }
        }

        while (TryDequeuePendingTitle(out var title))
        {
            if (!_closed)
                UpdateWindowTitle(title);
        }

        while (TryDequeueClipboardWrite(out var request))
        {
            if (_closed || _keyboard is null || !UserConfigService.Current.Clipboard.AllowOsc52Write)
                continue;

            if (!TryFindOwningPane(request.Session, out var ownerTab, out var ownerPane))
                continue;
            if (!_luaHost.Hooks.AllowClipboardWrite(ownerPane, ownerTab, request.Text))
                continue;

            if ((request.Selection & Dotty.Abstractions.Clipboard.ClipboardSelection.Clipboard) != 0)
                _clipboard?.SetText(request.Text);
            if ((request.Selection & Dotty.Abstractions.Clipboard.ClipboardSelection.Primary) != 0 && _primary is { IsSystemSelection: true } primary)
                primary.Set(request.Text);
        }

        _clipboardReads.Drain();
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
            _pendingClipboardBytes -= request.Bytes;
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
            RevealWindowWhenReady();
        }
        catch (Exception exception)
        {
            RevealWindow();
            // Never close the window or end the shells on a render failure: retry, rebuild, then ask.
            if (!_closed)
                HandleRenderFailure(exception);
        }
    }

    private static void RunFrame()
    {
        _frameDepth++;
        try
        {
            _window.DoEvents();
            if (!_window.IsClosing)
                _window.DoUpdate();
            if (!_window.IsClosing)
                _window.DoRender();
        }
        finally
        {
            _frameDepth--;
        }
    }

    /// <summary>
    /// Whether the shared blink timer runs: the last composed frame drew a blinking cursor or SGR 5/6 text (see
    /// <see cref="TerminalSceneComposer.BlinkActive"/>) and the window is not known to be unfocused. While it is idle
    /// the blink phase stays on, so an unfocused window shows a steady cursor and steady text.
    /// </summary>
    private static bool CursorBlinks(bool hasActiveTab) =>
        hasActiveTab && _lastWindowFocus != false && (_sceneComposer?.BlinkActive ?? false);

    /// <summary>
    /// The shared blink phase (<see cref="_cursorBlinkVisible"/>). <see cref="_lastCursorBlinkTimestampMs"/> is the
    /// start of the current on or off segment; input and focus changes restart an on segment.
    /// <see cref="BlinkPolicy.Steady"/> while the timer is idle.
    /// </summary>
    private static BlinkPolicy CurrentBlinkPolicy(bool hasActiveTab)
    {
        if (!CursorBlinks(hasActiveTab))
            return BlinkPolicy.Steady;
        int interval = Math.Max(100, UserConfigService.Current.Cursor.BlinkIntervalMs);
        long onSegmentStart = _cursorBlinkVisible ? _lastCursorBlinkTimestampMs : _lastCursorBlinkTimestampMs - interval;
        return new BlinkPolicy(onSegmentStart, interval);
    }

    /// <summary>
    /// Blocks until input, a cross-thread wake, or the nearest deadline. Replaces a
    /// fixed 1 ms sleep: input and output end the wait immediately, so nothing is
    /// delayed after them, while an idle window wakes only for its next deadline.
    /// </summary>
    private static void WaitForWork(
        bool includeBlink,
        bool retryShortly = false,
        long coalesceRemainingMs = 0,
        long syncHoldRemainingMs = 0,
        bool includeImageAnimation = false)
    {
        if (_frameDepth > 1 || (!retryShortly && HostWake.IsPending))
            return;

        long now = GetClockMilliseconds();
        int timeoutMs = IdleWait.Compute(new IdleWaitInputs(
            NowMs: now,
            RetryShortly: retryShortly,
            NextBlinkToggleMs: NextBlinkToggleMs(includeBlink, now),
            NextLuaStatusRefreshMs: NextLuaStatusRefreshMs(),
            CoalesceRemainingMs: coalesceRemainingMs,
            SyncHoldRemainingMs: syncHoldRemainingMs,
            SelectionAutoscrollActive: _mouseController?.SelectionAutoscrollActive == true,
            ImageAnimationRemainingMs: includeImageAnimation
                ? IdleWait.ImageAnimationRemainingMs(
                    _nextImageDeadlineTicks, TimeProvider.System.GetTimestamp(), TimeProvider.System.TimestampFrequency)
                : 0,
            ControlWaitRemainingMs: IdleWaiterRemainingMs(now),
            BellFlashRemainingMs: BellFlashRemainingMs(now)));
        if (timeoutMs > 0)
            _glfwApi!.WaitEventsTimeout(timeoutMs / 1000.0);
    }

    private static void OnRenderCore(double delta)
    {
        HostWake.Reset();
        _nativeTextInput?.ThrowPendingCallbackError();
        DrainWindowEvents();
        if (_closed)
            return;
        VtHostBridge.Tick(GetClockMilliseconds());
        if (_mouseController?.TickSelectionAutoscroll() == true)
            WindowPresentationGate.Invalidate(WindowFrameReason.Selection);
        var tabManager = _tabManager;
        var activeTab = tabManager.ActiveTab;
        long now = GetClockMilliseconds();
        ExpireIdleWaiters(now);
        if (now >= NextLuaStatusRefreshMs())
            RefreshLuaStatus();
        // Before the sync-hold and minimized-window early returns: a bell must still request attention then.
        TickBell(now);
        // A driver reset lost the context for good: input, shells and control commands keep being served above, but
        // nothing can be drawn until Dotty restarts.
        if (RenderingStopped)
        {
            FailScreenshotRequests("rendering stopped after a GPU reset");
            WaitWithoutRendering();
            return;
        }
        int framebufferWidth = _window.FramebufferSize.X;
        int framebufferHeight = _window.FramebufferSize.Y;
        FrameGate gate = FramePlanner.Gate(
            synchronizedUpdateHeld: !WindowPresentationGate.ShouldPresent(activeTab?.Session.Adapter),
            framebufferWidth,
            framebufferHeight,
            iconified: Volatile.Read(ref _windowIconified));
        // While a synchronized update (CSI 2026, used by nvim for every
        // redraw) is held, presents are withheld by design. Sleep instead of
        // spinning: the gate below used to burn 100% of a core here, starving
        // the PTY consumer and the producer itself and stretching the hold.
        if (gate == FrameGate.SyncHold)
        {
            if (!_syncHeld)
            {
                _syncHeld = true;
                _syncHoldStartTimestampMs = now;
            }
            WaitForWork(
                includeBlink: false,
                syncHoldRemainingMs: Math.Max(1L, (long)Math.Ceiling(activeTab?.Session.Adapter.SynchronizedUpdateRemainingMs ?? 0d)));
            return;
        }
        if (_syncHeld)
        {
            _syncHeld = false;
            long holdDurationMs = now - _syncHoldStartTimestampMs;
            _syncHoldCount++;
            if (holdDurationMs > _syncHoldMaxDurationMs)
                _syncHoldMaxDurationMs = holdDurationMs;
            if (holdDurationMs >= 250)
                _syncHoldOver250MsCount++;
        }

        // Iconified or zero-sized: nothing to draw into. Pending reasons stay queued for the restore frame.
        if (gate == FrameGate.NoSurface)
        {
            WaitForWork(includeBlink: false);
            return;
        }
        // An animated image's next frame is due. Checked after the synchronized-update gate (a held present would
        // otherwise spin on the wake this invalidation posts) and in the same clock as IKittyGraphicsHost.NowTicks.
        // The deadline is re-read from every composed frame, so a stale one costs at most one extra frame.
        if (_nextImageDeadlineTicks != 0 && TimeProvider.System.GetTimestamp() >= _nextImageDeadlineTicks)
            WindowPresentationGate.Invalidate(WindowFrameReason.ImageAnimation);
        UpdateCursorBlink(now, activeTab != null);
        // Without an update_status hook the status text (shell command state) only changes with PTY output or
        // other invalidations, so it is refreshed with them instead of polled.
        if (!LuaStatusPollActive() && WindowPresentationGate.PendingReasons != WindowFrameReason.None)
            RefreshLuaStatus();

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
        bool stateDirty = generationDirty ||
            framebufferWidth != _committedFramebufferWidth ||
            framebufferHeight != _committedFramebufferHeight ||
            _atlas.ContentVersion != _committedAtlasVersion;
        RefreshMonitorRefreshRateIfStale();
        FramePlan plan = FramePlanner.Decide(new FramePlanInputs(
            NowMs: now,
            PendingReasons: pendingReasons,
            StateDirty: stateDirty,
            LastPresentMs: _lastPresentTimestampMs,
            LastInteractionMs: _lastInteractionTimestampMs,
            ContentFrameIntervalMs: ContentFrameIntervalMs(AnyVisibleLeafBacklogged())));
        if (plan.Action == FrameAction.Idle)
        {
            // No SwapBuffers here: swapping without a fresh render presents the
            // stale back buffer (one full frame behind, or uninitialized garbage
            // at startup) — a periodic fullscreen flash to old content every
            // time the keepalive would have fired. Holding the front buffer is
            // always correct when nothing changed.
            CompleteIdleWaitersIfIdle(framebufferWidth, framebufferHeight);
            WaitForWork(includeBlink: true, includeImageAnimation: true);
            return;
        }
        // Preserve bulk-output coalescing, but let content catch up immediately
        // for a short window after interactive input.
        if (plan.Action == FrameAction.Hold)
        {
            WaitForWork(includeBlink: true, coalesceRemainingMs: plan.HoldMs, includeImageAnimation: true);
            return;
        }
        // The driver reset the GPU: the context is lost and rendering stops (see StopRenderingAfterContextLoss).
        if (HandleContextResetIfReported())
            return;
        WindowFrameReason consumedReasons = WindowPresentationGate.Consume();
        try
        {
            if (activeTab == null)
            {
                _nextImageDeadlineTicks = 0;
                long emptyRenderStart = FrameTimestamp();
                _renderer.Render(
                    ReadOnlySpan<CellInstance>.Empty,
                    ReadOnlySpan<ChromeQuadInstance>.Empty,
                    _atlas.Width,
                    _atlas.Height,
                    framebufferWidth,
                    framebufferHeight,
                    _cellWidthPx,
                    _cellHeightPx,
                    0.85f,
                    0.7f,
                    0.04f,
                    _themeBackground,
                    false);
                _frameTimings.RecordRender(emptyRenderStart, FrameTimestamp());
                CheckGlErrorsIfDebugging("render");
                CaptureScreenshotsBeforeSwap(framebufferWidth, framebufferHeight);
                SwapBuffersTimed();
                _lastPresentTimestampMs = GetClockMilliseconds();
                CommitFrameStamps(visibleLeaves, framebufferWidth, framebufferHeight);
                OnFramePresented();
                return;
            }

            var theme = _activeTheme;
            var padding = UserConfigService.Current.Window.Padding;
            var devicePadding = CurrentDevicePadding();
            float padLeft = devicePadding.Left;
            float padTop = devicePadding.Top;
            int barRows = CurrentBarRows();
            float statusWidth = TabBarQuadBuilder.MeasureStatusWidth(
                GetLuaStatusText(),
                _cellWidthPx,
                _typeface,
                _cellFontSizePx(),
                _atlas,
                _luaStatusWarning);
            PublishCustomFrameHitTestGeometry(
                framebufferWidth,
                barRows * _cellHeightPx,
                statusWidth);

            long composeStart = FrameTimestamp();
            var frame = _sceneComposer.Compose(
                activeTab,
                tabManager,
                theme,
                _themeForeground,
                _themeSelectionColor,
                framebufferWidth,
                framebufferHeight,
                _cellWidthPx,
                _cellHeightPx,
                _display.ContentScale,
                _rows,
                _cols,
                padding,
                IsTabBarVisible,
                _cursorBlinkVisible,
                _mouseController?.IsScrollbarHovered ?? false,
                _mouseController?.IsDraggingScrollbar ?? false,
                new SearchOverlayRenderState(
                    _keyboardDispatcher?.SearchActive ?? false,
                    _keyboardDispatcher?.SearchQuery ?? string.Empty,
                    _keyboardDispatcher?.ActiveMatchIndex ?? -1,
                    _keyboardDispatcher?.SearchMatches?.Count ?? 0,
                    _keyboardDispatcher?.SearchMatches,
                    _keyboardDispatcher is { SearchActive: true } dispatcher ? dispatcher.SearchQueryCursorColumn : -1,
                    _keyboardDispatcher?.SearchOverlayFlags ?? Dotty.Runtime.Search.SearchOverlayFlags.None),
                _activeContextMenu,
                _mouseController?.HoveredTabIndex ?? -1,
                _mouseController?.HoveredTabHitType ?? TabBarHitType.None,
                _tabTitleSource,
                GetLuaStatusText(),
                _luaStatusWarning,
                captionButtonsWidth: _customFrameActive
                    ? Math.Min(framebufferWidth, TabBarLayout.CaptionButtonWidth * TabBarLayout.CaptionButtonCount * _display.ContentScale)
                    : 0f,
                isMaximized: _window.WindowState == WindowState.Maximized,
                preeditState: _nativeTextInput?.Preedit,
                windowFocused: _lastWindowFocus != false);

            _nextImageDeadlineTicks = frame.NextImageDeadlineTicks;
            _frameTimings.RecordCompose(composeStart, FrameTimestamp());
            if (frame.IsIncomplete)
            {
                // A leaf lost the buffer-lock race mid-burst; its quads are missing.
                // Presenting would flash background where live content belongs, so
                // hold the previous front buffer and retry next frame. Requeue the
                // consumed reasons so the retry still has work to do. Yield: the
                // lock is held by the PTY consumer, so an immediate retry just
                // burns a core re-composing a frame that will likely lose again.
                frame.ReleaseImageLeases();
                WindowPresentationGate.Requeue(consumedReasons);
                WaitForWork(includeBlink: false, retryShortly: true);
                return;
            }
            if (frame.ImeCaretBoundsValid && _nativeTextInput != null)
            {
                // The composer reports device pixels; the IME wants window coordinates.
                var caret = frame.ImeCaretBounds;
                float toWindow = _display.WindowUnitsToDevice;
                int left = (int)MathF.Floor(caret.Left / toWindow);
                int top = (int)MathF.Floor(caret.Top / toWindow);
                _nativeTextInput.SetCursorRectangle(left, top,
                    Math.Max(1, (int)MathF.Ceiling(caret.Right / toWindow) - left),
                    Math.Max(1, (int)MathF.Ceiling(caret.Bottom / toWindow) - top));
            }
            var textConfig = UserConfigService.Current.Text;
            _renderer.TextBlend = CoverageCorrection.ParseMode(textConfig.Blending);
            _renderer.TextContrast = CoverageCorrection.NormalizeContrast(textConfig.Contrast);
            long renderStart = FrameTimestamp();
            _renderer.Render(
                frame.AsSpan(),
                frame.AsChromeSpan(),
                _atlas.Width,
                _atlas.Height,
                framebufferWidth,
                framebufferHeight,
                _cellWidthPx,
                _cellHeightPx,
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
                frame.MenuChromeStart,
                frame.ImageDraws,
                underlayChromeCount: frame.UnderlayChromeCount);
            _frameTimings.RecordRender(renderStart, FrameTimestamp());
            CheckGlErrorsIfDebugging("render");
            frame.ReleaseImageLeases();
            if (_renderer.ImageUploadsDeferred)
                WindowPresentationGate.Requeue(WindowFrameReason.ImageUpload);
            DrawBellFlash(framebufferWidth, framebufferHeight);
            CaptureScreenshotsBeforeSwap(framebufferWidth, framebufferHeight);
            SwapBuffersTimed();
            _lastPresentTimestampMs = GetClockMilliseconds();
            _presentCount++;
            CommitFrameStamps(visibleLeaves, framebufferWidth, framebufferHeight);
            OnFramePresented();
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

        BlinkPolicy blink = CurrentBlinkPolicy(hasActiveTab);
        if (blink.IsBlinking)
        {
            bool on = blink.IsOn(now);
            // Always advance to the segment containing now, so the idle wait's next deadline is never in the past.
            _lastCursorBlinkTimestampMs = blink.NextToggleMs(now) - blink.IntervalMs;
            if (on != _cursorBlinkVisible)
            {
                _cursorBlinkVisible = on;
                WindowPresentationGate.Invalidate(WindowFrameReason.CursorBlink);
            }
        }
        else
        {
            // An idle timer restarts with a full on segment when blinking resumes.
            _lastCursorBlinkTimestampMs = now;
            if (!_cursorBlinkVisible)
            {
                _cursorBlinkVisible = true;
                WindowPresentationGate.Invalidate(WindowFrameReason.CursorBlink);
            }
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
                    if (ReferenceEquals(visibleLeaves[i].Session, _tabManager?.ActiveTab?.Session))
                        _presentedActiveGeneration = after;
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

    private static unsafe void InstallNativeKeyboardCallbacks()
    {
        nint handle = _window.Native?.Glfw ?? 0;
        if (handle == 0)
            throw new PlatformNotSupportedException("Native keyboard input requires the selected GLFW window handle.");

        var context = _glfwApi!.Context;
        var setInputMode = (delegate* unmanaged[Cdecl]<nint, int, int, void>)context.GetProcAddress("glfwSetInputMode");
        var setKeyCallback = (delegate* unmanaged[Cdecl]<nint, nint, nint>)context.GetProcAddress("glfwSetKeyCallback");
        // The char-mods callback also receives the characters of Alt chords (X11/Wayland Mod1, Windows WM_SYSCHAR),
        // which the plain char callback drops; Meta chords use them to keep Shift (Alt+Shift+, = ESC <).
        var setCharModsCallback = (delegate* unmanaged[Cdecl]<nint, nint, nint>)context.GetProcAddress("glfwSetCharModsCallback");
        // GLFW_LOCK_KEY_MODS includes the real Caps Lock/Num Lock state in each key event.
        setInputMode(handle, 0x00033004, 1);
        setKeyCallback(handle, (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, int, void>)&OnNativeKeyboardKey);
        setCharModsCallback(handle, (nint)(delegate* unmanaged[Cdecl]<nint, uint, int, void>)&OnNativeKeyboardScalar);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeKeyboardKey(nint window, int key, int scancode, int action, int modifiers)
    {
        if (_closed)
            return;

        // keyboard.leftAlt/rightAlt are read live; GLFW reports the effective modifier state even when modifiers
        // arrive without physical key events.
        var keyboardConfig = UserConfigService.Current.Keyboard;
        _keyboardController.Modifiers.Policy = KeyboardPolicy.ForCurrentPlatform(keyboardConfig.LeftAlt, keyboardConfig.RightAlt);
        _keyboardController.Modifiers.SetNativeGlfwModifiers(modifiers);
        if (action == 0)
        {
            WindowPresentationGate.Invalidate(WindowFrameReason.Input);
            _keyboardController.HandleKeyUp((InputKey)key, scancode);
        }
        else if (action is 1 or 2)
        {
            WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.TabOrPane);
            RecordInteraction();
            if (action == 1)
                _keyboardController.HandleKeyDown((InputKey)key, scancode);
            else
                _keyboardController.HandleKeyRepeat((InputKey)key, scancode);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeKeyboardScalar(nint window, uint codepoint, int modifiers)
    {
        if (_closed)
            return;

        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
        _keyboardController.Modifiers.SetNativeGlfwModifiers(modifiers);
        _keyboardController.HandleUnicodeScalar(codepoint);
    }

    private static void OnNativePreeditChanged(ImePreeditState state)
    {
        if (_closed)
            return;
        if (state.IsActive)
            _tabManager?.ActiveTab?.ActivePane.ScrollToBottom();
        RecordInteraction();
        _cursorBlinkVisible = true;
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay);
    }

    private static void OnKeyboardActivity()
    {
        RecordInteraction();
        _cursorBlinkVisible = true;
        _lastCursorBlinkTimestampMs = GetClockMilliseconds();
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.CursorBlink);
    }

    private static void OnWindowFocusChanged(bool focused)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input);
        if (focused)
        {
            _cursorBlinkVisible = true;
            _lastCursorBlinkTimestampMs = GetClockMilliseconds();
        }
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
        _nativeTextInput?.SetFocus(focused && _activeContextMenu?.IsVisible != true);
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
    private static void RecordInteraction()
    {
        _lastInteractionTimestampMs = GetClockMilliseconds();
        _frameTimings.MarkInput(FrameTimestamp());
    }

    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (_closed)
            return;

        RecordInteraction();
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

        RecordInteraction();
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseUp(mouse, button);
    }

    private static void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_closed)
            return;

        RecordInteraction();
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseScroll(mouse, wheel);
    }

    private static void CopySelectionToClipboard()
    {
        var activePane = _tabManager?.ActiveTab?.ActivePane;
        if (activePane == null || !activePane.Selection.HasSelection) return;

        var text = activePane.Selection.GetSelectedText();
        if (!string.IsNullOrEmpty(text) && _keyboard != null)
        {
            _clipboard?.SetText(text);
        }
        if (UserConfigService.Current.Selection.ClearOnCopy)
            activePane.Selection.ClearSelection();
    }

    private static void CopyFinishedSelection(LeafPane pane)
    {
        var mode = MouseOptions.ParseCopyOnSelect(UserConfigService.Current.Mouse.CopyOnSelect);
        if (mode == CopyOnSelectMode.None || !pane.Selection.HasSelection)
            return;

        CopyOnSelect.Apply(mode, pane.Selection.GetSelectedText(), _primary, _clipboard);
    }

    private static void PastePrimaryToPane(LeafPane targetPane)
    {
        if (_keyboard == null) return;

        _primaryPasteRouter?.Paste(targetPane);
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

        _pasteRouter?.Paste(targetPane);
    }

    private static bool IsPaneAlive(LeafPane pane)
    {
        if (_closed || _tabManager == null)
            return false;
        foreach (var tab in _tabManager.Tabs)
        {
            foreach (var leaf in tab.PaneTree.Leaves)
            {
                if (ReferenceEquals(leaf, pane))
                    return true;
            }
        }

        return false;
    }

    private static void OnClosing()
    {
        if (_closed) return;
        _nativeTextInput?.Dispose();
        _nativeTextInput = null;
        _closed = true;
        _systemThemeRefreshCancellation?.Cancel();
        try { _systemThemeRefreshTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _systemThemeRefreshCancellation?.Dispose();
        _systemThemeRefreshCancellation = null;
        _systemThemeRefreshTask = null;
        Volatile.Write(ref _themeApplyPending, 0);
        Win32WindowFrame.Uninstall();
        _customFrameActive = false;
        _customFrameHitTestGeometryValid = false;
        _lifecycle.Close();
        _controlServer?.Dispose();
        _controlServer = null;
        while (_pendingControlCommands.TryDequeue(out var request))
            request.Completion.TrySetResult("ERROR host is closed");
        FailParkedControlRequests();
        UnsubscribeFramePacing();
        _window.FocusChanged -= OnWindowFocusChanged;
        VtHostBridge.Detach();
        UserConfigService.ConfigChanged -= OnConfigChanged;
        if (_tabManager != null)
        {
            _tabManager.ProcessExited -= OnTabProcessExited;
            _tabManager.ActiveTabChanged -= OnActiveTabChanged;
            _tabManager.TabAdded -= OnTabAdded;
            _tabManager.TabClosed -= OnTabClosed;
            _tabManager.TabTitleChanged -= OnTabTitleChanged;
            DetachBellNotifications(_tabManager);
            var tabs = _tabManager.Tabs;
            for (int i = 0; i < tabs.Count; i++)
                UnsubscribePaneTree(tabs[i]);
        }
        foreach (var pair in _clipboardSubscriptions)
            pair.Key.ClipboardWriteRequested -= pair.Value;
        _clipboardSubscriptions.Clear();
        foreach (var pair in _clipboardReadSubscriptions)
            pair.Key.ClipboardReadRequested -= pair.Value;
        _clipboardReadSubscriptions.Clear();
        _clipboardReads.Clear();
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
            _pendingClipboardBytes = 0;
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
        _pasteRouter = null;
        _sceneComposer?.Dispose();
        DisposeBellOverlay();
        _renderer?.Dispose();
        if (_atlas != null)
        {
            GlyphAtlasService.ReleaseAtlas(_atlas);
            _atlas = null!;
        }
    }

    private static readonly Dictionary<TerminalSession, Action<Dotty.Abstractions.Clipboard.ClipboardReadRequest>> _clipboardReadSubscriptions = new();
    private static readonly ClipboardReadService _clipboardReads = new(
        new ClipboardReadEnvironment(),
        static () => UserConfigService.Current.Clipboard,
        GetClockMilliseconds);

    /// <summary>Connects <see cref="ClipboardReadService"/> to the window state owned by this host.</summary>
    private sealed class ClipboardReadEnvironment : IClipboardReadEnvironment
    {
        public bool IsAvailable => !_closed && _keyboard is not null;
        public bool IsWindowFocused => _lastWindowFocus != false;
        public bool IsMenuVisible => _activeContextMenu?.IsVisible == true;

        public bool TryResolveOwner(TerminalSession session, out TerminalTab tab, out LeafPane pane) =>
            TryFindOwningPane(session, out tab, out pane);

        public bool IsActiveTab(TerminalTab tab) => ReferenceEquals(tab, _tabManager?.ActiveTab);

        public bool AllowRead(LeafPane pane, TerminalTab tab, Dotty.Abstractions.Clipboard.ClipboardSelection selection) =>
            _luaHost.Hooks.AllowClipboardRead(
                pane,
                tab,
                selection == Dotty.Abstractions.Clipboard.ClipboardSelection.Primary ? "primary" : "clipboard");

        public bool SupportsPrimarySelection => _primary is { IsSystemSelection: true };

        public string? ReadPrimary() => _primary is { IsSystemSelection: true } primary ? primary.Get() : null;

        public string? ReadClipboard() => _clipboard?.GetText();

        public void WriteReply(TerminalSession session, byte[] reply) => session.WriteInput(reply);

        public void ShowPrompt(ContextMenuModel prompt)
        {
            var size = _window.FramebufferSize;
            ClipboardReadPromptBuilder.Center(prompt, size.X, size.Y, _cellWidthPx, _cellHeightPx);
            _mouseHost.ActiveContextMenu = prompt;
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
        }

        public void Wake() => HostWake.Request();
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
            set
            {
                if (ReferenceEquals(_activeContextMenu, value))
                    return;
                var previousMenu = _activeContextMenu;
                _nativeTextInput?.Cancel();
                _activeContextMenu = value;
                _nativeTextInput?.SetFocus((_lastWindowFocus ?? _initialWindowInputFocus) && value?.IsVisible != true);
                // Close last: a Dismissed callback may install another menu (e.g. a follow-up prompt) and must not be overwritten.
                if (previousMenu is { IsVisible: true })
                    previousMenu.Close();
            }
        }

        public TerminalMouseGeometry Geometry
        {
            get
            {
                var size = _window.FramebufferSize;
                var padding = CurrentDevicePadding();
                int barRows = CurrentBarRows();
                float captionButtonsWidth = _customFrameActive
                    ? Math.Min(size.X, TabBarLayout.CaptionButtonWidth * TabBarLayout.CaptionButtonCount * _display.ContentScale)
                    : 0f;
                float statusWidth = TabBarQuadBuilder.MeasureStatusWidth(
                    GetLuaStatusText(),
                    _cellWidthPx,
                    _typeface,
                    _cellFontSizePx(),
                    _atlas,
                    _luaStatusWarning);
                return new TerminalMouseGeometry(
                    ContentScale: _display.ContentScale,
                    WindowUnitsToDevice: _display.WindowUnitsToDevice,
                    CellWidth: _cellWidthPx,
                    CellHeight: _cellHeightPx,
                    PaddingLeft: padding.Left,
                    PaddingTop: padding.Top,
                    TopOffset: barRows * _cellHeightPx,
                    FramebufferWidth: size.X,
                    FramebufferHeight: size.Y,
                    Columns: _cols,
                    Rows: _rows,
                    ShowTabBar: IsTabBarVisible,
                    StatusReservedWidth: statusWidth,
                    CaptionButtonsWidth: captionButtonsWidth);
            }
        }
        public bool Ctrl => _keyboardController?.Modifiers.Ctrl ?? false;
        public bool Shift => _keyboardController?.Modifiers.Shift ?? false;
        public bool Alt => _keyboardController?.Modifiers.Alt ?? false;
        public bool AltGr => _keyboardController?.Modifiers.AltGr ?? false;
        public bool Super => _keyboardController?.Modifiers.Super ?? false;
        public TerminalKeyModifiers LockModifiers => _keyboardController?.Modifiers.LockModifiers ?? TerminalKeyModifiers.None;
        public bool MetaUsesCommittedText => _keyboardController?.Modifiers.MetaUsesCommittedText ?? false;
        public bool KeypadNavigation => _keyboardController?.Modifiers.KeypadNavigation ?? false;

        public void CopySelection() => CopySelectionToClipboard();
        public void PasteClipboard() => PasteClipboardToSession();
        public void PasteClipboard(LeafPane targetPane) => PasteClipboardToPane(targetPane);
        public MiddleClickAction MiddleClick => MouseOptions.ParseMiddleClick(UserConfigService.Current.Mouse.MiddleClickAction);
        public MouseInputSettings MouseInput => MouseOptions.ResolveInput(UserConfigService.Current.Mouse);
        public void PastePrimary(LeafPane targetPane) => PastePrimaryToPane(targetPane);
        public void OnLocalSelectionFinished(LeafPane pane) => CopyFinishedSelection(pane);
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

        public void ReloadConfig() => UserConfigService.RequestReload();

        public string? ReadClipboardText() => _clipboard?.GetText();

        public string? GetShortcutLabel(TerminalAction action) => _keybindings.GetShortcutLabel(action);

        public SearchUserConfig SearchDefaults => UserConfigService.Current.Search;

        public bool TryClickSearchOverlay(float deviceX, float deviceY)
        {
            var geometry = Geometry;
            return _keyboardDispatcher?.ClickSearchOverlay(deviceX, deviceY, geometry.FramebufferWidth, geometry.FramebufferHeight,
                geometry.CellWidth) ?? false; // TerminalMouseGeometry cell sizes are device pixels (W4-C1)
        }

        public void ToggleTabBar()
        {
            _showTabBar = !_showTabBar;
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
                workingDirectory: activeTab.Session.InheritableWorkingDirectory,
                shell: activeTab.Session.LaunchShell,
                shellIsExecutable: activeTab.Session.LaunchShellIsExecutable);
            SilkConfig.ApplyConfigToAdapter(newTab.Session.Adapter);
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
                workingDirectory: activeTab.Session.InheritableWorkingDirectory,
                shell: activeTab.Session.LaunchShell,
                shellIsExecutable: activeTab.Session.LaunchShellIsExecutable);
            SilkConfig.ApplyConfigToAdapter(newTab.Session.Adapter);
        }

        public void ClearTerminal(TerminalTab activeTab)
        {
            Span<byte> clear = stackalloc byte[1];
            clear[0] = 0x0c;
            activeTab.Session.WriteInput(clear);
        }

        public void WriteInput(TerminalTab activeTab, ReadOnlySpan<byte> bytes) =>
            activeTab.Session.WriteInput(bytes);

        private HyperlinkActivator? _hyperlinks;

        private HyperlinkActivator Hyperlinks => _hyperlinks ??= new HyperlinkActivator(
            () => HyperlinkOptions.From(UserConfigService.Current.Hyperlinks),
            (url, source) => _luaHost.Hooks.TryOpenUrl(
                url, source == HyperlinkSource.Explicit ? "explicit" : "detected"),
            new SystemHyperlinkOpener());

        public HyperlinkScanOptions HyperlinkScan => new(UserConfigService.Current.Hyperlinks.Osc8);

        public void OpenHyperlink(string url, HyperlinkSource source, string? displayText)
        {
            HyperlinkActivation activation = Hyperlinks.Activate(url, source, displayText);
            switch (activation.Kind)
            {
                case HyperlinkActivationKind.Open:
                    OpenApprovedHyperlink(activation.Target!);
                    break;
                case HyperlinkActivationKind.NeedsConfirmation:
                    ConfirmHyperlink(activation, displayText);
                    break;
                case HyperlinkActivationKind.Blocked:
                    DebugLog.Log($"[hyperlink] blocked: {activation.Message}");
                    ShowPrompt(HyperlinkPromptBuilder.BuildNotice("Link blocked", activation.Message ?? "The link is not allowed."));
                    break;
            }
        }

        /// <summary>
        /// Asks the user to confirm a risky link. The prompt holds the already-evaluated canonical target, so
        /// what the user confirmed is exactly what opens. Cancel is focused; Escape and click-outside cancel.
        /// </summary>
        private void ConfirmHyperlink(HyperlinkActivation activation, string? displayText)
        {
            HyperlinkTarget target = activation.Target!;
            ShowPrompt(HyperlinkPromptBuilder.BuildConfirm(
                target,
                activation.Risks,
                displayText,
                open: () => OpenApprovedHyperlink(target),
                copyAddress: () => _clipboard?.SetText(target.Canonical)));
        }

        private void OpenApprovedHyperlink(HyperlinkTarget target) => _ = OpenApprovedHyperlinkAsync(target);

        private async Task OpenApprovedHyperlinkAsync(HyperlinkTarget target)
        {
            HyperlinkOpenResult result = await Hyperlinks.OpenApprovedAsync(target).ConfigureAwait(false);
            if (result.Success)
                return;

            DebugLog.Log($"[hyperlink] open failed: {result.Error}");
            string message = result.Error ?? "The link could not be opened.";
            // The opener finishes on a thread-pool thread; menus belong to the UI thread.
            _lifecycle.TryEnqueue(() => ShowPrompt(HyperlinkPromptBuilder.BuildNotice("Could not open link", message)));
        }

        /// <summary>Shows a host prompt at the pointer (the layout clamps it into the viewport).</summary>
        internal void ShowPrompt(ContextMenuModel prompt)
        {
            if (_closed)
                return;

            var pointer = _mouse?.Position ?? default;
            prompt.X = _display.WindowToDevice(pointer.X);
            prompt.Y = _display.WindowToDevice(pointer.Y);
            ActiveContextMenu = prompt;
            WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
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
