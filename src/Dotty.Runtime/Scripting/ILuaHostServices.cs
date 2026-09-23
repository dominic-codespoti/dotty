using System;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;

namespace Dotty.Runtime.Scripting;

/// <summary>
/// Window-host services consumed by <see cref="LuaScriptHost"/>. Every member except
/// <see cref="Post"/> and <see cref="Log"/> is invoked on the UI thread only.
/// </summary>
public interface ILuaHostServices
{
    /// <summary>Queues work onto the UI thread. Safe to call from any thread.</summary>
    void Post(Action action);

    /// <summary>Creates a themed, correctly sized tab, selects it, and returns it.</summary>
    TerminalTab CreateTab(string? workingDirectory, string? shell);

    /// <summary>Splits <paramref name="target"/> with a themed, correctly sized session.</summary>
    LeafPane SplitPane(TerminalTab tab, LeafPane target, SplitDirection direction, string? workingDirectory, string? shell);

    /// <summary>Runs a named action against the active tab. Returns false when not applicable.</summary>
    bool TryExecuteAction(TerminalAction action);

    /// <summary>Re-applies the current configuration after a runtime (non-evaluation) Lua mutation.</summary>
    void ApplyConfig();

    /// <summary>Requests a chrome redraw (tab bar, status area).</summary>
    void Invalidate();

    /// <summary>Records a script log line or error. Safe to call from any thread.</summary>
    void Log(LuaMessageLevel level, string message);
}

public enum LuaMessageLevel
{
    Info,
    Warning,
    Error,
}
