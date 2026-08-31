using System;
using System.Collections.Generic;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// State model representing an active floating context menu popup.
/// </summary>
public sealed class ContextMenuModel
{
    /// <summary>Horizontal origin in window pixels where the menu was triggered.</summary>
    public float X { get; set; }

    /// <summary>Vertical origin in window pixels where the menu was triggered.</summary>
    public float Y { get; set; }

    /// <summary>The collection of menu items.</summary>
    public IReadOnlyList<ContextMenuItem> Items { get; set; }

    /// <summary>Zero-based index of the currently hovered menu item, or -1 if none.</summary>
    public int HoveredIndex { get; set; } = -1;

    /// <summary>Whether the context menu is currently visible and receiving interaction.</summary>
    public bool IsVisible { get; set; }

    public ContextMenuModel(float x = 0f, float y = 0f, IReadOnlyList<ContextMenuItem>? items = null)
    {
        X = x;
        Y = y;
        Items = items ?? Array.Empty<ContextMenuItem>();
        HoveredIndex = -1;
        IsVisible = items != null && items.Count > 0;
    }

    /// <summary>
    /// Opens the context menu at the specified position.
    /// </summary>
    public void Open(float x, float y, IReadOnlyList<ContextMenuItem> items)
    {
        X = x;
        Y = y;
        Items = items ?? Array.Empty<ContextMenuItem>();
        HoveredIndex = -1;
        IsVisible = true;
    }

    /// <summary>
    /// Closes and hides the context menu.
    /// </summary>
    public void Close()
    {
        IsVisible = false;
        HoveredIndex = -1;
    }

    /// <summary>
    /// Triggers the action of the currently hovered item if it is enabled.
    /// Returns true if an action was executed.
    /// </summary>
    public bool ExecuteHovered()
    {
        if (!IsVisible || HoveredIndex < 0 || HoveredIndex >= Items.Count)
        {
            return false;
        }

        var item = Items[HoveredIndex];
        if (item.IsSeparator || item.IsDisabled)
        {
            return false;
        }

        item.Action?.Invoke();
        Close();
        return true;
    }
}
