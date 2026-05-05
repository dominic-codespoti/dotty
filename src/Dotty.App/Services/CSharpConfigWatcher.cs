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

    public event EventHandler<RuntimeSettingsData>? ConfigCompiled;
    public event EventHandler<string>? Error;

    public bool IsWatching => _watcher?.EnableRaisingEvents ?? false;
    public string ConfigPath => _configPath;

    public CSharpConfigWatcher()
    {
        _configDir = ConfigGeneratorService.ProjectDir;
        _configPath = ConfigGeneratorService.ConfigPath;
        try { _lastPollWrite = File.GetLastWriteTimeUtc(_configPath); } catch { _lastPollWrite = DateTime.MinValue; }
    }

    public RuntimeSettingsData? CompileAndLoad()
    {
        if (!File.Exists(_configPath))
        {
            Console.WriteLine($"[CSharpConfig] Config file not found: {_configPath}");
            return null;
        }

        try
        {
            var source = File.ReadAllText(_configPath);
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            // Collect all assembly references recursively
            var references = new List<MetadataReference>();
            var loadedAsm = new HashSet<string>();

            void AddAssembly(Assembly asm)
            {
                if (asm == null || asm.IsDynamic) return;
                if (!loadedAsm.Add(asm.Location)) return;
                try { references.Add(MetadataReference.CreateFromFile(asm.Location)); } catch { }
                foreach (var r in asm.GetReferencedAssemblies())
                {
                    try { AddAssembly(Assembly.Load(r)); } catch { }
                }
            }

            AddAssembly(typeof(IDottyConfig).Assembly);
            AddAssembly(typeof(object).Assembly);
            try { AddAssembly(Assembly.Load("System.Runtime")); } catch { }
            try { AddAssembly(Assembly.Load("System.Console")); } catch { }

            var compilation = CSharpCompilation.Create(
                "UserConfig_Gen",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithMetadataImportOptions(MetadataImportOptions.All));

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success)
            {
                var errors = string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                Console.Error.WriteLine($"[CSharpConfig] Compilation failed: {errors}");
                Error?.Invoke(this, $"Compilation failed: {errors}");
                return null;
            }

            Console.WriteLine($"[CSharpConfig] Compiled ({ms.Length} bytes)");
            ms.Position = 0;

            _loadContext?.Unload();
            _loadContext = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            _loadContext = new AssemblyLoadContext("UserCfg", isCollectible: true);
            var assembly = _loadContext.LoadFromStream(ms);

            foreach (var type in assembly.GetTypes())
            {
                if (typeof(IDottyConfig).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var instance = (IDottyConfig)Activator.CreateInstance(type)!;
                    Console.WriteLine($"[CSharpConfig] Loaded {type.Name}");
                    return ExtractSettings(instance);
                }
            }

            Error?.Invoke(this, "No IDottyConfig implementor found");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CSharpConfig] Exception: {ex.Message}");
            Error?.Invoke(this, $"Exception: {ex.Message}");
            return null;
        }
    }

    private static RuntimeSettingsData ExtractSettings(IDottyConfig config)
    {
        var rs = new RuntimeSettingsData();
        void Try(Action a) { try { a(); } catch { } }

        Try(() => rs.FontFamily = config.FontFamily);
        Try(() => rs.FontSize = config.FontSize);
        Try(() => rs.CellPadding = config.CellPadding);
        Try(() => rs.Background = config.Colors?.Background is { } bg ? $"#{bg:X8}" : null);
        Try(() => rs.Foreground = config.Colors?.Foreground is { } fg ? $"#{fg:X8}" : null);
        Try(() =>
        {
            if (config.Cursor != null)
            {
                rs.CursorShape = config.Cursor.Shape.ToString();
                rs.CursorBlink = config.Cursor.Blink;
                rs.CursorBlinkIntervalMs = config.Cursor.BlinkIntervalMs;
            }
        });
        Try(() => rs.ContentPaddingLeft = config.ContentPadding?.Left);
        Try(() => rs.ContentPaddingTop = config.ContentPadding?.Top);
        Try(() => rs.ContentPaddingRight = config.ContentPadding?.Right);
        Try(() => rs.ContentPaddingBottom = config.ContentPadding?.Bottom);

        return rs;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_watcher != null) return;

        Directory.CreateDirectory(_configDir);

        _watcher = new FileSystemWatcher(_configDir, "Config.cs")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = false
        };

        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.IncludeSubdirectories = false;
        _watcher.InternalBufferSize = 32768;
        _watcher.EnableRaisingEvents = true;

        _pollTimer = new Timer(_ =>
        {
            try
            {
                if (!File.Exists(_configPath)) return;
                var lastWrite = File.GetLastWriteTimeUtc(_configPath);
                if (lastWrite > _lastPollWrite)
                {
                    _lastPollWrite = lastWrite;
                    OnConfigFileChanged(null, new FileSystemEventArgs(WatcherChangeTypes.Changed, _configDir, "Config.cs"));
                }
            }
            catch { }
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

    private void OnConfigFileChanged(object? sender, FileSystemEventArgs e)
    {
        Debounce(() =>
        {
            Thread.Sleep(100);
            try
            {
                Console.WriteLine($"[CSharpConfig] Change detected, compiling...");
                var settings = CompileAndLoad();
                if (settings != null)
                {
                    ConfigCompiled?.Invoke(this, settings);
                    Console.WriteLine($"[CSharpConfig] ✓ Reloaded");
                }
                else
                {
                    Console.WriteLine($"[CSharpConfig] ✗ CompileAndLoad returned null");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CSharpConfig] Error: {ex.Message}");
                Error?.Invoke(this, ex.Message);
            }
        });
    }

    private void OnConfigFileRenamed(object? sender, RenamedEventArgs e)
    {
        if (e.Name == "Config.cs" || e.FullPath == _configPath)
            OnConfigFileChanged(sender, e);
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
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
                catch (Exception ex) { Error?.Invoke(this, $"Debounce: {ex.Message}"); }
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
        _loadContext?.Unload();
    }
}
