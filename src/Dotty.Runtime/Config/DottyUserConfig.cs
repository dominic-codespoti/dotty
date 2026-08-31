using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dotty.Abstractions.Config;

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

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "DarkPlus";

    [JsonPropertyName("selectionColor")]
    public string? SelectionColor { get; set; }
    [JsonPropertyName("panes")]
    public PanesUserConfig Panes { get; set; } = new();

    [JsonPropertyName("keybindings")]
    public System.Collections.Generic.Dictionary<string, string> Keybindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class FontUserConfig
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = "JetBrainsMono Nerd Font Mono, JetBrains Mono, Fira Code, Cascadia Code, monospace";

    [JsonPropertyName("size")]
    public double Size { get; set; } = 14.0;

    [JsonPropertyName("lineHeight")]
    public double LineHeight { get; set; } = 1.25;
}

public sealed class WindowUserConfig
{
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
                    CreateDefaultConfigFile(path);
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

    private static DottyUserConfig? ReadConfig(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize(
            stream,
            DottyUserConfigJsonContext.Default.DottyUserConfig);
    }

    private static void CreateDefaultConfigFile(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return;

        Directory.CreateDirectory(directory);
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var defaultConfig = new DottyUserConfig();
            string json = JsonSerializer.Serialize(
                defaultConfig,
                DottyUserConfigJsonContext.Default.DottyUserConfig);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
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
        DottyUserConfig? loaded;
        try
        {
            if (!File.Exists(path))
                return;
            loaded = ReadConfig(path);
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
