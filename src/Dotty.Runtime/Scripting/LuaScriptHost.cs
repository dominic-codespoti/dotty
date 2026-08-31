using System;
using System.IO;
using Dotty.Runtime.Config;
using Dotty.Runtime.Tabs;
using NLua;

namespace Dotty.Runtime.Scripting;

/// <summary>
/// Host engine that executes Lua user scripts, exposes the `dotty` API, and hot-reloads on save.
/// </summary>
public sealed class LuaScriptHost : IDisposable
{
    private Lua? _lua;
    private FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private bool _disposed;

    public LuaConfigProxy ConfigProxy { get; private set; } = null!;
    public LuaTabsProxy TabsProxy { get; private set; } = null!;
    public LuaKeybindRegistry Keybinds { get; } = new();
    public LuaHookManager Hooks { get; } = new();

    public event Action? ConfigReloaded;

    public static string GetConfigLuaPath()
    {
        string configLua = Path.Combine(PlatformPaths.LuaDirectory, "config.lua");
        if (File.Exists(configLua)) return configLua;

        string initLua = Path.Combine(PlatformPaths.LuaDirectory, "init.lua");
        if (File.Exists(initLua)) return initLua;

        return configLua;
    }

    public void Initialize(DottyUserConfig config, TerminalTabManager tabManager)
    {
        lock (_lock)
        {
            _lua?.Dispose();
            _lua = new Lua();
            _lua.State.Encoding = System.Text.Encoding.UTF8;

            ConfigProxy = new LuaConfigProxy(config);
            TabsProxy = new LuaTabsProxy(tabManager);
            Keybinds.Clear();
            Hooks.Clear();

            // Create global 'dotty' table
            _lua.NewTable("dotty");
            var dottyTable = _lua.GetTable("dotty");

            dottyTable["config"] = ConfigProxy;
            dottyTable["tabs"] = TabsProxy;

            // dotty.bind("ctrl+shift+t", function() ... end)
            _lua.RegisterFunction("dotty.bind", this, typeof(LuaScriptHost).GetMethod(nameof(RegisterKeybind)));

            // dotty.on("format_tab_title", function() ... end)
            _lua.RegisterFunction("dotty.on", this, typeof(LuaScriptHost).GetMethod(nameof(RegisterHook)));

            // dotty.log(msg)
            _lua.RegisterFunction("dotty.log", this, typeof(LuaScriptHost).GetMethod(nameof(LogMessage)));

            // Setup require("dotty") and ergonomic Lua wrappers
            // Setup require("dotty") and ergonomic Lua wrappers
            string setupScript = @"
                local raw_config = dotty.config
                local raw_tabs = dotty.tabs

                local tabs_wrapper = {
                    new = function(opts) return raw_tabs:new(opts) end,
                    close = function(idx) raw_tabs:close(idx) end,
                    select = function(idx) raw_tabs:select(idx) end,
                    next = function() raw_tabs:next() end,
                    prev = function() raw_tabs:prev() end,
                }

                setmetatable(tabs_wrapper, {
                    __index = function(t, k)
                        if k == 'count' then return raw_tabs.count
                        elseif k == 'active_index' then return raw_tabs.active_index
                        elseif k == 'active' then return raw_tabs.active
                        end
                        return raw_tabs[k]
                    end
                })
                dotty.tabs = tabs_wrapper

                local config_wrapper = {
                    apply_table = function(self_or_tbl, maybe_tbl)
                        local tbl = maybe_tbl or self_or_tbl
                        raw_config:apply_table(tbl)
                    end,
                    apply = function(self_or_tbl, maybe_tbl)
                        local tbl = maybe_tbl or self_or_tbl
                        raw_config:apply_table(tbl)
                    end
                }

                setmetatable(config_wrapper, {
                    __index = function(t, k)
                        return raw_config[k]
                    end,
                    __newindex = function(t, k, v)
                        if k == 'theme' then
                            raw_config.theme = v
                        elseif type(v) == 'table' then
                            raw_config:apply_table({ [k] = v })
                        else
                            raw_config[k] = v
                        end
                    end
                })
                dotty.config = config_wrapper

                package.loaded['dotty'] = dotty
            ";
            _lua.DoString(setupScript);
        }
    }

    public void RegisterKeybind(string chord, LuaFunction callback)
    {
        Keybinds.Register(chord, callback);
    }

    public void RegisterHook(string eventName, LuaFunction callback)
    {
        Hooks.Register(eventName, callback);
    }

    public void LogMessage(object? message)
    {
    }

    public bool LoadScript(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        lock (_lock)
        {
            try
            {
                _lua?.DoFile(path);
                EnsureWatcher(Path.GetDirectoryName(path)!);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lua Error] Failed to execute '{path}': {ex.Message}");
                return false;
            }
        }
    }

    public bool ExecuteString(string luaCode)
    {
        lock (_lock)
        {
            try
            {
                _lua?.DoString(luaCode);
                return true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"[Lua Error] {ex.Message}\n{ex}", ex);
            }
        }
    }

    private void EnsureWatcher(string directory)
    {
        if (_watcher != null || !Directory.Exists(directory)) return;

        try
        {
            _watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                Filter = "*.lua",
                EnableRaisingEvents = true
            };

            _watcher.Changed += (s, e) => ReloadDebounced(e.FullPath);
            _watcher.Created += (s, e) => ReloadDebounced(e.FullPath);
        }
        catch { }
    }

    private DateTime _lastReload = DateTime.MinValue;
    private void ReloadDebounced(string path)
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastReload).TotalMilliseconds < 200) return;
            _lastReload = DateTime.UtcNow;
        }

        System.Threading.Tasks.Task.Delay(60).ContinueWith(_ =>
        {
            try
            {
                if (File.Exists(path))
                {
                    lock (_lock)
                    {
                        _lua?.DoFile(path);
                    }
                    ConfigReloaded?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lua Hot-Reload Error] {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _lua?.Dispose();
    }
}
