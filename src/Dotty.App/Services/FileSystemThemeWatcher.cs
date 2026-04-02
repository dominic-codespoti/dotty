using System;
using System.IO;
using System.Threading;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

/// <summary>
/// Watches the user themes directory for changes and triggers hot reload.
/// Debounces file changes to avoid excessive reloads.
/// </summary>
public sealed class FileSystemThemeWatcher : IDisposable
{
    private readonly ThemeManager _themeManager;
    private readonly ThemeValidator _validator;
    private readonly string _watchPath;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();
    private bool _disposed;

    // Debounce delay in milliseconds
    private const int DebounceDelayMs = 300;

    /// <summary>
    /// Event raised when a theme change is detected and validated.
    /// </summary>
    public event EventHandler<ThemeFileChangedEventArgs>? ThemeFileChanged;

    /// <summary>
    /// Event raised when a validation error occurs during hot reload.
    /// </summary>
    public event EventHandler<ThemeValidationErrorEventArgs>? ValidationError;

    /// <summary>
    /// Creates a new FileSystemThemeWatcher.
    /// </summary>
    /// <param name="themeManager">The theme manager to update</param>
    /// <param name="watchPath">Directory to watch (defaults to user themes directory)</param>
    public FileSystemThemeWatcher(ThemeManager themeManager, string? watchPath = null)
    {
        _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
        _validator = new ThemeValidator();
        _watchPath = watchPath ?? UserThemeLoader.DefaultThemesDirectory;
    }

    /// <summary>
    /// Gets whether the watcher is currently active.
    /// </summary>
    public bool IsWatching => _watcher?.EnableRaisingEvents ?? false;

    /// <summary>
    /// Gets the directory being watched.
    /// </summary>
    public string WatchPath => _watchPath;

    /// <summary>
    /// Starts watching for theme file changes.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();

        if (_watcher != null)
        {
            // Already started
            return;
        }

        // Ensure directory exists
        if (!Directory.Exists(_watchPath))
        {
            try
            {
                Directory.CreateDirectory(_watchPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FileSystemThemeWatcher] Failed to create watch directory '{_watchPath}': {ex.Message}");
                return;
            }
        }

        _watcher = new FileSystemWatcher(_watchPath, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = false // Will enable after setting up events
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;

        _watcher.EnableRaisingEvents = true;

        Console.WriteLine($"[FileSystemThemeWatcher] Started watching '{_watchPath}'");
    }

    /// <summary>
    /// Stops watching for theme file changes.
    /// </summary>
    public void Stop()
    {
        ThrowIfDisposed();

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileDeleted;
            _watcher.Renamed -= OnFileRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        Console.WriteLine("[FileSystemThemeWatcher] Stopped watching");
    }

    /// <summary>
    /// Handles file changed/created events with debouncing.
    /// </summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore temporary files and non-JSON files
        if (!IsValidThemeFile(e.FullPath))
            return;

        Debounce(() => HandleFileChange(e.FullPath, e.ChangeType));
    }

    /// <summary>
    /// Handles file deleted events.
    /// </summary>
    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsValidThemeFile(e.FullPath))
            return;

        Debounce(() =>
        {
            Console.WriteLine($"[FileSystemThemeWatcher] Theme file deleted: {e.Name}");
            
            // Reload user themes (the deleted theme will be removed)
            _themeManager.LoadUserThemes();
            
            ThemeFileChanged?.Invoke(this, new ThemeFileChangedEventArgs(e.FullPath, e.Name ?? Path.GetFileName(e.FullPath), WatcherChangeTypes.Deleted, null));
        });
    }

    /// <summary>
    /// Handles file renamed events.
    /// </summary>
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsValidThemeFile(e.FullPath) && !IsValidThemeFile(e.OldFullPath))
            return;

        Debounce(() =>
        {
            Console.WriteLine($"[FileSystemThemeWatcher] Theme file renamed: {e.OldName} -> {e.Name}");
            
            // Reload user themes to pick up the rename
            _themeManager.LoadUserThemes();
            
            ThemeFileChanged?.Invoke(this, new ThemeFileChangedEventArgs(e.FullPath, e.Name ?? Path.GetFileName(e.FullPath), WatcherChangeTypes.Renamed, null));
        });
    }

    /// <summary>
    /// Handles FileSystemWatcher errors.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Console.Error.WriteLine($"[FileSystemThemeWatcher] Watcher error: {e.GetException().Message}");
        
        // Try to restart the watcher
        try
        {
            Stop();
            Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSystemThemeWatcher] Failed to restart watcher: {ex.Message}");
        }
    }

    /// <summary>
    /// Debounces an action by delaying execution.
    /// </summary>
    private void Debounce(Action action)
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FileSystemThemeWatcher] Error in debounced action: {ex.Message}");
                }
            }, null, DebounceDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Handles a file change event.
    /// </summary>
    private void HandleFileChange(string fullPath, WatcherChangeTypes changeType)
    {
        var fileName = Path.GetFileName(fullPath);
        
        Console.WriteLine($"[FileSystemThemeWatcher] Theme file changed: {fileName}");

        // Wait a moment for the file to be fully written
        Thread.Sleep(50);

        // Validate the file before reloading
        var validationResult = _validator.ValidateFile(fullPath);

        if (!validationResult.IsValid)
        {
            Console.Error.WriteLine($"[FileSystemThemeWatcher] Validation failed for '{fileName}':");
            foreach (var error in validationResult.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            ValidationError?.Invoke(this, new ThemeValidationErrorEventArgs(fullPath, fileName, validationResult));
            return;
        }

        // Log warnings if any
        foreach (var warning in validationResult.Warnings)
        {
            Console.WriteLine($"[FileSystemThemeWatcher] Warning for '{fileName}': {warning}");
        }

        try
        {
            // Load the specific theme to get its name
            var loader = new UserThemeLoader(Path.GetDirectoryName(fullPath)!);
            var theme = loader.LoadFromFile(fullPath);

            if (theme != null)
            {
                // Reload all user themes
                _themeManager.LoadUserThemes();

                // If this is the currently active theme, reapply it
                var currentTheme = _themeManager.CurrentTheme;
                var currentThemeName = _themeManager.GetType().Name; // Heuristic approach

                ThemeFileChanged?.Invoke(this, new ThemeFileChangedEventArgs(fullPath, fileName, changeType, theme));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSystemThemeWatcher] Failed to load theme '{fileName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a file is a valid theme file.
    /// </summary>
    private static bool IsValidThemeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Ignore temporary/swap files
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith(".") || fileName.StartsWith("~") || fileName.EndsWith(".tmp"))
            return false;

        // Must be .json
        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileSystemThemeWatcher));
        }
    }

    /// <summary>
    /// Disposes the watcher and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Stop();

        _debounceTimer?.Dispose();
    }
}

/// <summary>
/// Event arguments for theme file changes.
/// </summary>
public sealed class ThemeFileChangedEventArgs : EventArgs
{
    /// <summary>
    /// Full path to the changed file.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// Name of the changed file.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Type of change that occurred.
    /// </summary>
    public WatcherChangeTypes ChangeType { get; }

    /// <summary>
    /// The loaded theme definition (null for deletions).
    /// </summary>
    public ThemeDefinition? ThemeDefinition { get; }

    /// <summary>
    /// Creates new ThemeFileChangedEventArgs.
    /// </summary>
    public ThemeFileChangedEventArgs(string fullPath, string fileName, WatcherChangeTypes changeType, ThemeDefinition? themeDefinition)
    {
        FullPath = fullPath;
        FileName = fileName;
        ChangeType = changeType;
        ThemeDefinition = themeDefinition;
    }
}

/// <summary>
/// Event arguments for theme validation errors.
/// </summary>
public sealed class ThemeValidationErrorEventArgs : EventArgs
{
    /// <summary>
    /// Full path to the invalid file.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// Name of the invalid file.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The validation result containing errors.
    /// </summary>
    public ValidationResult ValidationResult { get; }

    /// <summary>
    /// Creates new ThemeValidationErrorEventArgs.
    /// </summary>
    public ThemeValidationErrorEventArgs(string fullPath, string fileName, ValidationResult validationResult)
    {
        FullPath = fullPath;
        FileName = fileName;
        ValidationResult = validationResult ?? throw new ArgumentNullException(nameof(validationResult));
    }
}
