using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Runtime.ContextMenu;

namespace Dotty.Runtime.Prompt;

/// <summary>
/// Builds host-owned confirmation / notice prompts on top of <see cref="ContextMenuModel"/>.
/// The host centers the returned model and assigns it to the active context menu slot; Escape,
/// click-outside and replacement by another menu all fire <see cref="ContextMenuModel.Dismissed"/>.
/// </summary>
/// <remarks>
/// Untrusted text (clipboard previews, URLs, titles) MUST pass through <see cref="SanitizeLine"/>
/// before it reaches a prompt. <see cref="Build"/> and <see cref="BuildNotice"/> enforce this and the
/// size limits themselves: at most <see cref="MaxLines"/> body lines of at most
/// <see cref="MaxLineChars"/> chars each (<see cref="ContextMenuLayout"/> does not clamp width).
/// </remarks>
public static class HostPrompt
{
    /// <summary>Maximum characters per prompt row (title, preview line, action label).</summary>
    public const int MaxLineChars = 60;

    /// <summary>Maximum number of preview/body lines; extra lines are dropped.</summary>
    public const int MaxLines = 8;

    /// <summary>Id given to the disabled title row.</summary>
    public const string TitleId = "prompt-title";

    /// <summary>Id prefix for disabled preview/body rows (followed by the line index).</summary>
    public const string LineIdPrefix = "prompt-line-";

    /// <summary>Id prefix for action rows (followed by the action index).</summary>
    public const string ActionIdPrefix = "prompt-action-";

    /// <summary>Id of the single action row created by <see cref="BuildNotice"/>.</summary>
    public const string DismissId = "prompt-dismiss";

    /// <summary>
    /// Builds a confirmation prompt: title, separator, preview lines, separator, then one enabled row per action.
    /// Focus starts on <c>actions[initialFocus]</c> (clamped); callers should point it at the safe (deny) row.
    /// X/Y are left at 0; the caller positions the model.
    /// </summary>
    /// <param name="onDismissed">Runs once if the prompt closes without an action (Esc, click outside, replaced).</param>
    public static ContextMenuModel Build(
        string title,
        IReadOnlyList<string> previewLines,
        IReadOnlyList<(string Label, Action Run)> actions,
        int initialFocus,
        Action? onDismissed)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
            throw new ArgumentException("A prompt needs at least one action.", nameof(actions));

        var items = new List<ContextMenuItem>((previewLines?.Count ?? 0) + actions.Count + 3);
        AddHeader(items, title, previewLines);
        items.Add(ContextMenuItem.Separator("prompt-sep-actions"));

        int focus = Math.Clamp(initialFocus, 0, actions.Count - 1);
        int focusRow = -1;
        for (int i = 0; i < actions.Count; i++)
        {
            if (i == focus)
                focusRow = items.Count;
            var run = actions[i].Run;
            items.Add(new ContextMenuItem(
                ActionIdPrefix + i,
                SanitizeLine(actions[i].Label, MaxLineChars),
                action: run ?? (static () => { })));
        }

        return Finish(items, focusRow, onDismissed);
    }

    /// <summary>Builds an informational prompt: title, body lines and a single focused "Dismiss" row.</summary>
    public static ContextMenuModel BuildNotice(string title, IReadOnlyList<string> lines, Action? onDismissed)
    {
        var items = new List<ContextMenuItem>((lines?.Count ?? 0) + 4);
        AddHeader(items, title, lines);
        items.Add(ContextMenuItem.Separator("prompt-sep-actions"));
        int focusRow = items.Count;
        items.Add(new ContextMenuItem(DismissId, "Dismiss", action: static () => { }));
        return Finish(items, focusRow, onDismissed);
    }

    /// <summary>
    /// Makes untrusted text safe to draw on a single prompt row: C0 controls, DEL and C1 controls become
    /// visible <c>\xNN</c> escapes (TAB/LF/CR/VT/FF collapse to one space), bidi override/isolate marks and
    /// lone surrogates become <c>\u{XXXX}</c>, and the result is truncated to <paramref name="maxChars"/>
    /// chars with a trailing ellipsis. Never returns null; the result never contains a control character.
    /// </summary>
    public static string SanitizeLine(string? s, int maxChars)
    {
        if (maxChars <= 0 || string.IsNullOrEmpty(s))
            return string.Empty;

        var sb = new StringBuilder(Math.Min(s.Length, maxChars) + 8);
        bool truncated = false;
        bool lastWasBreak = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            string? replacement = null;
            string? literal = null;
            if (c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f')
            {
                if (lastWasBreak)
                    continue;
                literal = " ";
                lastWasBreak = true;
            }
            else
            {
                lastWasBreak = false;
                if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
                    replacement = "\\x" + ((int)c).ToString("X2");
                else if (IsBidiControl(c) || c == '\u2028' || c == '\u2029')
                    replacement = "\\u{" + ((int)c).ToString("X4") + "}";
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        if (sb.Length + 2 > maxChars) { truncated = true; break; }
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                        continue;
                    }
                    replacement = "\\u{" + ((int)c).ToString("X4") + "}";
                }
                else if (char.IsLowSurrogate(c))
                    replacement = "\\u{" + ((int)c).ToString("X4") + "}";
            }

            string text = replacement ?? literal ?? c.ToString();
            if (sb.Length + text.Length > maxChars)
            {
                truncated = true;
                break;
            }
            sb.Append(text);
        }

        if (!truncated)
            return sb.ToString();

        // Reserve one char for the ellipsis; never split an escape or a surrogate pair.
        string result = sb.ToString();
        if (result.Length > maxChars - 1)
            result = result.Substring(0, maxChars - 1);
        result = TrimPartialTail(result);
        return result + "\u2026";
    }

    private static void AddHeader(List<ContextMenuItem> items, string? title, IReadOnlyList<string>? lines)
    {
        items.Add(new ContextMenuItem(TitleId, SanitizeLine(title, MaxLineChars), isDisabled: true));
        items.Add(ContextMenuItem.Separator("prompt-sep-title"));
        if (lines == null)
            return;
        int count = Math.Min(lines.Count, MaxLines);
        for (int i = 0; i < count; i++)
            items.Add(new ContextMenuItem(LineIdPrefix + i, SanitizeLine(lines[i], MaxLineChars), isDisabled: true));
    }

    private static ContextMenuModel Finish(List<ContextMenuItem> items, int focusRow, Action? onDismissed)
    {
        var model = new ContextMenuModel(0f, 0f, items)
        {
            HoveredIndex = focusRow,
            IsVisible = true,
            Dismissed = onDismissed,
        };
        return model;
    }

    private static bool IsBidiControl(char c) =>
        c == '\u061C' || c == '\u200E' || c == '\u200F' ||
        (c >= '\u202A' && c <= '\u202E') ||
        (c >= '\u2066' && c <= '\u2069');

    private static string TrimPartialTail(string s)
    {
        // Drop a dangling high surrogate or an incomplete "\x"/"\u{" escape cut by truncation.
        if (s.Length > 0 && char.IsHighSurrogate(s[^1]))
            return s.Substring(0, s.Length - 1);
        int bs = s.LastIndexOf('\\');
        if (bs >= 0)
        {
            string tail = s.Substring(bs);
            bool completeX = tail.Length == 4 && tail[1] == 'x';
            bool completeU = tail.Length >= 3 && tail[1] == 'u' && tail[^1] == '}';
            bool literalBackslash = tail.Length == 1 || (tail[1] != 'x' && tail[1] != 'u');
            if (!completeX && !completeU && !literalBackslash)
                return s.Substring(0, bs);
        }
        return s;
    }
}
