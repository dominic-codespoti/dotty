using System;
using System.Collections.Generic;
using System.Globalization;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Prompt;

namespace Dotty.Silk;

/// <summary>
/// Builds the host-owned paste confirmation prompt on top of <see cref="HostPrompt"/>. Clipboard text is only
/// ever shown through <see cref="PasteSafety.BuildPreview"/> (controls rendered as visible glyphs) and
/// <see cref="HostPrompt"/> sanitizes every row again.
/// </summary>
internal static class PastePromptBuilder
{
    /// <summary>Index of the Cancel action in <see cref="Build"/>; it starts focused.</summary>
    internal const int CancelActionIndex = 1;

    /// <summary>Upper bound on preview lines; reason rows and the "more lines" note must still fit <see cref="HostPrompt.MaxLines"/>.</summary>
    internal const int MaxPreviewLines = 6;

    /// <summary>
    /// Confirmation prompt for <paramref name="text"/>: a title with line/character counts, a sanitized preview,
    /// one row per reason in <paramref name="reasons"/>, then Paste and Cancel (focused). Dismissal pastes nothing.
    /// </summary>
    public static ContextMenuModel Build(string text, PasteRisk reasons, Action paste)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(paste);

        var reasonLines = new List<string>(3);
        if ((reasons & PasteRisk.Newlines) != 0)
            reasonLines.Add("Multiple lines: the shell would run them immediately.");
        if ((reasons & PasteRisk.ControlChars) != 0)
            reasonLines.Add("Control characters will be replaced by spaces.");
        if ((reasons & PasteRisk.EndMarker) != 0)
            reasonLines.Add("Contains a paste-end marker (possible injection).");

        // BuildPreview may append a "... N more lines" row; reserve it so reasons are never dropped.
        int previewLines = Math.Clamp(HostPrompt.MaxLines - 1 - reasonLines.Count, 1, MaxPreviewLines);
        string[] preview = PasteSafety.BuildPreview(text, previewLines, HostPrompt.MaxLineChars);

        var lines = new List<string>(HostPrompt.MaxLines);
        lines.AddRange(preview);
        lines.AddRange(reasonLines);

        int lineCount = PasteSafety.CountLines(text);
        string title = string.Create(
            CultureInfo.InvariantCulture,
            $"Paste {lineCount:N0} {(lineCount == 1 ? "line" : "lines")}, {text.Length:N0} characters?");

        return HostPrompt.Build(
            title,
            lines,
            new (string Label, Action Run)[]
            {
                ("Paste", paste),
                ("Cancel", static () => { }),
            },
            CancelActionIndex,
            onDismissed: null);
    }
}
