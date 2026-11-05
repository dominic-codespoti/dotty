using System;
using System.Collections.Generic;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Prompt;
using Dotty.Runtime.Localization;

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
            reasonLines.Add(UiStrings.PasteReasonMultiline);
        if ((reasons & PasteRisk.ControlChars) != 0)
            reasonLines.Add(UiStrings.PasteReasonControl);
        if ((reasons & PasteRisk.EndMarker) != 0)
            reasonLines.Add(UiStrings.PasteReasonEndMarker);

        // BuildPreview may append a "... N more lines" row; reserve it so reasons are never dropped.
        int previewLines = Math.Clamp(HostPrompt.MaxLines - 1 - reasonLines.Count, 1, MaxPreviewLines);
        string[] preview = PasteSafety.BuildPreview(text, previewLines, HostPrompt.MaxLineChars);

        var lines = new List<string>(HostPrompt.MaxLines);
        lines.AddRange(preview);
        lines.AddRange(reasonLines);

        int lineCount = PasteSafety.CountLines(text);
        string title = UiStrings.PasteTitle(lineCount, text.Length);

        return HostPrompt.Build(
            title,
            lines,
            new (string Label, Action Run)[]
            {
                (UiStrings.Paste, paste),
                (UiStrings.Cancel, static () => { }),
            },
            CancelActionIndex,
            onDismissed: null);
    }
}
