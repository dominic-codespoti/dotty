using System;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// Represents an entry in a context menu.
/// </summary>
public sealed class ContextMenuItem
{
    public string Id { get; init; }
    public string Label { get; init; }
    public string? Shortcut { get; init; }
    public Action? Action { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsDisabled { get; init; }
    public string? Icon { get; init; }

    public ContextMenuItem(
        string id,
        string label,
        string? shortcut = null,
        Action? action = null,
        bool isSeparator = false,
        bool isDisabled = false,
        string? icon = null)
    {
        Id = id ?? string.Empty;
        Label = label ?? string.Empty;
        Shortcut = shortcut;
        Action = action;
        IsSeparator = isSeparator;
        IsDisabled = isDisabled;
        Icon = icon;
    }

    /// <summary>
    /// Factory for creating a separator item.
    /// </summary>
    public static ContextMenuItem Separator(string id = "separator") =>
        new(id, string.Empty, isSeparator: true);

    /// <summary>
    /// Factory for creating a standard actionable menu item.
    /// </summary>
    public static ContextMenuItem Item(
        string id,
        string label,
        Action? action,
        string? shortcut = null,
        string? icon = null,
        bool isDisabled = false) =>
        new(id, label, shortcut, action, isSeparator: false, isDisabled, icon);
}
