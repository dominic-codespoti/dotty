using System;
using System.Collections.Generic;

namespace Dotty.Runtime.ContextMenu;

/// <summary>Helper providing default context menus for tabs and the terminal viewport.</summary>
public static class DefaultContextMenus
{
    public static IReadOnlyList<ContextMenuItem> BuildTabMenu(Action onSplitRight, Action onSplitDown, Action onNewTab, Action onClose) => new[]
    {
        new ContextMenuItem(id: "tab.split_right", label: "Split Right", shortcut: "Ctrl+Shift+E", action: onSplitRight, icon: "◫"),
        new ContextMenuItem(id: "tab.split_down", label: "Split Down", shortcut: "Ctrl+Shift+O", action: onSplitDown, icon: "⊟"),
        ContextMenuItem.Separator("tab.sep1"),
        new ContextMenuItem(id: "tab.new", label: "New Tab", shortcut: "Ctrl+Shift+T", action: onNewTab, icon: "+"),
        ContextMenuItem.Separator("tab.sep2"),
        new ContextMenuItem(id: "tab.close", label: "Close Tab", shortcut: "Ctrl+Shift+W", action: onClose, icon: "×")
    };

    /// <summary>Builds the standard terminal context menu with optional shell integration actions.</summary>
    public static IReadOnlyList<ContextMenuItem> BuildTerminalMenu(
        bool hasSelection,
        Action onCopy,
        Action onPaste,
        Action onSelectAll,
        Action onSplitRight,
        Action onSplitDown,
        Action onClear,
        bool hasCommandOutput = false,
        Action? onCopyCommandOutput = null,
        bool hasShellPrompts = false,
        Action? onPreviousPrompt = null,
        Action? onNextPrompt = null) => new[]
    {
        new ContextMenuItem(id: "terminal.copy", label: "Copy", shortcut: "Ctrl+Shift+C", action: onCopy, isDisabled: !hasSelection, icon: "⎘"),
        new ContextMenuItem(id: "terminal.copy_command_output", label: "Copy Command Output", shortcut: "Ctrl+Alt+O", action: onCopyCommandOutput ?? onCopy, isDisabled: !hasCommandOutput || onCopyCommandOutput is null, icon: "↧"),
        new ContextMenuItem(id: "terminal.previous_prompt", label: "Previous Prompt", shortcut: "Ctrl+Shift+PageUp", action: onPreviousPrompt ?? onCopy, isDisabled: !hasShellPrompts || onPreviousPrompt is null, icon: "↑"),
        new ContextMenuItem(id: "terminal.next_prompt", label: "Next Prompt", shortcut: "Ctrl+Shift+PageDown", action: onNextPrompt ?? onCopy, isDisabled: !hasShellPrompts || onNextPrompt is null, icon: "↓"),
        new ContextMenuItem(id: "terminal.paste", label: "Paste", shortcut: "Ctrl+Shift+V", action: onPaste, icon: "📋"),
        new ContextMenuItem(id: "terminal.select_all", label: "Select All", shortcut: "Ctrl+Shift+A", action: onSelectAll, icon: "⬚"),
        ContextMenuItem.Separator("terminal.sep1"),
        new ContextMenuItem(id: "terminal.split_right", label: "Split Pane Right", shortcut: "Ctrl+Shift+E", action: onSplitRight, icon: "◫"),
        new ContextMenuItem(id: "terminal.split_down", label: "Split Pane Down", shortcut: "Ctrl+Shift+O", action: onSplitDown, icon: "⊟"),
        ContextMenuItem.Separator("terminal.sep2"),
        new ContextMenuItem(id: "terminal.clear", label: "Clear Buffer", shortcut: "Ctrl+K", action: onClear, icon: "⌫")
    };
}
