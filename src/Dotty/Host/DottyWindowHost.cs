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
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Selection;
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
    private static int _cols = 80, _rows = 24;
    private static SilkTerminalRenderer _renderer = null!;
    private static SgrColorArgb _themeForeground;
    private static SgrColorArgb _themeBackground;
    private static TerminalTabManager _tabManager = null!;
    private static readonly TextSelectionService _selectionService = new();
    private static readonly LuaScriptHost _luaHost = new();
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
    private static bool _cursorBlinkVisible = true;
    private static long _lastCursorBlinkTimestampMs;
    private static readonly ConcurrentQueue<string> _pendingTitles = new();
    private static readonly ConcurrentQueue<string> _pendingClipboards = new();
    private static readonly ConcurrentQueue<ControlRequest> _pendingControlCommands = new();
    private static WindowLifecycleCoordinator _lifecycle = new();
    private static DesktopControlServer? _controlServer;
    private static readonly Dictionary<TerminalSession, Action> _renderSubscriptions = new();
    private static LeafPane[] _visibleLeaves = Array.Empty<LeafPane>();
    private static readonly Dictionary<LeafPane, ulong> _committedGenerations = new();
    private static readonly Dictionary<LeafPane, ulong> _frameGenerations = new();
    private static int _committedFramebufferWidth = -1;
    private static int _committedFramebufferHeight = -1;
    private static int _committedAtlasVersion = int.MinValue;
    private static long _lastKeepalivePresentTimestampMs;

    private static bool _showTabBar = true;
    private static ContextMenuModel? _activeContextMenu;
    private sealed record ControlRequest(string Command, TaskCompletionSource<string> Completion);

    public static void Run()
    {
        _closed = false;
        _lifecycle = new WindowLifecycleCoordinator();
        global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        global::Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1000, 650),
            Title = "Dotty (Silk)",
            VSync = true,
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
        WindowPresentationGate.Invalidate(WindowFrameReason.Initial);
        _lastCursorBlinkTimestampMs = GetClockMilliseconds();
        UserConfigService.CallbackDispatcher = action => _lifecycle.TryEnqueue(action);
        UserConfigService.ConfigChanged += OnConfigChanged;
        UserConfigService.Load();

        _gl = _window.CreateOpenGL();
        string openGlVersion = _gl.GetStringS(StringName.Version);
        if (!GraphicsCapabilities.IsOpenGlVersionSupported(openGlVersion))
            throw new PlatformNotSupportedException(GraphicsCapabilities.DescribeUnsupportedVersion(openGlVersion));

        ResolveFontAndMetrics();
        _atlas = GlyphAtlasService.GetOrCreateAtlas(_typeface, _cellFontSizePx());
        GlyphAtlasService.AcquireAtlas(_atlas);
        _renderer = new SilkTerminalRenderer(_gl, _atlas);
        _sceneComposer = new TerminalSceneComposer(_atlas, _typeface, _cellFontSizePx(), _selectionService);
        (_themeForeground, _themeBackground) = SilkConfig.InitializeTheme();

        _tabManager = new TerminalTabManager();
        _tabManager.ActiveTabChanged += OnActiveTabChanged;
        _tabManager.TabAdded += OnTabAdded;
        _tabManager.TabClosed += OnTabClosed;
        _tabManager.TabTitleChanged += (tab, title) =>
        {
            if (tab == _tabManager.ActiveTab)
            {
                WindowPresentationGate.Invalidate(WindowFrameReason.Overlay);
                _pendingTitles.Enqueue(title);
            }
        };

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
        _luaHost.Initialize(UserConfigService.Current, _tabManager);
        string luaConfigPath = LuaScriptHost.GetConfigLuaPath();
        if (File.Exists(luaConfigPath))
        {
            _luaHost.LoadScript(luaConfigPath);
        }
        _luaHost.ConfigReloaded += () => OnConfigChanged(UserConfigService.Current);
        _keybindings.RegisterDefaults();
        _keybindings.ApplyCustomBindings(UserConfigService.Current.Keybindings);
        int barRows = _showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;
        float topOffset = barRows * _cellHeight * _scale;
        _window.Size = new Vector2D<int>((int)(_cols * _cellWidth), (int)(_rows * _cellHeight + topOffset / _scale));
        StartControlServer();
    }

    private static void OnActiveTabChanged(TerminalTab? tab)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        RefreshVisibleSessionSubscriptions();
        if (tab == null)
        {
            if (!_closed) _window.Close();
            return;
        }

        _pendingTitles.Enqueue(tab.Title);
        tab.Session.ClipboardWriteRequested += text => _pendingClipboards.Enqueue(text);
    }

    private static void OnTabAdded(TerminalTab tab)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        RefreshVisibleSessionSubscriptions();
    }

    private static void OnTabClosed(TerminalTab tab)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.TabOrPane);
        RefreshVisibleSessionSubscriptions();
    }

    private static void RefreshVisibleSessionSubscriptions()
    {
        var leaves = _tabManager?.ActiveTab?.PaneTree.Leaves;
        if (leaves == null)
        {
            _visibleLeaves = Array.Empty<LeafPane>();
            foreach (var pair in _renderSubscriptions)
                pair.Key.RenderScheduled -= pair.Value;
            _renderSubscriptions.Clear();
            _committedGenerations.Clear();
            return;
        }

        var visibleSessions = new HashSet<TerminalSession>();
        var visibleLeafSet = new HashSet<LeafPane>();
        var nextLeaves = new LeafPane[leaves.Count];
        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            nextLeaves[i] = leaf;
            visibleLeafSet.Add(leaf);
            if (visibleSessions.Add(leaf.Session) && !_renderSubscriptions.ContainsKey(leaf.Session))
            {
                Action callback = () => WindowPresentationGate.Invalidate(WindowFrameReason.Content);
                leaf.Session.RenderScheduled += callback;
                _renderSubscriptions.Add(leaf.Session, callback);
            }
        }

        var staleSessions = new List<TerminalSession>();
        foreach (var pair in _renderSubscriptions)
        {
            if (!visibleSessions.Contains(pair.Key))
                staleSessions.Add(pair.Key);
        }
        foreach (var session in staleSessions)
        {
            session.RenderScheduled -= _renderSubscriptions[session];
            _renderSubscriptions.Remove(session);
        }

        var staleLeaves = new List<LeafPane>();
        foreach (var pair in _committedGenerations)
        {
            if (!visibleLeafSet.Contains(pair.Key))
                staleLeaves.Add(pair.Key);
        }
        foreach (var leaf in staleLeaves)
            _committedGenerations.Remove(leaf);
        _visibleLeaves = nextLeaves;
    }

    private static float _cellFontSizePx()
    {
        float size = (float)UserConfigService.Current.Font.Size;
        size = float.IsFinite(size) ? Math.Clamp(size, 1f, 512f) : 14f;
        float scale = float.IsFinite(_scale) ? Math.Clamp(_scale, 0.1f, 16f) : 1f;
        return size * scale;
    }

    private static void OnConfigChanged(DottyUserConfig config)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.ThemeConfig);
        _keybindings.RegisterDefaults();
        _keybindings.ApplyCustomBindings(config.Keybindings);
        ResolveFontAndMetrics();
        RefreshFontResources();
        (_themeForeground, _themeBackground) = SilkConfig.InitializeTheme();
    }
    private static void ResolveFontAndMetrics()
    {
        var config = UserConfigService.Current;
        float rawScale = _window.FramebufferSize.X / (float)MathF.Max(1, _window.Size.X);
        _scale = float.IsFinite(rawScale) ? Math.Clamp(rawScale, 0.1f, 16f) : 1f;
        _typeface = FontMetricsService.ResolveTypeface(config.Font.Family);
        (_cellWidth, _cellHeight) = FontMetricsService.MeasureCell(
            _typeface,
            (float)config.Font.Size,
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
        WindowPresentationGate.Invalidate(WindowFrameReason.Resize);
        if (size.X <= 0 || size.Y <= 0) return;

        float previousScale = _scale;
        ResolveFontAndMetrics();
        if (MathF.Abs(previousScale - _scale) > 0.01f)
            RefreshFontResources();

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
        if (string.Equals(command, "STATS", StringComparison.OrdinalIgnoreCase))
        {
            int tabCount = _tabManager?.Count ?? 0;
            int activeIndex = _tabManager?.ActiveIndex ?? -1;
            return $"{{\"tabs\":{tabCount},\"activeTab\":{activeIndex}}}";
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
        byte[]? bytes = normalized switch
        {
            "ctrlc" or "control-c" => [0x03],
            "enter" or "return" => [0x0d],
            "tab" => [0x09],
            "escape" or "esc" => [0x1b],
            "backspace" => [0x7f],
            _ => null,
        };

        if (bytes == null)
        {
            if (!Enum.TryParse<InputKey>(keyName, ignoreCase: true, out var key))
                return "ERROR unknown key";
            bytes = SilkKeyMapper.Encode(
                key,
                ctrl: false,
                shift: false,
                alt: false,
                keypadAppMode: activeTab.Session.Adapter.KeypadApplicationMode,
                kittyMode: activeTab.Session.Adapter.KittyKeyboardMode,
                applicationCursorKeys: activeTab.Session.Adapter.ApplicationCursorKeysEnabled);
            if (bytes == null)
                return "ERROR unsupported key";
        }

        activeTab.Session.WriteInput(bytes);
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
        while (_pendingTitles.TryDequeue(out var title))
        {
            if (!_closed)
            {
                var tabCount = _tabManager?.Count ?? 1;
                var activeTab = _tabManager?.ActiveTab;
                string customTitle = activeTab != null ? _luaHost.Hooks.FormatTabTitle(activeTab, _tabManager!.ActiveIndex) ?? title : title;
                _window.Title = tabCount > 1 ? $"[{_tabManager!.ActiveIndex + 1}/{tabCount}] {customTitle}" : customTitle;
            }
        }

        while (_pendingClipboards.TryDequeue(out var text))
        {
            if (!_closed && _keyboard is not null)
            {
                _clipboard?.SetText(text);
            }
        }
    }
    private static void OnRender(double delta)
    {
        try
        {
            OnRenderCore(delta);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(GraphicsCapabilities.DescribeInitializationFailure(exception));
            _window.Close();
        }
    }

    private static void OnRenderCore(double delta)
    {
        _keyboardController?.Tick();
        DrainWindowEvents();

        var tabManager = _tabManager;
        var activeTab = tabManager.ActiveTab;
        if (!WindowPresentationGate.ShouldPresent(activeTab?.Session.Adapter))
            return;

        int framebufferWidth = _window.FramebufferSize.X;
        int framebufferHeight = _window.FramebufferSize.Y;
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
            return;

        long now = GetClockMilliseconds();
        UpdateCursorBlink(now, activeTab != null);

        WindowFrameReason pendingReasons = WindowPresentationGate.PendingReasons;
        if ((pendingReasons & WindowFrameReason.TabOrPane) != 0)
            RefreshVisibleSessionSubscriptions();

        _frameGenerations.Clear();
        bool generationDirty = false;
        if (activeTab != null)
        {
            foreach (var leaf in _visibleLeaves)
            {
                if (!TryReadGeneration(leaf.Session.Adapter.Buffer, out ulong generation))
                {
                    generationDirty = true;
                    continue;
                }

                _frameGenerations[leaf] = generation;
                if (!_committedGenerations.TryGetValue(leaf, out ulong committed) || committed != generation)
                    generationDirty = true;
            }
        }

        bool dirty = pendingReasons != WindowFrameReason.None ||
            generationDirty ||
            framebufferWidth != _committedFramebufferWidth ||
            framebufferHeight != _committedFramebufferHeight ||
            _atlas.ContentVersion != _committedAtlasVersion;
        if (!dirty)
        {
            if (now - _lastKeepalivePresentTimestampMs >= 1000)
            {
                _window.SwapBuffers();
                _lastKeepalivePresentTimestampMs = now;
            }
            return;
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
                CommitFrameStamps(framebufferWidth, framebufferHeight);
                return;
            }

            var theme = SilkConfig.LoadActiveTheme();
            var padding = UserConfigService.Current.Window.Padding;
            float padLeft = (float)padding.Left * _scale;
            float padTop = (float)padding.Top * _scale;
            int barRows = _showTabBar ? TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, _cellHeight) : 0;

            var frame = _sceneComposer.Compose(
                activeTab,
                tabManager,
                theme,
                _themeForeground,
                SilkConfig.ResolveSelectionColor(theme),
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
                    _keyboardDispatcher?.SearchMatches?.Count ?? 0),
                _activeContextMenu,
                _mouseController?.HoveredTabIndex ?? -1,
                _mouseController?.HoveredTabHitType ?? TabBarHitType.None);

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
                frame.MenuInstanceStart,
                frame.MenuChromeStart);
            _window.SwapBuffers();
            CommitFrameStamps(framebufferWidth, framebufferHeight);
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

    private static void CommitFrameStamps(int framebufferWidth, int framebufferHeight)
    {
        foreach (var leaf in _visibleLeaves)
        {
            if (_frameGenerations.TryGetValue(leaf, out ulong before) &&
                TryReadGeneration(leaf.Session.Adapter.Buffer, out ulong after) &&
                before == after)
            {
                _committedGenerations[leaf] = after;
            }
        }

        _committedFramebufferWidth = framebufferWidth;
        _committedFramebufferHeight = framebufferHeight;
        _committedAtlasVersion = _atlas.ContentVersion;
        _lastKeepalivePresentTimestampMs = GetClockMilliseconds();
    }

    private static void OnKeyboardDown(IKeyboard keyboard, InputKey key, int scancode)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.TabOrPane);
        _keyboardController.HandleKeyDown(key, scancode);
    }

    private static void OnKeyboardUp(IKeyboard keyboard, InputKey key, int scancode)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input);
        _keyboardController.HandleKeyUp(key, scancode);
    }

    private static void OnKeyboardChar(IKeyboard keyboard, char character)
    {
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
        WindowFocusRouter.Route(
            ref _lastWindowFocus,
            focused,
            _closed,
            state =>
            {
                var activeTab = _tabManager?.ActiveTab;
                if (activeTab != null)
                    activeTab.Session.SendFocusReport(state);
            });
    }

    private static long GetClockMilliseconds() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseDown(mouse, button);
    }

    private static void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseMove(mouse, position);
    }

    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseUp(mouse, button);
    }

    private static void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        WindowPresentationGate.Invalidate(WindowFrameReason.Input | WindowFrameReason.Overlay | WindowFrameReason.Selection);
        _mouseController.HandleMouseScroll(mouse, wheel);
    }


    private static void CopySelectionToClipboard()
    {
        var activeTab = _tabManager?.ActiveTab;
        if (activeTab == null || !_selectionService.HasSelection) return;

        var text = _selectionService.GetSelectedText(activeTab.Session.Adapter.Buffer);
        if (!string.IsNullOrEmpty(text) && _keyboard != null)
        {
            _clipboard?.SetText(text);
        }
        _selectionService.ClearSelection();
    }

    private static void PasteClipboardToSession()
    {
        var activeTab = _tabManager?.ActiveTab;
        if (activeTab == null || _keyboard == null) return;

        var text = _clipboard?.GetText();
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = ClipboardPasteRouter.Encode(text, activeTab.Session.Adapter);
            activeTab.Session.WriteInput(bytes);
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
        foreach (var pair in _renderSubscriptions)
            pair.Key.RenderScheduled -= pair.Value;
        _renderSubscriptions.Clear();
        _visibleLeaves = Array.Empty<LeafPane>();
        _committedGenerations.Clear();
        _frameGenerations.Clear();
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
        public TextSelectionService SelectionService => _selectionService;
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
                    ShowTabBar: _showTabBar);
            }
        }

        public bool Ctrl => _keyboardController?.Ctrl ?? false;
        public bool Shift => _keyboardController?.Shift ?? false;
        public bool Alt => _keyboardController?.Alt ?? false;
        public bool Super => _keyboardController?.Super ?? false;

        public void CopySelection() => CopySelectionToClipboard();
        public void PasteClipboard() => PasteClipboardToSession();

        public void CreateTab(TerminalTab activeTab)
        {
            var newTab = _tabManager.CreateTab(
                cols: _cols,
                rows: _rows,
                workingDirectory: activeTab.WorkingDirectory);
            SilkConfig.ApplyThemeToAdapter(newTab.Session.Adapter);
            newTab.Session.ClipboardWriteRequested += text => _pendingClipboards.Enqueue(text);
        }

        public void ClearTerminal(TerminalTab activeTab) =>
            activeTab.Session.WriteInput(new byte[] { 0x0c });

        public void WriteInput(TerminalTab activeTab, byte[] bytes) =>
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
