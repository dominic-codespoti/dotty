using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dotty.Abstractions.Config;

/// <summary>A bound action plus its argument (only <see cref="TerminalAction.SendText"/> takes one).</summary>
public readonly record struct BoundAction(TerminalAction Action, string? Argument = null)
{
    public static implicit operator BoundAction(TerminalAction action) => new(action);

    public override string ToString() => ActionRegistry.Format(this);
}

/// <summary>Stable configuration name and description of one <see cref="TerminalAction"/>.</summary>
public readonly record struct ActionInfo(TerminalAction Action, string Name, string Description, bool TakesArgument = false);

/// <summary>
/// The table of bindable actions: stable snake_case names (the enum name is accepted too), descriptions for
/// the docs and <c>+explain-config</c>, and the parser for <c>name[:argument]</c> values.
/// </summary>
public static class ActionRegistry
{
    private static readonly ActionInfo[] s_all =
    [
        new(TerminalAction.None, "none", "Unbind the chord; never dispatched."),
        new(TerminalAction.NewTab, "new_tab", "Create a new tab."),
        new(TerminalAction.CloseTab, "close_tab", "Close the current tab."),
        new(TerminalAction.NextTab, "next_tab", "Select the next tab."),
        new(TerminalAction.PreviousTab, "previous_tab", "Select the previous tab."),
        new(TerminalAction.SwitchTab1, "switch_tab_1", "Select tab 1."),
        new(TerminalAction.SwitchTab2, "switch_tab_2", "Select tab 2."),
        new(TerminalAction.SwitchTab3, "switch_tab_3", "Select tab 3."),
        new(TerminalAction.SwitchTab4, "switch_tab_4", "Select tab 4."),
        new(TerminalAction.SwitchTab5, "switch_tab_5", "Select tab 5."),
        new(TerminalAction.SwitchTab6, "switch_tab_6", "Select tab 6."),
        new(TerminalAction.SwitchTab7, "switch_tab_7", "Select tab 7."),
        new(TerminalAction.SwitchTab8, "switch_tab_8", "Select tab 8."),
        new(TerminalAction.SwitchTab9, "switch_tab_9", "Select tab 9."),
        new(TerminalAction.Copy, "copy", "Copy the current selection."),
        new(TerminalAction.Paste, "paste", "Paste the clipboard."),
        new(TerminalAction.Clear, "clear", "Send Ctrl+L (form feed) so the shell clears the screen."),
        new(TerminalAction.ToggleFullscreen, "toggle_fullscreen", "Toggle fullscreen mode."),
        new(TerminalAction.ZoomIn, "zoom_in", "Increase the font size."),
        new(TerminalAction.ZoomOut, "zoom_out", "Decrease the font size."),
        new(TerminalAction.ResetZoom, "reset_zoom", "Restore the configured font size."),
        new(TerminalAction.Search, "search", "Toggle the search overlay."),
        new(TerminalAction.DuplicateTab, "duplicate_tab", "Duplicate the current tab."),
        new(TerminalAction.CloseOtherTabs, "close_other_tabs", "Close every other tab."),
        new(TerminalAction.Quit, "quit", "Quit the application."),
        new(TerminalAction.SplitVertical, "split_vertical", "Split the active pane left/right."),
        new(TerminalAction.SplitHorizontal, "split_horizontal", "Split the active pane top/bottom."),
        new(TerminalAction.FocusPaneLeft, "focus_pane_left", "Focus the pane to the left."),
        new(TerminalAction.FocusPaneRight, "focus_pane_right", "Focus the pane to the right."),
        new(TerminalAction.FocusPaneUp, "focus_pane_up", "Focus the pane above."),
        new(TerminalAction.FocusPaneDown, "focus_pane_down", "Focus the pane below."),
        new(TerminalAction.ClosePane, "close_pane", "Close the focused pane; if it is the tab's only pane, close the tab."),
        new(TerminalAction.PreviousPrompt, "previous_prompt", "Scroll to the previous shell prompt (shell integration)."),
        new(TerminalAction.NextPrompt, "next_prompt", "Scroll to the next shell prompt (shell integration)."),
        new(TerminalAction.CopyCommandOutput, "copy_command_output", "Select and copy the latest command's output (shell integration)."),
        new(TerminalAction.ScrollPageUp, "scroll_page_up", "Scroll the scrollback up by half the pane height."),
        new(TerminalAction.ScrollPageDown, "scroll_page_down", "Scroll the scrollback down by half the pane height."),
        new(TerminalAction.ScrollLineUp, "scroll_line_up", "Scroll the scrollback up one line."),
        new(TerminalAction.ScrollLineDown, "scroll_line_down", "Scroll the scrollback down one line."),
        new(TerminalAction.ScrollToTop, "scroll_to_top", "Scroll to the oldest scrollback line."),
        new(TerminalAction.ScrollToBottom, "scroll_to_bottom", "Return to the live screen."),
        new(TerminalAction.SelectAll, "select_all", "Select the active pane's scrollback and screen."),
        new(TerminalAction.ClearScrollback, "clear_scrollback", "Discard the active pane's scrollback; the screen stays."),
        new(TerminalAction.ReloadConfig, "reload_config", "Re-read config.json and the Lua config."),
        new(TerminalAction.PasteSelection, "paste_selection", "Paste the primary selection."),
        new(TerminalAction.SearchNext, "search_next", "Select the next search match while the overlay is open."),
        new(TerminalAction.SearchPrevious, "search_previous", "Select the previous search match while the overlay is open."),
        new(TerminalAction.CopyOrSend, "copy_or_send", "Copy the selection; without one the key goes to the application."),
        new(TerminalAction.MoveTabLeft, "move_tab_left", "Move the active tab one place to the left."),
        new(TerminalAction.MoveTabRight, "move_tab_right", "Move the active tab one place to the right."),
        new(TerminalAction.ToggleTabBar, "toggle_tab_bar", "Show or hide the tab bar."),
        new(TerminalAction.SendText, "send_text", "Write the escaped text after the colon to the active pane (send_text:\\x1bOA).", TakesArgument: true),
    ];

    /// <summary>Every action, in enum order.</summary>
    public static IReadOnlyList<ActionInfo> All => s_all;

    public static ActionInfo Get(TerminalAction action)
    {
        foreach (var info in s_all)
            if (info.Action == action)
                return info;
        throw new ArgumentOutOfRangeException(nameof(action), action, "Action has no registry row.");
    }

    public static string GetName(TerminalAction action) => Get(action).Name;

    /// <summary>Resolves a snake_case name or an enum name (case-insensitive) without an argument.</summary>
    public static bool TryGetAction(ReadOnlySpan<char> name, out TerminalAction action)
    {
        name = name.Trim();
        foreach (var info in s_all)
        {
            if (name.Equals(info.Name, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(Enum.GetName(info.Action), StringComparison.OrdinalIgnoreCase))
            {
                action = info.Action;
                return true;
            }
        }
        action = TerminalAction.None;
        return false;
    }

    /// <summary>
    /// Parses <c>name</c> or <c>name:argument</c>. Only actions that take an argument accept (and require) one;
    /// the argument of <c>send_text</c> is unescaped strictly (see <see cref="TryUnescape"/>).
    /// </summary>
    public static bool TryParse(string? value, out BoundAction action, out string? error)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "empty action";
            return false;
        }
        int colon = value.IndexOf(':');
        ReadOnlySpan<char> name = colon < 0 ? value.AsSpan() : value.AsSpan(0, colon);
        if (!TryGetAction(name, out var parsed))
        {
            error = $"unknown action '{name.Trim().ToString()}'";
            return false;
        }
        var info = Get(parsed);
        if (!info.TakesArgument)
        {
            if (colon >= 0)
            {
                error = $"action '{info.Name}' takes no argument";
                return false;
            }
            action = new BoundAction(parsed);
            error = null;
            return true;
        }
        if (colon < 0 || colon == value.Length - 1)
        {
            error = $"action '{info.Name}' needs an argument ('{info.Name}:<text>')";
            return false;
        }
        if (!TryUnescape(value.AsSpan(colon + 1), out string text, out error))
            return false;
        action = new BoundAction(parsed, text);
        return true;
    }

    /// <summary>Canonical text of a bound action; <see cref="TryParse"/> reads it back unchanged.</summary>
    public static string Format(BoundAction action)
    {
        string name = GetName(action.Action);
        return action.Argument is null ? name : name + ":" + Escape(action.Argument);
    }

    /// <summary>
    /// Unescapes <c>\\ \n \r \t \e \a \b \f \v \0 \xHH \u{H…}</c>; any other backslash sequence, a lone surrogate,
    /// or an empty result is an error.
    /// </summary>
    public static bool TryUnescape(ReadOnlySpan<char> text, out string result, out string? error)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[++i]);
                    continue;
                }
                return Fail("lone surrogate in text", out result, out error);
            }
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }
            if (++i >= text.Length)
                return Fail("text ends with a lone backslash", out result, out error);
            switch (text[i])
            {
                case '\\': builder.Append('\\'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'e': builder.Append('\x1b'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;
                case '0': builder.Append('\0'); break;
                case 'x':
                    if (text.Length - i - 1 < 2
                        || !byte.TryParse(text.Slice(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
                        return Fail("\\x needs two hex digits", out result, out error);
                    builder.Append((char)b);
                    i += 2;
                    break;
                case 'u':
                {
                    if (i + 1 >= text.Length || text[i + 1] != '{')
                        return Fail("\\u needs braces: \\u{1F600}", out result, out error);
                    int close = text[(i + 2)..].IndexOf('}');
                    if (close < 1 || close > 6
                        || !int.TryParse(text.Slice(i + 2, close), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int scalar)
                        || !Rune.IsValid(scalar))
                        return Fail("\\u{…} needs 1-6 hex digits naming a Unicode scalar value", out result, out error);
                    builder.Append(new Rune(scalar).ToString());
                    i += 2 + close;
                    break;
                }
                default:
                    return Fail($"unknown escape '\\{text[i]}'", out result, out error);
            }
        }
        if (builder.Length == 0)
            return Fail("text is empty", out result, out error);
        result = builder.ToString();
        error = null;
        return true;
    }

    /// <summary>Escapes control characters and backslashes so <see cref="TryUnescape"/> restores <paramref name="text"/>.</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '\\') builder.Append("\\\\");
            else if (c < 0x20 || c == 0x7f) builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else if (c is >= '\u0080' and <= '\u009f') builder.Append("\\u{").Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append('}');
            else builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool Fail(string message, out string result, out string? error)
    {
        result = string.Empty;
        error = message;
        return false;
    }
}
