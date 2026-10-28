using System;
using System.Collections.Generic;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Prompt;

namespace Dotty.Silk;

/// <summary>
/// Builds the host-owned prompts for link activation on top of <see cref="HostPrompt"/>. Only the canonical
/// target is ever shown (never the raw OSC 8 text); <see cref="HostPrompt"/> sanitizes every row.
/// </summary>
internal static class HyperlinkPromptBuilder
{
    /// <summary>Index of the Cancel action in <see cref="BuildConfirm"/>; it starts focused.</summary>
    internal const int CancelActionIndex = 2;

    /// <summary>
    /// Confirmation prompt for a risky link: one row per risk, the link text for mismatches, then the canonical
    /// target hard-wrapped over the remaining rows. Focus starts on Cancel; Escape and click-outside also cancel.
    /// </summary>
    public static ContextMenuModel BuildConfirm(
        HyperlinkTarget target,
        HyperlinkRisk risks,
        string? displayText,
        Action open,
        Action copyAddress)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(copyAddress);

        var lines = new List<string>(HostPrompt.MaxLines);
        if ((risks & HyperlinkRisk.NonWebScheme) != 0)
            lines.Add("Non-web link (" + target.Scheme + ")");
        if ((risks & HyperlinkRisk.LocalFile) != 0)
            lines.Add("Opens a local file");
        if ((risks & HyperlinkRisk.Userinfo) != 0)
            lines.Add("URL contains credentials");
        if ((risks & HyperlinkRisk.Idn) != 0)
            lines.Add("Host contains non-ASCII characters");
        if ((risks & HyperlinkRisk.TextMismatch) != 0)
        {
            lines.Add("Link text differs from the target:");
            lines.Add(displayText ?? string.Empty);
        }

        // Keep at least two rows for the target so the host is never dropped.
        int targetRows = Math.Max(2, HostPrompt.MaxLines - lines.Count);
        AppendWrapped(lines, target.Canonical, targetRows);

        return HostPrompt.Build(
            "Open link?",
            lines,
            new (string Label, Action Run)[]
            {
                ("Open", open),
                ("Copy link address", copyAddress),
                ("Cancel", static () => { }),
            },
            CancelActionIndex,
            onDismissed: null);
    }

    /// <summary>Informational prompt with a single Dismiss row.</summary>
    public static ContextMenuModel BuildNotice(string title, string message)
    {
        var lines = new List<string>(HostPrompt.MaxLines);
        AppendWrapped(lines, message, HostPrompt.MaxLines);
        return HostPrompt.BuildNotice(title, lines, onDismissed: null);
    }

    /// <summary>
    /// Appends <paramref name="text"/> split into rows of at most <see cref="HostPrompt.MaxLineChars"/> chars,
    /// at most <paramref name="maxRows"/> rows; overflow is marked with an ellipsis on the last row.
    /// </summary>
    internal static void AppendWrapped(List<string> lines, string? text, int maxRows)
    {
        if (string.IsNullOrEmpty(text) || maxRows <= 0)
            return;

        int width = HostPrompt.MaxLineChars;
        int rows = (text.Length + width - 1) / width;
        if (rows <= maxRows)
        {
            for (int i = 0; i < rows; i++)
                lines.Add(text.Substring(i * width, Math.Min(width, text.Length - i * width)));
            return;
        }

        for (int i = 0; i < maxRows - 1; i++)
            lines.Add(text.Substring(i * width, width));
        lines.Add(text.Substring((maxRows - 1) * width, width - 1) + "\u2026");
    }
}
