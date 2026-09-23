using System;

namespace Dotty.Runtime.Tabs;

/// <summary>
/// Supplies the display title for each tab without allocating per frame.
/// Implementations return spans over storage that stays valid until the next UI-thread update.
/// </summary>
public interface ITabTitleSource
{
    /// <summary>Returns the title to draw for <paramref name="tab"/> at <paramref name="index"/>.</summary>
    ReadOnlySpan<char> GetTitle(TerminalTab tab, int index);
}
