using System;
using System.Collections.Generic;
using System.Globalization;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Prompt;

namespace Dotty.Silk;

/// <summary>
/// Builds the host-owned permission prompt for an OSC 52 clipboard read on top of <see cref="HostPrompt"/>.
/// The clipboard text and the requesting tab's title both come from untrusted sources, so every row goes
/// through <see cref="HostPrompt.SanitizeLine"/>: control characters are shown as visible escapes and the
/// preview is a bounded, wrapped excerpt with the full character count.
/// </summary>
internal static class ClipboardReadPromptBuilder
{
    /// <summary>Index of the Deny action; it starts focused so Enter never discloses the clipboard.</summary>
    internal const int DenyActionIndex = 0;

    /// <summary>Index of the Allow action.</summary>
    internal const int AllowActionIndex = 1;

    /// <summary>Tab title characters shown in the title row; the rest is truncated so the question stays visible.</summary>
    internal const int MaxTabTitleChars = 24;

    /// <summary>Preview rows; one body row is reserved for the character count.</summary>
    internal const int MaxPreviewRows = HostPrompt.MaxLines - 1;

    /// <summary>Clipboard characters examined for the preview.</summary>
    internal const int MaxPreviewSourceChars = MaxPreviewRows * HostPrompt.MaxLineChars;

    /// <summary>
    /// Builds the prompt for a read request from the tab titled <paramref name="tabTitle"/>. <paramref name="allow"/>
    /// runs only from the Allow row and is expected to send the exact snapshot previewed in
    /// <paramref name="clipboardText"/>; Deny, Escape, click-outside and replacement by another menu send nothing.
    /// </summary>
    public static ContextMenuModel Build(string? tabTitle, string clipboardText, Action allow)
    {
        ArgumentNullException.ThrowIfNull(clipboardText);
        ArgumentNullException.ThrowIfNull(allow);

        return HostPrompt.Build(
            BuildTitle(tabTitle),
            BuildPreviewLines(clipboardText),
            new (string Label, Action Run)[]
            {
                ("Deny", static () => { }),
                ("Allow", allow),
            },
            DenyActionIndex,
            onDismissed: null);
    }

    /// <summary>Title row: the (truncated, sanitized) tab title followed by the fixed question.</summary>
    internal static string BuildTitle(string? tabTitle)
    {
        string name = HostPrompt.SanitizeLine(tabTitle, MaxTabTitleChars);
        return name.Length == 0
            ? "A tab wants to read the clipboard"
            : "Tab \"" + name + "\" wants to read the clipboard";
    }

    /// <summary>
    /// Sanitized preview: at most <see cref="MaxPreviewRows"/> rows of at most <see cref="HostPrompt.MaxLineChars"/>
    /// characters (logical lines are kept, long lines wrap, an ellipsis marks cut-off text), followed by the
    /// total character count. An empty clipboard is described instead of previewed.
    /// </summary>
    internal static List<string> BuildPreviewLines(string text)
    {
        var lines = new List<string>(HostPrompt.MaxLines);
        if (text.Length == 0)
        {
            lines.Add("(clipboard is empty)");
            return lines;
        }

        AppendPreviewRows(lines, text);
        lines.Add(text.Length == 1
            ? "(1 character)"
            : "(" + text.Length.ToString(CultureInfo.InvariantCulture) + " characters)");
        return lines;
    }

    /// <summary>
    /// Places <paramref name="prompt"/> in the middle of a viewport of the given pixel size, using the same
    /// layout metrics the renderer and hit tester use (<paramref name="cellWidth"/> and
    /// <paramref name="cellHeight"/> already scaled to framebuffer pixels).
    /// </summary>
    internal static void Center(ContextMenuModel prompt, float viewportWidth, float viewportHeight, float cellWidth, float cellHeight)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        prompt.X = 0f;
        prompt.Y = 0f;
        ContextMenuLayout layout = ContextMenuLayout.Calculate(prompt, viewportWidth, viewportHeight, cellWidth, cellHeight);
        prompt.X = Math.Max(0f, (viewportWidth - layout.Width) / 2f);
        prompt.Y = Math.Max(0f, (viewportHeight - layout.Height) / 2f);
    }

    private static void AppendPreviewRows(List<string> lines, string text)
    {
        int width = HostPrompt.MaxLineChars;
        int limit = Math.Min(text.Length, MaxPreviewSourceChars);
        if (limit < text.Length && char.IsHighSurrogate(text[limit - 1]))
            limit--;

        int rows = 0;
        int pos = 0;
        bool cutOff = limit < text.Length;
        while (pos < limit)
        {
            if (rows >= MaxPreviewRows)
            {
                cutOff = true;
                break;
            }

            int eol = text.AsSpan(pos, limit - pos).IndexOfAny('\n', '\r');
            int end = eol < 0 ? limit : pos + eol;
            string sanitized = HostPrompt.SanitizeLine(text.Substring(pos, end - pos), int.MaxValue);
            pos = end;
            if (pos < limit)
                pos += text[pos] == '\r' && pos + 1 < limit && text[pos + 1] == '\n' ? 2 : 1;

            if (sanitized.Length == 0)
            {
                lines.Add(string.Empty);
                rows++;
                continue;
            }

            int offset = 0;
            while (offset < sanitized.Length && rows < MaxPreviewRows)
            {
                int take = Math.Min(width, sanitized.Length - offset);
                // Sanitized text only contains well-formed surrogate pairs; never split one across rows.
                if (offset + take < sanitized.Length && char.IsHighSurrogate(sanitized[offset + take - 1]))
                    take--;
                lines.Add(sanitized.Substring(offset, take));
                rows++;
                offset += take;
            }

            if (offset < sanitized.Length)
            {
                cutOff = true;
                break;
            }
        }

        if (cutOff && lines.Count > 0)
            lines[lines.Count - 1] = WithEllipsis(lines[lines.Count - 1], width);
    }

    private static string WithEllipsis(string row, int width)
    {
        if (row.Length >= width)
            row = row.Substring(0, width - 1);
        if (row.Length > 0 && char.IsHighSurrogate(row[row.Length - 1]))
            row = row.Substring(0, row.Length - 1);
        return row + "\u2026";
    }
}
