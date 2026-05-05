using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

public class RuntimeSettingsData
{
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? CursorShape { get; set; }
    public bool? CursorBlink { get; set; }
    public double? CursorBlinkIntervalMs { get; set; }
    public string? Background { get; set; }
    public string? Foreground { get; set; }
    public string? SelectionColor { get; set; }
    public string? Theme { get; set; }
    public double? CellPadding { get; set; }
    public double? ContentPaddingLeft { get; set; }
    public double? ContentPaddingTop { get; set; }
    public double? ContentPaddingRight { get; set; }
    public double? ContentPaddingBottom { get; set; }
}

public static class RuntimeSettings
{
    private static RuntimeSettingsData s_current = new();
    public static RuntimeSettingsData Current => s_current;

    public static event EventHandler? Changed;

    internal static void Apply(RuntimeSettingsData data)
    {
        s_current = data;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string GetFontFamily() => Current.FontFamily ?? global::Dotty.Generated.Config.FontFamily;
    public static double GetFontSize() => Current.FontSize ?? global::Dotty.Generated.Config.FontSize;
    public static double GetCellPadding() => Current.CellPadding ?? global::Dotty.Generated.Config.CellPadding;
    public static double GetCursorBlinkIntervalMs() => Current.CursorBlinkIntervalMs ?? global::Dotty.Generated.Config.CursorBlinkIntervalMs;
}

public sealed class FileSystemConfigWatcher : IDisposable
{
    private readonly string _configPath;
    private readonly string _configDir;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();
    private bool _disposed;
    private const int DebounceDelayMs = 300;

    public event EventHandler<string>? Error;

    public FileSystemConfigWatcher(string? configDir = null)
    {
        _configDir = NormalizeConfigDirectory(configDir);
        _configPath = Path.Combine(_configDir, "settings.json");
    }

    public bool IsWatching => _watcher?.EnableRaisingEvents ?? false;
    public string ConfigPath => _configPath;

    public RuntimeSettingsData? LoadSettings()
    {
        try
        {
            if (!File.Exists(_configPath))
                return null;

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<RuntimeSettingsData>(json);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to load settings: {ex.Message}");
            return null;
        }
    }

    public void SaveSettings(RuntimeSettingsData settings)
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to save settings: {ex.Message}");
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_watcher != null) return;

        Directory.CreateDirectory(_configDir);

        _watcher = new FileSystemWatcher(_configDir, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        Console.WriteLine($"[ConfigWatcher] Watching '{_configPath}'");
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigFileChanged;
            _watcher.Created -= OnConfigFileChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        Debounce(() =>
        {
            Thread.Sleep(50);
            try
            {
                var settings = LoadSettings();
                if (settings != null)
                {
                    RuntimeSettings.Apply(settings);
                    Console.WriteLine($"[ConfigWatcher] Settings reloaded from '{_configPath}'");
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Error reloading settings: {ex.Message}");
            }
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Error?.Invoke(this, $"Watcher error: {e.GetException().Message}");
        try { Stop(); Start(); } catch { }
    }

    private void Debounce(Action action)
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                try { action(); }
                catch (Exception ex) { Error?.Invoke(this, $"Debounce error: {ex.Message}"); }
            }, null, DebounceDelayMs, Timeout.Infinite);
        }
    }

    private static string NormalizeConfigDirectory(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "dotty");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileSystemConfigWatcher));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _debounceTimer?.Dispose();
    }
}
