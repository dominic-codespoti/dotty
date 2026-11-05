using System;
using Dotty.Runtime.Config;

namespace Dotty.Runtime.Clipboard;

/// <summary>Copy-on-select decision and application. Never clears the selection.</summary>
public static class CopyOnSelect
{
    public static bool CopiesToPrimary(CopyOnSelectMode mode) => mode is CopyOnSelectMode.Primary or CopyOnSelectMode.Both;

    public static bool CopiesToClipboard(CopyOnSelectMode mode) => mode is CopyOnSelectMode.Clipboard or CopyOnSelectMode.Both;

    /// <summary>Writes <paramref name="text"/> to the targets selected by <paramref name="mode"/>; empty text writes nothing.</summary>
    public static void Apply(CopyOnSelectMode mode, string? text, IPrimarySelection? primary, ITerminalClipboard? clipboard)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (CopiesToPrimary(mode))
            primary?.Set(text);
        if (CopiesToClipboard(mode))
            clipboard?.SetText(text);
    }
}
