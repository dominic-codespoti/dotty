using System;

namespace Dotty.Abstractions.Clipboard;

/// <summary>OSC 52 target selections. Only <see cref="Clipboard"/> maps to the system clipboard on all platforms.</summary>
[Flags]
public enum ClipboardSelection : byte
{
    None = 0,
    Clipboard = 1,
    Primary = 2,
}

public readonly record struct ClipboardWriteRequest(ClipboardSelection Selection, string Text);

public readonly record struct ClipboardReadRequest(ClipboardSelection Selection);

public static class Osc52Selectors
{
    /// <summary>
    /// Parses the OSC 52 selector parameter. Empty means clipboard; 'c' is clipboard;
    /// 'p' and 's' are primary; unknown letters are ignored.
    /// </summary>
    public static ClipboardSelection Parse(ReadOnlySpan<char> pc)
    {
        if (pc.IsEmpty)
            return ClipboardSelection.Clipboard;

        var selection = ClipboardSelection.None;
        foreach (char c in pc)
        {
            switch (c)
            {
                case 'c':
                    selection |= ClipboardSelection.Clipboard;
                    break;
                case 'p':
                case 's':
                    selection |= ClipboardSelection.Primary;
                    break;
            }
        }
        return selection;
    }
}
