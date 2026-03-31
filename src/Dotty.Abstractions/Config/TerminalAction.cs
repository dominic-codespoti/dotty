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

    /// <summary>Rename current tab.</summary>
    RenameTab,

    /// <summary>Toggle terminal visibility (minimize/restore).</summary>
    ToggleVisibility,

    /// <summary>Increase scrollback buffer size.</summary>
    IncreaseScrollback,

    /// <summary>Decrease scrollback buffer size.</summary>
    DecreaseScrollback,

    /// <summary>Send a custom escape sequence to the terminal.</summary>
    SendEscapeSequence,

    /// <summary>Quit the application.</summary>
    Quit,
}
