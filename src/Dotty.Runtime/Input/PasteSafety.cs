using System;
using System.Text;
using Dotty.Runtime.Config;

namespace Dotty.Runtime.Input;

/// <summary>Reasons a paste deserves user confirmation.</summary>
[Flags]
public enum PasteRisk : byte
{
    None = 0,
    Newlines = 1,
    ControlChars = 2,
    EndMarker = 4,
}

/// <summary>Pure classification and preview helpers for pasted text.</summary>
public static class PasteSafety
{
    /// <summary>Classifies text. Newlines: any LF/CR. ControlChars: any neutralised control. EndMarker: ESC[201~ or CSI 201~ (U+009B).</summary>
    public static PasteRisk Evaluate(ReadOnlySpan<char> text)
    {
        PasteRisk risk = PasteRisk.None;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                risk |= PasteRisk.Newlines;
            }
            else if (PasteEncoder.IsNeutralisedControl(c))
            {
                risk |= PasteRisk.ControlChars;
                if ((c == '\x1b' && text.Slice(i + 1).StartsWith("[201~", StringComparison.Ordinal))
                    || (c == '\u009b' && text.Slice(i + 1).StartsWith("201~", StringComparison.Ordinal)))
                    risk |= PasteRisk.EndMarker;
            }
        }

        return risk;
    }

    public static bool IsMultiline(ReadOnlySpan<char> text) => (Evaluate(text) & PasteRisk.Newlines) != 0;

    public static bool HasControl(ReadOnlySpan<char> text) => (Evaluate(text) & PasteRisk.ControlChars) != 0;

    /// <summary>
    /// The subset of <paramref name="risk"/> that should be shown/confirmed. Newlines are harmless when a bracketed
    /// paste is active and the user declared bracketed paste safe; control characters and end markers never are.
    /// </summary>
    public static PasteRisk Reasons(PasteRisk risk, bool bracketed, bool bracketedIsSafe) =>
        bracketed && bracketedIsSafe ? risk & ~PasteRisk.Newlines : risk;

    public static bool NeedsConfirmation(PasteRisk risk, bool bracketed, bool bracketedIsSafe) =>
        Reasons(risk, bracketed, bracketedIsSafe) != PasteRisk.None;

    /// <summary>Full decision: false when <see cref="ClipboardUserConfig.ConfirmPaste"/> is off.</summary>
    public static bool NeedsConfirmation(string? text, bool bracketedActive, ClipboardUserConfig config)
    {
        if (!config.ConfirmPaste || string.IsNullOrEmpty(text))
            return false;
        return NeedsConfirmation(Evaluate(text), bracketedActive, config.BracketedPasteIsSafe);
    }

    /// <summary>Number of logical lines; CRLF counts once and a trailing terminator adds no extra line.</summary>
    public static int CountLines(ReadOnlySpan<char> text)
    {
        int lines = 0;
        int segmentStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                lines++;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                segmentStart = i + 1;
            }
        }

        return segmentStart < text.Length ? lines + 1 : lines;
    }

    /// <summary>
    /// Builds a safe preview: at most <paramref name="maxLines"/> lines of at most <paramref name="maxColumns"/>
    /// characters, controls rendered as visible glyphs, plus a trailing "… N more lines" row when truncated.
    /// </summary>
    public static string[] BuildPreview(string text, int maxLines, int maxColumns)
    {
        if (string.IsNullOrEmpty(text) || maxLines <= 0)
            return Array.Empty<string>();
        maxColumns = Math.Max(1, maxColumns);

        int total = CountLines(text);
        int shown = Math.Min(total, maxLines);
        var result = new string[total > shown ? shown + 1 : shown];

        int index = 0;
        int segmentStart = 0;
        for (int i = 0; i < text.Length && index < shown; i++)
        {
            char c = text[i];
            if (c != '\n' && c != '\r')
                continue;
            result[index++] = BuildLine(text.AsSpan(segmentStart, i - segmentStart), maxColumns);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            segmentStart = i + 1;
        }

        if (index < shown)
            result[index++] = BuildLine(text.AsSpan(segmentStart), maxColumns);

        if (total > shown)
        {
            int more = total - shown;
            string note = "… " + more.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (more == 1 ? " more line" : " more lines");
            result[index] = note.Length > maxColumns ? note.Substring(0, maxColumns) : note;
        }

        return result;
    }

    private static string BuildLine(ReadOnlySpan<char> line, int maxColumns)
    {
        bool truncated = line.Length > maxColumns;
        int take = truncated ? maxColumns - 1 : line.Length;
        if (truncated && take > 0 && char.IsHighSurrogate(line[take - 1]))
            take--;

        var builder = new StringBuilder(take + 1);
        for (int i = 0; i < take; i++)
            builder.Append(Visible(line[i]));
        if (truncated)
            builder.Append('…');
        return builder.ToString();
    }

    private static char Visible(char c)
    {
        if (c == '\t')
            return ' ';
        if (c == 0x7F)
            return '\u2421';
        if (c < 0x20)
            return (char)(0x2400 + c);
        if (c >= '\u0080' && c <= '\u009F')
            return '·';
        return c;
    }
}
