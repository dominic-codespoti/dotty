using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Dotty.Abstractions.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dotty.App.Services;

public sealed class CSharpConfigWatcher : IDisposable
{
    private readonly string _configPath;
    private readonly string _configDir;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _pollTimer;
    private DateTime _lastPollWrite;
    private readonly object _lock = new();
    private bool _disposed;
    private AssemblyLoadContext? _loadContext;
    private const int DebounceDelayMs = 500;
    private static readonly string[] s_assemblyPaths = new[]
    {
        typeof(IDottyConfig).Assembly.Location,
        typeof(object).Assembly.Location,
        typeof(System.Linq.Enumerable).Assembly.Location,
    };

    public event EventHandler<RuntimeSettingsData>? ConfigCompiled;
    public event EventHandler<string>? Error;

    public CSharpConfigWatcher()
    {
        _configDir = ConfigGeneratorService.ProjectDir;
        _configPath = ConfigGeneratorService.ConfigPath;
        try { _lastPollWrite = File.GetLastWriteTimeUtc(_configPath); } catch { _lastPollWrite = DateTime.MinValue; }
    }

    public bool IsWatching => _watcher?.EnableRaisingEvents ?? false;
    public string ConfigPath => _configPath;

    public RuntimeSettingsData? CompileAndLoad()
    {
        if (!File.Exists(_configPath))
            return null;

        try
        {
            var source = File.ReadAllText(_configPath);

            // Add using statements and wrap in a class that implements IDottyConfig
            // The user's Config.cs is a partial class implementing IDottyConfig.
            // We compile it as a standalone assembly referencing Dotty.Abstractions.
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new List<MetadataReference>();
            foreach (var asmPath in s_assemblyPaths)
                references.Add(MetadataReference.CreateFromFile(asmPath));

            // Also add all assemblies referenced by Dotty.Abstractions
            foreach (var refAsm in typeof(IDottyConfig).Assembly.GetReferencedAssemblies())
            {
                try
                {
                    var asm = Assembly.Load(refAsm);
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                catch { }
            }

            var compilation = CSharpCompilation.Create(
                "UserConfig_Generated",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithMetadataImportOptions(MetadataImportOptions.All));

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success)
            {
                var errors = string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                Error?.Invoke(this, $"Compilation failed: {errors}");
                return null;
            }

            ms.Position = 0;

            // Unload previous context
            if (_loadContext != null)
            {
                _loadContext.Unload();
                _loadContext = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            _loadContext = new AssemblyLoadContext("UserConfig", isCollectible: true);
            var assembly = _loadContext.LoadFromStream(ms);

            // Find the config class implementing IDottyConfig
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(IDottyConfig).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var instance = (IDottyConfig)Activator.CreateInstance(type)!;
                    return ExtractSettings(instance);
                }
            }

            Error?.Invoke(this, "No class implementing IDottyConfig found in compiled config");
            return null;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to compile config: {ex.Message}");
            return null;
        }
    }

    private static RuntimeSettingsData ExtractSettings(IDottyConfig config)
    {
        var rs = new RuntimeSettingsData();

        try { rs.FontFamily = config.FontFamily; } catch { }
        try { rs.FontSize = config.FontSize; } catch { }
        try
        {
            if (config.Cursor != null)
            {
                rs.CursorShape = config.Cursor.Shape.ToString();
                rs.CursorBlink = config.Cursor.Blink;
                rs.CursorBlinkIntervalMs = config.Cursor.BlinkIntervalMs;
            }
        }
        catch { }
        try
        {
            if (config.Colors != null)
            {
                rs.Background = $"#{config.Colors.Background:X8}";
                rs.Foreground = $"#{config.Colors.Foreground:X8}";
            }
        }
        catch { }
        try { rs.CellPadding = config.CellPadding; } catch { }

        return rs;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_watcher != null) return;

        Directory.CreateDirectory(_configDir);

        _watcher = new FileSystemWatcher(_configDir, "Config.cs")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = false
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.IncludeSubdirectories = false;
        _watcher.InternalBufferSize = 32768;
        _watcher.EnableRaisingEvents = true;

        // Also watch for changes via polling as fallback (for editors that use atomic saves)
        _pollTimer = new Timer(_ =>
        {
            var lastWrite = File.GetLastWriteTimeUtc(_configPath);
            if (lastWrite > _lastPollWrite)
            {
                _lastPollWrite = lastWrite;
                OnConfigFileChanged(null, new FileSystemEventArgs(WatcherChangeTypes.Changed, _configDir, "Config.cs"));
            }
        }, null, 2000, 2000);

        Console.WriteLine($"[CSharpConfig] Watching '{_configPath}'");
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigFileChanged;
            _watcher.Created -= OnConfigFileChanged;
            _watcher.Renamed -= OnConfigFileRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
        _pollTimer?.Dispose();
        _pollTimer = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        Debounce(() =>
        {
            Thread.Sleep(100);
            try
            {
                var settings = CompileAndLoad();
                if (settings != null)
                {
                    RuntimeSettings.Apply(settings);
                    Console.WriteLine($"[CSharpConfig] Config recompiled and applied");
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Error: {ex.Message}");
            }
        });
    }

    private void OnConfigFileRenamed(object sender, RenamedEventArgs e)
    {
        if (e.Name == "Config.cs" || e.FullPath == _configPath)
            OnConfigFileChanged(sender, e);
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

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CSharpConfigWatcher));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pollTimer?.Dispose();
        _debounceTimer?.Dispose();
        _loadContext?.Unload();
    }
}
