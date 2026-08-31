using System;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// Hit-testing helper for context menu mouse interactions.
/// </summary>
public static class ContextMenuHitTester
{
    /// <summary>
    /// Performs hit testing for a mouse coordinate (x, y) against the context menu layout.
    /// </summary>
    /// <param name="layout">The computed context menu layout.</param>
    /// <param name="x">Mouse horizontal coordinate in window pixels.</param>
    /// <param name="y">Mouse vertical coordinate in window pixels.</param>
    /// <returns>
    /// The 0-based index of the hit item (interactive or separator), or -1 if outside the menu.
    /// </returns>
    public static int HitTest(ContextMenuLayout layout, float x, float y)
    {
        if (layout == null || !layout.Bounds.Contains(x, y))
        {
            return -1;
        }

        var items = layout.Items;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Bounds.Contains(x, y))
            {
                return items[i].Index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Performs hit testing specifically for actionable, non-disabled items.
    /// Returns false if clicking outside, on a separator, or on a disabled item.
    /// </summary>
    public static bool TryHitInteractiveItem(ContextMenuLayout layout, float x, float y, out int itemIndex)
    {
        itemIndex = -1;
        if (layout == null || !layout.Bounds.Contains(x, y))
        {
            return false;
        }

        var items = layout.Items;
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            if (item.Bounds.Contains(x, y))
            {
                if (!item.IsSeparator && !item.IsDisabled)
                {
                    itemIndex = item.Index;
                    return true;
                }
                return false;
            }
        }

        return false;
    }
}
