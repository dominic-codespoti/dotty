namespace Dotty.Abstractions.Config;

/// <summary>
/// Terminal actions that can be bound to keys.
/// </summary>
public enum TerminalAction
{
    /// <summary>No action.</summary>
    None,

    /// <summary>Create a new tab.</summary>
    NewTab,

    /// <summary>Close the current tab.</summary>
    CloseTab,

    /// <summary>Go to the next tab.</summary>
    NextTab,

    /// <summary>Go to the previous tab.</summary>
    PreviousTab,

    /// <summary>Switch to tab by index (1-9).</summary>
    SwitchTab1,
    SwitchTab2,
    SwitchTab3,
    SwitchTab4,
    SwitchTab5,
    SwitchTab6,
    SwitchTab7,
    SwitchTab8,
    SwitchTab9,

    /// <summary>Copy selected text to clipboard.</summary>
    Copy,

    /// <summary>Paste from clipboard.</summary>
    Paste,

    /// <summary>Clear the terminal buffer.</summary>
    Clear,

    /// <summary>Toggle fullscreen mode.</summary>
    ToggleFullscreen,

    /// <summary>Zoom in (increase font size).</summary>
    ZoomIn,

    /// <summary>Zoom out (decrease font size).</summary>
    ZoomOut,

    /// <summary>Reset zoom to default font size.</summary>
    ResetZoom,

    /// <summary>Open search/find dialog.</summary>
    Search,

    /// <summary>Duplicate the current tab.</summary>
    DuplicateTab,

    /// <summary>Close all other tabs.</summary>
    CloseOtherTabs,


    /// <summary>Quit the application.</summary>
    Quit,

    /// <summary>Split the active pane vertically (left/right).</summary>
    SplitVertical,

    /// <summary>Split the active pane horizontally (top/bottom).</summary>
    SplitHorizontal,

    /// <summary>Navigate pane focus to the left pane.</summary>
    FocusPaneLeft,

    /// <summary>Navigate pane focus to the right pane.</summary>
    FocusPaneRight,

    /// <summary>Navigate pane focus to the upper pane.</summary>
    FocusPaneUp,

    /// <summary>Navigate pane focus to the lower pane.</summary>
    FocusPaneDown,

    /// <summary>Close the focused pane.</summary>
    ClosePane,

    /// <summary>Navigate to the previous shell prompt.</summary>
    PreviousPrompt,

    /// <summary>Navigate to the next shell prompt.</summary>
    NextPrompt,

    /// <summary>Select and copy the latest command's output.</summary>
    CopyCommandOutput,

    /// <summary>Scroll the active pane's scrollback up by half a page.</summary>
    ScrollPageUp,

    /// <summary>Scroll the active pane's scrollback down by half a page.</summary>
    ScrollPageDown,

    /// <summary>Scroll the active pane's scrollback up one line.</summary>
    ScrollLineUp,

    /// <summary>Scroll the active pane's scrollback down one line.</summary>
    ScrollLineDown,

    /// <summary>Scroll the active pane to the top of its scrollback.</summary>
    ScrollToTop,

    /// <summary>Scroll the active pane back to the live screen.</summary>
    ScrollToBottom,

    /// <summary>Select the active pane's scrollback and screen.</summary>
    SelectAll,

    /// <summary>Discard the active pane's scrollback; the visible screen stays.</summary>
    ClearScrollback,

    /// <summary>Re-read the configuration files.</summary>
    ReloadConfig,

    /// <summary>Paste the primary selection into the active pane.</summary>
    PasteSelection,

    /// <summary>Select the next search match (only while the search overlay is open).</summary>
    SearchNext,

    /// <summary>Select the previous search match (only while the search overlay is open).</summary>
    SearchPrevious,

    /// <summary>Copy the selection when there is one; otherwise the key goes to the application.</summary>
    CopyOrSend,

    /// <summary>Move the active tab one place to the left.</summary>
    MoveTabLeft,

    /// <summary>Move the active tab one place to the right.</summary>
    MoveTabRight,

    /// <summary>Show or hide the tab bar.</summary>
    ToggleTabBar,

    /// <summary>Write the binding's text argument to the active pane (<c>send_text:&lt;escaped&gt;</c>).</summary>
    SendText,
}
