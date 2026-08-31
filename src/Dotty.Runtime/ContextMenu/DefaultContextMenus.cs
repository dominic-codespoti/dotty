using System;
using System.Collections.Generic;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// Helper providing default context menus for tabs and the terminal viewport.
/// </summary>
public static class DefaultContextMenus
{
    /// <summary>
    /// Builds a default context menu for tab headers.
    /// </summary>
    /// <param name="tabIndex">Index of the targeted tab.</param>
    /// <param name="onSplitRight">Action to split the active pane to the right.</param>
    /// <param name="onSplitDown">Action to split the active pane downwards.</param>
    /// <param name="onRename">Action to rename the targeted tab.</param>
    /// <param name="onClose">Action to close the targeted tab.</param>
    /// <returns>A list of configured <see cref="ContextMenuItem"/>s.</returns>
    public static IReadOnlyList<ContextMenuItem> BuildTabMenu(
        int tabIndex,
        Action onSplitRight,
        Action onSplitDown,
        Action onRename,
        Action onClose)
    {
        return new[]
        {
            new ContextMenuItem(
                id: "tab.split_right",
                label: "Split Right",
                shortcut: "Ctrl+Shift+E",
                action: onSplitRight,
                icon: "◫"),
            new ContextMenuItem(
                id: "tab.split_down",
                label: "Split Down",
                shortcut: "Ctrl+Shift+O",
                action: onSplitDown,
                icon: "⊟"),
            ContextMenuItem.Separator("tab.sep1"),
            new ContextMenuItem(
                id: "tab.rename",
                label: "Rename Tab...",
                action: onRename,
                icon: "✎"),
            ContextMenuItem.Separator("tab.sep2"),
            new ContextMenuItem(
                id: "tab.close",
                label: "Close Tab",
                shortcut: "Ctrl+Shift+W",
                action: onClose,
                icon: "×")
        };
    }

    /// <summary>
    /// Builds a default context menu for terminal canvas / right-click interactions.
    /// </summary>
    /// <param name="hasSelection">Whether there is active text selection.</param>
    /// <param name="onCopy">Action to copy selected text to clipboard.</param>
    /// <param name="onPaste">Action to paste from clipboard.</param>
    /// <param name="onSelectAll">Action to select all text in the terminal buffer.</param>
    /// <param name="onSplitRight">Action to split pane right.</param>
    /// <param name="onSplitDown">Action to split pane down.</param>
    /// <param name="onClear">Action to clear the terminal screen buffer.</param>
    /// <returns>A list of configured <see cref="ContextMenuItem"/>s.</returns>
    public static IReadOnlyList<ContextMenuItem> BuildTerminalMenu(
        bool hasSelection,
        Action onCopy,
        Action onPaste,
        Action onSelectAll,
        Action onSplitRight,
        Action onSplitDown,
        Action onClear)
    {
        return new[]
        {
            new ContextMenuItem(
                id: "terminal.copy",
                label: "Copy",
                shortcut: "Ctrl+Shift+C",
                action: onCopy,
                isDisabled: !hasSelection,
                icon: "⎘"),
            new ContextMenuItem(
                id: "terminal.paste",
                label: "Paste",
                shortcut: "Ctrl+Shift+V",
                action: onPaste,
                icon: "📋"),
            new ContextMenuItem(
                id: "terminal.select_all",
                label: "Select All",
                shortcut: "Ctrl+Shift+A",
                action: onSelectAll,
                icon: "⬚"),
            ContextMenuItem.Separator("terminal.sep1"),
            new ContextMenuItem(
                id: "terminal.split_right",
                label: "Split Pane Right",
                shortcut: "Ctrl+Shift+E",
                action: onSplitRight,
                icon: "◫"),
            new ContextMenuItem(
                id: "terminal.split_down",
                label: "Split Pane Down",
                shortcut: "Ctrl+Shift+O",
                action: onSplitDown,
                icon: "⊟"),
            ContextMenuItem.Separator("terminal.sep2"),
            new ContextMenuItem(
                id: "terminal.clear",
                label: "Clear Buffer",
                shortcut: "Ctrl+K",
                action: onClear,
                icon: "⌫")
        };
    }
}
