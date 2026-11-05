using System;

namespace Dotty.Abstractions.Clipboard;

/// <summary>OSC 52 selections; SELECT resolves through host policy, and CUT_BUFFER0 requires a real X11 backend.</summary>
[Flags]
public enum ClipboardSelection : byte
{
    None = 0,
    Clipboard = 1,
    Primary = 2,
    Select = 4,
    CutBuffer0 = 8,
}

public readonly record struct ClipboardWriteRequest(ClipboardSelection Selection, string Text);

public readonly record struct ClipboardReadRequest(ClipboardSelection Selection, string ReplySelector = "");



public static class Osc52Selectors
{
    /// <summary>Parses OSC 52 selector values. Empty means xterm s0 (configured SELECT plus X11 cut buffer zero).</summary>
    public static ClipboardSelection Parse(ReadOnlySpan<char> pc)
    {
        if (pc.IsEmpty)
            return ClipboardSelection.Select | ClipboardSelection.CutBuffer0;
        var selection = ClipboardSelection.None;
        foreach (char c in pc)
        {
            switch (c)
            {
                case 'c':
                    selection |= ClipboardSelection.Clipboard;
                    break;
                case 'p':
                    selection |= ClipboardSelection.Primary;
                    break;
                case 's':
                    selection |= ClipboardSelection.Select;
                    break;
                case '0':
                    selection |= ClipboardSelection.CutBuffer0;
                    break;
            }
        }
        return selection;
    }
}
