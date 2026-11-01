using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Graphics;

namespace Dotty.Runtime.Config;

/// <summary>
/// Root user-facing configuration model loaded from ~/.config/dotty/config.json.
/// </summary>
public sealed class DottyUserConfig
{
    [JsonPropertyName("font")]
    public FontUserConfig Font { get; set; } = new();

    [JsonPropertyName("window")]
    public WindowUserConfig Window { get; set; } = new();

    [JsonPropertyName("tabBar")]
    public TabBarUserConfig TabBar { get; set; } = new();

    [JsonPropertyName("cursor")]
    public CursorUserConfig Cursor { get; set; } = new();

    [JsonPropertyName("hyperlinks")]
    public HyperlinksUserConfig Hyperlinks { get; set; } = new();

    [JsonPropertyName("mouse")]
    public MouseUserConfig Mouse { get; set; } = new();

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "DarkPlus";

    [JsonPropertyName("selectionColor")]
    public string? SelectionColor { get; set; }

    /// <summary>
    /// Optional light theme name used when <see cref="ThemeAuto"/> is true and
    /// the OS reports a light color scheme. Defaults to LightPlus.
    /// </summary>
    [JsonPropertyName("themeLight")]
    public string? ThemeLight { get; set; }

    /// <summary>
    /// Optional dark theme name used when <see cref="ThemeAuto"/> is true and
    /// the OS reports a dark color scheme. Defaults to <see cref="Theme"/>.
    /// </summary>
    [JsonPropertyName("themeDark")]
    public string? ThemeDark { get; set; }

    /// <summary>
    /// When true, the host follows the OS light/dark color scheme, selecting
    /// <see cref="ThemeDark"/> (or <see cref="Theme"/>) vs <see cref="ThemeLight"/>
    /// (default LightPlus). Explicit theme names always win when false.
    /// </summary>
    [JsonPropertyName("themeAuto")]
    public bool ThemeAuto { get; set; }

    [JsonPropertyName("panes")]
    public PanesUserConfig Panes { get; set; } = new();

    [JsonPropertyName("bell")]
    public BellUserConfig Bell { get; set; } = new();

    [JsonPropertyName("keybindings")]
    public System.Collections.Generic.Dictionary<string, string> Keybindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alt/Option roles (decision K1); read live at each key event.</summary>
    [JsonPropertyName("keyboard")]
    public KeyboardUserConfig Keyboard { get; set; } = new();
    [JsonPropertyName("clipboard")]
    public ClipboardUserConfig Clipboard { get; set; } = new();

    [JsonPropertyName("vt")]
    public VtUserConfig Vt { get; set; } = new();

    /// <summary>History limits. Applies to sessions created after a change; existing tabs keep theirs.</summary>
    [JsonPropertyName("scrollback")]
    public ScrollbackUserConfig Scrollback { get; set; } = new();
    /// <summary>
    /// Shell integration auto-injection: <c>none</c> (default, opt-in) or <c>detect</c>. Any other value
    /// is treated as <c>none</c>. Applies to sessions created after the change.
    /// </summary>
    [JsonPropertyName("shellIntegration")]
    public string ShellIntegration { get; set; } = "none";

    /// <summary>
    /// TERM given to new sessions. <c>xterm-256color</c> (default) or a terminfo entry such as <c>xterm-dotty</c>;
    /// a name whose terminfo entry cannot be found falls back to <c>xterm-256color</c>. Applies to sessions created
    /// after the change.
    /// </summary>
    [JsonPropertyName("term")]
    public string Term { get; set; } = "xterm-256color";

    /// <summary>Kitty graphics protocol settings. Applies to sessions created after a change.</summary>
    [JsonPropertyName("graphics")]
    public GraphicsUserConfig Graphics { get; set; } = new();

    /// <summary>Normalized Kitty graphics options derived from <see cref="Graphics"/>.</summary>
    public KittyGraphicsOptions ToKittyGraphicsOptions() => (Graphics ?? new GraphicsUserConfig()).ToOptions();
}
public sealed class FontUserConfig
{
    /// <summary>Font families in fallback order (comma list); the first installed family is the primary font.</summary>
    [JsonPropertyName("family")]
    public string Family { get; set; } = DottyDefaults.FontFamily;

    /// <summary>Font size in points; device pixels = points × 96/72 × display content scale.</summary>
    [JsonPropertyName("size")]
    public double Size { get; set; } = DottyDefaults.FontSize;

    [JsonPropertyName("lineHeight")]
    public double LineHeight { get; set; } = 1.25;

    /// <summary>Cell width adjustment: signed logical pixels (<c>"1"</c>) or percent (<c>"10%"</c>); empty keeps the measured width.</summary>
    [JsonPropertyName("adjustCellWidth")]
    public string AdjustCellWidth { get; set; } = string.Empty;

    /// <summary>Cell height adjustment: signed logical pixels (<c>"2"</c>) or percent (<c>"-5%"</c>); extra height is split above and below the text.</summary>
    [JsonPropertyName("adjustCellHeight")]
    public string AdjustCellHeight { get; set; } = string.Empty;

    /// <summary>Per-range font overrides, checked before the generic fallback chain.</summary>
    [JsonPropertyName("symbolMap")]
    public System.Collections.Generic.List<string> SymbolMap { get; set; } = new();

    /// <summary>Faint (SGR 2) text keeps this share of its colour, mixed toward the cell background (0-1).</summary>
    [JsonPropertyName("faintOpacity")]
    public double FaintOpacity { get; set; } = 0.5;

    /// <summary>Families for bold text in the primary font (comma list); empty uses the primary family's bold face.</summary>
    [JsonPropertyName("boldFamily")]
    public string BoldFamily { get; set; } = string.Empty;

    /// <summary>Families for italic text in the primary font (comma list); empty uses the primary family's italic face.</summary>
    [JsonPropertyName("italicFamily")]
    public string ItalicFamily { get; set; } = string.Empty;

    /// <summary>Families for bold italic text in the primary font (comma list); empty uses the primary family's bold italic face.</summary>
    [JsonPropertyName("boldItalicFamily")]
    public string BoldItalicFamily { get; set; } = string.Empty;

    /// <summary>Weight of regular text: a CSS weight name or a number 1-1000.</summary>
    [JsonPropertyName("weight")]
    public string Weight { get; set; } = DottyDefaults.FontWeight;

    /// <summary>OpenType feature settings for shaping, such as <c>+calt</c>, <c>-liga</c>, <c>cv01=2</c>.</summary>
    [JsonPropertyName("features")]
    public System.Collections.Generic.List<string> Features { get; set; } = new();
}

public sealed class WindowUserConfig
{
    [JsonPropertyName("decorations")]
    public string Decorations { get; set; } = "custom";

    [JsonPropertyName("padding")]
    public PaddingUserConfig Padding { get; set; } = new();

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "Dotty";
}

public sealed class PaddingUserConfig
{
    [JsonPropertyName("left")]
    public double Left { get; set; } = 14.0;

    [JsonPropertyName("top")]
    public double Top { get; set; } = 8.0;

    [JsonPropertyName("right")]
    public double Right { get; set; } = 14.0;

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; } = 8.0;
}

public sealed class TabBarUserConfig
{
    [JsonPropertyName("show")]
    public bool Show { get; set; } = true;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 38.0;

    [JsonPropertyName("style")]
    public string Style { get; set; } = "Pill"; // "Pill", "Compact", "Minimal"
}

public sealed class CursorUserConfig
{
    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "Block"; // "Block", "Beam", "Underline"

    [JsonPropertyName("blink")]
    public bool Blink { get; set; } = true;

    [JsonPropertyName("blinkIntervalMs")]
    public int BlinkIntervalMs { get; set; } = 500;

    /// <summary>Cursor drawn while the window is unfocused: <c>hollow</c>, <c>same</c> or <c>hidden</c>.</summary>
    [JsonPropertyName("unfocusedStyle")]
    public string UnfocusedStyle { get; set; } = "hollow";

    /// <summary>Text colour inside a block cursor: <c>auto</c> (cell background) or <c>cell-foreground</c>.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "auto";
}
public sealed class ClipboardUserConfig
{
    [JsonPropertyName("allowOsc52Write")]
    public bool AllowOsc52Write { get; set; }

    /// <summary>Default for <see cref="Osc52ReadLimitBytes"/>: 1 MiB.</summary>
    public const int DefaultOsc52ReadLimitBytes = 1_048_576;

    /// <summary>Upper clamp for <see cref="Osc52ReadLimitBytes"/>: 8 MiB.</summary>
    public const int MaxOsc52ReadLimitBytes = 8 * 1024 * 1024;

    private int _osc52ReadLimitBytes = DefaultOsc52ReadLimitBytes;

    /// <summary>
    /// OSC 52 clipboard read (<c>?</c> query) policy: <c>deny</c> (default), <c>ask</c> or <c>allow</c>,
    /// case-insensitive. Any other value is treated as <c>deny</c> (fail closed); see <c>ClipboardReadModes.Parse</c>.
    /// </summary>
    [JsonPropertyName("osc52Read")]
    public string Osc52Read { get; set; } = "deny";

    /// <summary>
    /// Largest clipboard payload, in UTF-8 bytes, an OSC 52 read may return. Larger clipboards are dropped
    /// without a reply. Values are clamped to [1, <see cref="MaxOsc52ReadLimitBytes"/>].
    /// </summary>
    [JsonPropertyName("osc52ReadLimitBytes")]
    public int Osc52ReadLimitBytes
    {
        get => _osc52ReadLimitBytes;
        set => _osc52ReadLimitBytes = Math.Clamp(value, 1, MaxOsc52ReadLimitBytes);
    }

    [JsonPropertyName("confirmPaste")]
    public bool ConfirmPaste { get; set; } = true;

    [JsonPropertyName("bracketedPasteIsSafe")]
    public bool BracketedPasteIsSafe { get; set; } = true;
}
public sealed class ScrollbackUserConfig
{
    /// <summary>
    /// Maximum history lines per session: 0 = no history, capped at <see cref="ScrollbackPolicy.HardMaxLines"/>.
    /// Negative values fall back to the default.
    /// </summary>
    [JsonPropertyName("limitLines")]
    public int LimitLines { get; set; } = DottyDefaults.ScrollbackLines;

    /// <summary>
    /// Maximum raw ring bytes per session, counted as <c>(lines + rows) * columns * 12</c>; 0 = no byte cap.
    /// Non-zero values below 1 MiB and negative values fall back to the default. The first limit reached applies.
    /// </summary>
    [JsonPropertyName("limitBytes")]
    public long LimitBytes { get; set; } = DottyDefaults.ScrollbackLimitBytes;

    /// <summary>The normalized policy these settings describe.</summary>
    public ScrollbackPolicy ToPolicy() => new ScrollbackPolicy(LimitLines, LimitBytes).Normalize();
}
public sealed class GraphicsUserConfig
{
    /// <summary>Largest accepted <see cref="ImageStorageLimitMiB"/>.</summary>
    public const int MaxStorageLimitMiB = 4096;
    /// <summary>Largest accepted <see cref="MaxPayloadMiB"/>.</summary>
    public const int MaxPayloadMiBLimit = 1024;

    /// <summary>Enable the Kitty graphics protocol (APC G).</summary>
    [JsonPropertyName("kittyProtocol")]
    public bool KittyProtocol { get; set; } = DottyDefaults.KittyGraphicsEnabled;

    /// <summary>Per-session image memory budget in MiB; non-positive falls back to the default, capped at 4096.</summary>
    [JsonPropertyName("imageStorageLimitMiB")]
    public int ImageStorageLimitMiB { get; set; } = (int)(DottyDefaults.KittyImageStorageLimitBytes / (1024 * 1024));

    /// <summary>Largest single transmission in MiB; non-positive falls back to the default, capped at 1024.</summary>
    [JsonPropertyName("maxPayloadMiB")]
    public int MaxPayloadMiB { get; set; } = (int)(DottyDefaults.KittyMaxPayloadBytes / (1024 * 1024));

    /// <summary>Allow <c>t=f</c> regular-file transmission.</summary>
    [JsonPropertyName("allowFileTransfer")]
    public bool AllowFileTransfer { get; set; } = true;

    /// <summary>Allow <c>t=t</c> temporary-file transmission.</summary>
    [JsonPropertyName("allowTempFileTransfer")]
    public bool AllowTempFileTransfer { get; set; } = true;

    /// <summary>Allow <c>t=s</c> shared-memory transmission.</summary>
    [JsonPropertyName("allowSharedMemory")]
    public bool AllowSharedMemory { get; set; } = true;

    /// <summary>
    /// Windows only, experimental: create the ConPTY with <c>PSEUDOCONSOLE_PASSTHROUGH_MODE</c> so graphics APC is
    /// not dropped. May change how ConPTY forwards VT; verify on Windows before relying on it. Ignored elsewhere.
    /// </summary>
    [JsonPropertyName("windowsPassthrough")]
    public bool WindowsPassthrough { get; set; } = DottyDefaults.GraphicsWindowsPassthrough;

    /// <summary>The normalized options these settings describe (Direct transmission is always allowed).</summary>
    public KittyGraphicsOptions ToOptions()
    {
        KittyMediaFlags media = KittyMediaFlags.Direct;
        if (AllowFileTransfer) media |= KittyMediaFlags.File;
        if (AllowTempFileTransfer) media |= KittyMediaFlags.TempFile;
        if (AllowSharedMemory) media |= KittyMediaFlags.SharedMemory;
        return new KittyGraphicsOptions
        {
            Enabled = KittyProtocol,
            StorageLimitBytes = ToBytes(ImageStorageLimitMiB, MaxStorageLimitMiB, DottyDefaults.KittyImageStorageLimitBytes),
            MaxPayloadBytes = ToBytes(MaxPayloadMiB, MaxPayloadMiBLimit, DottyDefaults.KittyMaxPayloadBytes),
            Media = media,
        }.Normalize();
    }

    private static long ToBytes(int mib, int maxMiB, long defaultBytes) =>
        mib <= 0 ? defaultBytes : (long)Math.Min(mib, maxMiB) * 1024 * 1024;
}
public sealed class PanesUserConfig
{
    [JsonPropertyName("dividerThickness")]
    public double DividerThickness { get; set; } = 2.0;

    [JsonPropertyName("activeBorder")]
    public bool ActiveBorder { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DottyUserConfig))]
internal sealed partial class DottyUserConfigJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Service that loads, validates, defaults, and watches the platform-specific
/// Dotty configuration file with hot-reloading.
/// </summary>
public static class UserConfigService
{
    private static DottyUserConfig _current = new();
    private static FileSystemWatcher? _watcher;
    private static readonly object _lock = new();
    private static int _reloadVersion;
    private static string? _lastError;
    private static Action<Action>? _callbackDispatcher;

    public static event Action<DottyUserConfig>? ConfigChanged;

    public static DottyUserConfig Current
    {
        get { lock (_lock) return _current; }
        private set { lock (_lock) _current = value; }
    }

    public static string? LastError
    {
        get { lock (_lock) return _lastError; }
    }

    /// <summary>
    /// Optional host dispatcher. Watcher callbacks use it to reach the UI
    /// thread; null invokes callbacks on the watcher task.
    /// </summary>
    public static Action<Action>? CallbackDispatcher
    {
        get { lock (_lock) return _callbackDispatcher; }
        set { lock (_lock) _callbackDispatcher = value; }
    }

    public static string GetConfigPath() => PlatformPaths.ConfigFile;

    public static DottyUserConfig Load()
    {
        string path = GetConfigPath();
        lock (_lock)
        {
            _lastError = null;
            try
            {
                if (File.Exists(path))
                {
                    var loaded = ReadConfig(path);
                    if (loaded != null)
                    {
                        _current = loaded;
                        EnsureWatcher(Path.GetDirectoryName(path));
                        return loaded;
                    }
                }
                else
                {
                    // No file means "all defaults". Nothing is written, so later default changes reach
                    // every user; `dotty +init-config` creates a commented starter file on request. The
                    // directory is created so the watcher sees a config.json created later.
                    string? directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }

            _current = new DottyUserConfig();
            EnsureWatcher(Path.GetDirectoryName(path));
            return _current;
        }
    }

    /// <summary>
    /// Reads one config file without touching <see cref="Current"/> or the watcher. A file holding
    /// JSON <c>null</c> yields defaults; malformed JSON throws <see cref="JsonException"/>.
    /// </summary>
    public static DottyUserConfig ReadFile(string path) => ReadConfig(path) ?? new DottyUserConfig();

    /// <summary>Indented JSON in the same shape <c>config.json</c> is read in.</summary>
    public static string Serialize(DottyUserConfig config) =>
        JsonSerializer.Serialize(config, DottyUserConfigJsonContext.Default.DottyUserConfig);

    private static DottyUserConfig? ReadConfig(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        DottyUserConfig? config = JsonSerializer.Deserialize(
            stream,
            DottyUserConfigJsonContext.Default.DottyUserConfig);
        if (config is not null)
        {
            config.Clipboard ??= new ClipboardUserConfig();
            config.Hyperlinks ??= new HyperlinksUserConfig();
            config.Vt ??= new VtUserConfig();
            config.Mouse ??= new MouseUserConfig();
            config.Scrollback ??= new ScrollbackUserConfig();
            config.Graphics ??= new GraphicsUserConfig();
            config.Bell ??= new BellUserConfig();
            config.Keyboard ??= new KeyboardUserConfig();
        }
        return config;
    }

    private static void EnsureWatcher(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || _watcher != null || !Directory.Exists(directory))
            return;

        try
        {
            var watcher = new FileSystemWatcher(directory, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false,
            };
            watcher.Changed += OnConfigFileChanged;
            watcher.Created += OnConfigFileChanged;
            watcher.Deleted += OnConfigFileChanged;
            watcher.Renamed += OnConfigFileRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    private static void OnConfigFileChanged(object? sender, FileSystemEventArgs args) =>
        ScheduleReload();

    private static void OnConfigFileRenamed(object? sender, RenamedEventArgs args) =>
        ScheduleReload();

    private static void OnWatcherError(object? sender, ErrorEventArgs args)
    {
        lock (_lock)
        {
            _lastError = args.GetException().Message;
            DisposeWatcherLocked();
            EnsureWatcher(Path.GetDirectoryName(GetConfigPath()));
        }
    }

    public static void RequestReload() => ScheduleReload();

    private static void ScheduleReload()
    {
        int version = Interlocked.Increment(ref _reloadVersion);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (version != Volatile.Read(ref _reloadVersion))
                    return;
                Reload(version);
            }
            catch (Exception ex)
            {
                lock (_lock) _lastError = ex.Message;
            }
        });
    }

    private static void Reload(int version)
    {
        if (version != Volatile.Read(ref _reloadVersion))
            return;

        string path = GetConfigPath();
        DottyUserConfig loaded;
        try
        {
            loaded = File.Exists(path) ? ReadConfig(path) ?? new DottyUserConfig() : new DottyUserConfig();
        }
        catch (Exception ex)
        {
            lock (_lock) _lastError = ex.Message;
            return;
        }

        if (loaded == null || version != Volatile.Read(ref _reloadVersion))
            return;

        Current = loaded;
        var callback = ConfigChanged;
        if (callback == null || version != Volatile.Read(ref _reloadVersion))
            return;

        void Notify() => callback(loaded);
        var dispatcher = CallbackDispatcher;
        if (dispatcher != null)
            dispatcher(Notify);
        else if (version == Volatile.Read(ref _reloadVersion))
            Notify();
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _reloadVersion);
            DisposeWatcherLocked();
            _callbackDispatcher = null;
            ConfigChanged = null;
        }
    }

    private static void DisposeWatcherLocked()
    {
        if (_watcher == null)
            return;

        _watcher.Changed -= OnConfigFileChanged;
        _watcher.Created -= OnConfigFileChanged;
        _watcher.Deleted -= OnConfigFileChanged;
        _watcher.Renamed -= OnConfigFileRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }
}
