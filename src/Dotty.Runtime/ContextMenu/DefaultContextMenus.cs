using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// Helper providing default context menus for tabs and the terminal viewport. Shortcut labels come from the live
/// binding table (<paramref name="shortcutFor"/>, typically <c>KeybindingManager.GetShortcutLabel</c>), so a menu never
/// advertises a chord that does not run its item; an unbound action shows no shortcut.
/// </summary>
public static class DefaultContextMenus
{
    public static IReadOnlyList<ContextMenuItem> BuildTabMenu(
        Action onSplitRight,
        Action onSplitDown,
        Action onNewTab,
        Action onClose,
        Func<TerminalAction, string?>? shortcutFor = null) => new[]
    {
        new ContextMenuItem(id: "tab.split_right", label: "Split Right", shortcut: Label(shortcutFor, TerminalAction.SplitVertical), action: onSplitRight, icon: "◫"),
        new ContextMenuItem(id: "tab.split_down", label: "Split Down", shortcut: Label(shortcutFor, TerminalAction.SplitHorizontal), action: onSplitDown, icon: "⊟"),
        ContextMenuItem.Separator("tab.sep1"),
        new ContextMenuItem(id: "tab.new", label: "New Tab", shortcut: Label(shortcutFor, TerminalAction.NewTab), action: onNewTab, icon: "+"),
        ContextMenuItem.Separator("tab.sep2"),
        new ContextMenuItem(id: "tab.close", label: "Close Tab", shortcut: Label(shortcutFor, TerminalAction.CloseTab), action: onClose, icon: "×")
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
        Action? onNextPrompt = null,
        Func<TerminalAction, string?>? shortcutFor = null) => new[]
    {
        new ContextMenuItem(id: "terminal.copy", label: "Copy", shortcut: Label(shortcutFor, TerminalAction.Copy), action: onCopy, isDisabled: !hasSelection, icon: "⎘"),
        new ContextMenuItem(id: "terminal.copy_command_output", label: "Copy Command Output", shortcut: Label(shortcutFor, TerminalAction.CopyCommandOutput), action: onCopyCommandOutput ?? onCopy, isDisabled: !hasCommandOutput || onCopyCommandOutput is null, icon: "↧"),
        new ContextMenuItem(id: "terminal.previous_prompt", label: "Previous Prompt", shortcut: Label(shortcutFor, TerminalAction.PreviousPrompt), action: onPreviousPrompt ?? onCopy, isDisabled: !hasShellPrompts || onPreviousPrompt is null, icon: "↑"),
        new ContextMenuItem(id: "terminal.next_prompt", label: "Next Prompt", shortcut: Label(shortcutFor, TerminalAction.NextPrompt), action: onNextPrompt ?? onCopy, isDisabled: !hasShellPrompts || onNextPrompt is null, icon: "↓"),
        new ContextMenuItem(id: "terminal.paste", label: "Paste", shortcut: Label(shortcutFor, TerminalAction.Paste), action: onPaste, icon: "📋"),
        new ContextMenuItem(id: "terminal.select_all", label: "Select All", shortcut: Label(shortcutFor, TerminalAction.SelectAll), action: onSelectAll, icon: "⬚"),
        ContextMenuItem.Separator("terminal.sep1"),
        new ContextMenuItem(id: "terminal.split_right", label: "Split Pane Right", shortcut: Label(shortcutFor, TerminalAction.SplitVertical), action: onSplitRight, icon: "◫"),
        new ContextMenuItem(id: "terminal.split_down", label: "Split Pane Down", shortcut: Label(shortcutFor, TerminalAction.SplitHorizontal), action: onSplitDown, icon: "⊟"),
        ContextMenuItem.Separator("terminal.sep2"),
        new ContextMenuItem(id: "terminal.clear", label: "Clear Buffer", shortcut: Label(shortcutFor, TerminalAction.Clear), action: onClear, icon: "⌫")
    };

    private static string? Label(Func<TerminalAction, string?>? shortcutFor, TerminalAction action) => shortcutFor?.Invoke(action);
}
