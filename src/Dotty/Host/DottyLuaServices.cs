using System;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;

namespace Dotty.Silk;

internal sealed class DottyLuaServices : ILuaHostServices
{
    private readonly Action<Action> _post;
    private readonly Func<string?, string?, TerminalTab> _createTab;
    private readonly Func<TerminalTab, LeafPane, SplitDirection, string?, string?, LeafPane> _splitPane;
    private readonly Func<TerminalAction, bool> _executeAction;
    private readonly Action _applyConfig;
    private readonly Action _invalidate;

    internal DottyLuaServices(
        Action<Action> post,
        Func<string?, string?, TerminalTab> createTab,
        Func<TerminalTab, LeafPane, SplitDirection, string?, string?, LeafPane> splitPane,
        Func<TerminalAction, bool> executeAction,
        Action applyConfig,
        Action invalidate)
    {
        _post = post;
        _createTab = createTab;
        _splitPane = splitPane;
        _executeAction = executeAction;
        _applyConfig = applyConfig;
        _invalidate = invalidate;
    }

    public void Post(Action action) => _post(action);

    public TerminalTab CreateTab(string? workingDirectory, string? shell) =>
        _createTab(workingDirectory, shell);

    public LeafPane SplitPane(
        TerminalTab tab,
        LeafPane target,
        SplitDirection direction,
        string? workingDirectory,
        string? shell) =>
        _splitPane(tab, target, direction, workingDirectory, shell);

    public bool TryExecuteAction(TerminalAction action) => _executeAction(action);

    public void ApplyConfig() => _applyConfig();

    public void Invalidate() => _invalidate();

    public void Log(LuaMessageLevel level, string message)
    {
        var writer = level is LuaMessageLevel.Error or LuaMessageLevel.Warning
            ? Console.Error
            : Console.Out;
        writer.WriteLine(message);
    }
}
