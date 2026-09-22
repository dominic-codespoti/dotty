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

    /// <summary>Zero-based index of the currently focused or hovered menu item, or -1 if none.</summary>
    public int HoveredIndex { get; set; } = -1;

    /// <summary>Whether the context menu is currently visible and receiving interaction.</summary>
    public bool IsVisible { get; set; }

    public ContextMenuModel(float x = 0f, float y = 0f, IReadOnlyList<ContextMenuItem>? items = null)
    {
        X = x;
        Y = y;
        Items = items ?? Array.Empty<ContextMenuItem>();
        HoveredIndex = -1;
        IsVisible = Items.Count > 0;
    }

    /// <summary>Opens the context menu at the specified position.</summary>
    public void Open(float x, float y, IReadOnlyList<ContextMenuItem> items)
    {
        X = x;
        Y = y;
        Items = items ?? Array.Empty<ContextMenuItem>();
        HoveredIndex = -1;
        IsVisible = Items.Count > 0;
    }

    /// <summary>Closes and hides the context menu.</summary>
    public void Close()
    {
        IsVisible = false;
        HoveredIndex = -1;
    }

    /// <summary>Moves focus to the next or previous enabled actionable row, wrapping at either end.</summary>
    public bool MoveFocus(int delta)
    {
        if (!IsVisible || Items.Count == 0 || delta == 0)
            return false;

        int start = HoveredIndex;
        if (start < 0 || start >= Items.Count)
            start = delta > 0 ? -1 : Items.Count;

        int index = start;
        for (int step = 0; step < Items.Count; step++)
        {
            index = (index + delta) % Items.Count;
            if (index < 0)
                index += Items.Count;
            if (IsActionable(index))
            {
                HoveredIndex = index;
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves focus to the first enabled actionable row.</summary>
    public bool FocusFirst() => FocusBoundary(fromEnd: false);

    /// <summary>Moves focus to the last enabled actionable row.</summary>
    public bool FocusLast() => FocusBoundary(fromEnd: true);

    /// <summary>Triggers the focused item if it is enabled and actionable.</summary>
    public bool ExecuteFocused() => ExecuteHovered();

    /// <summary>
    /// Triggers the action of the currently hovered item if it is enabled.
    /// Returns true if an action was executed.
    /// </summary>
    public bool ExecuteHovered()
    {
        if (!IsVisible || !IsActionable(HoveredIndex))
            return false;

        var item = Items[HoveredIndex];
        item.Action?.Invoke();
        Close();
        return true;
    }

    private bool FocusBoundary(bool fromEnd)
    {
        if (!IsVisible || Items.Count == 0)
            return false;

        if (fromEnd)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (IsActionable(i))
                {
                    HoveredIndex = i;
                    return true;
                }
            }
        }
        else
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (IsActionable(i))
                {
                    HoveredIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsActionable(int index) =>
        index >= 0 && index < Items.Count &&
        !Items[index].IsSeparator &&
        !Items[index].IsDisabled &&
        Items[index].Action != null;
}
