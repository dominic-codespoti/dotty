using System;
using System.Text;
using Dotty.Runtime.Tabs;
using NLua;

namespace Dotty.Runtime.Scripting;

/// <summary>
/// Lua proxy for managing terminal tabs.
/// </summary>
public sealed class LuaTabsProxy
{
    private readonly TerminalTabManager _tabManager;

    public LuaTabsProxy(TerminalTabManager tabManager)
    {
        _tabManager = tabManager ?? throw new ArgumentNullException(nameof(tabManager));
    }

    public int count => _tabManager.Count;

    public int active_index => _tabManager.ActiveIndex + 1; // 1-based indexing for Lua

    public LuaTabHandle? active
    {
        get
        {
            var tab = _tabManager.ActiveTab;
            return tab != null ? new LuaTabHandle(tab, _tabManager.ActiveIndex) : null;
        }
    }

    public LuaTabHandle @new(LuaTable? options = null)
    {
        string? cwd = null;
        string? shell = null;

        if (options != null)
        {
            foreach (var keyObj in options.Keys)
            {
                string k = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
                var val = options[keyObj];
                if (k == "cwd" && val is string sCwd) cwd = sCwd;
                else if (k == "shell" && val is string sShell) shell = sShell;
            }
        }

        var tab = _tabManager.CreateTab(workingDirectory: cwd, shell: shell);
        int index = _tabManager.ActiveIndex;
        return new LuaTabHandle(tab, index);
    }

    public void close(int index)
    {
        int zeroIndex = index - 1; // 1-based Lua to 0-based C#
        if (zeroIndex >= 0 && zeroIndex < _tabManager.Tabs.Count)
        {
            _tabManager.CloseTab(_tabManager.Tabs[zeroIndex]);
        }
    }

    public void select(int index)
    {
        int zeroIndex = index - 1;
        if (zeroIndex >= 0 && zeroIndex < _tabManager.Tabs.Count)
        {
            _tabManager.SelectTab(zeroIndex);
        }
    }

    public void next() => _tabManager.SelectNextTab();

    public void prev() => _tabManager.SelectPreviousTab();
}

/// <summary>
/// Represents a handle to a specific <see cref="TerminalTab"/> in Lua.
/// </summary>
public sealed class LuaTabHandle
{
    private readonly TerminalTab _tab;
    private readonly int _index;

    public LuaTabHandle(TerminalTab tab, int index)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
        _index = index;
    }

    public int index => _index + 1; // 1-based

    public string title => _tab.Title;

    public string? cwd => _tab.WorkingDirectory;

    public void send(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _tab.Session.WriteInput(bytes);
    }
}
