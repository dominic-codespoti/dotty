using System;

namespace Dotty.Runtime.Clipboard;

/// <summary>Permission policy for OSC 52 clipboard read (<c>?</c>) requests.</summary>
public enum ClipboardReadMode : byte
{
    /// <summary>Requests are ignored without a reply (default; fails closed).</summary>
    Deny = 0,

    /// <summary>The user is shown the clipboard contents and must confirm before they are sent.</summary>
    Ask = 1,

    /// <summary>Requests are answered immediately. Any program that can write to the terminal can read the clipboard.</summary>
    Allow = 2,
}

/// <summary>Parsing for <see cref="ClipboardReadMode"/> configuration values.</summary>
public static class ClipboardReadModes
{
    /// <summary>
    /// Parses <c>deny</c>, <c>ask</c> or <c>allow</c> (case-insensitive, surrounding whitespace ignored).
    /// Null, empty and every unknown string yield <see cref="ClipboardReadMode.Deny"/>.
    /// </summary>
    public static ClipboardReadMode Parse(string? value)
    {
        if (value is null)
            return ClipboardReadMode.Deny;

        ReadOnlySpan<char> text = value.AsSpan().Trim();
        if (text.Equals("ask", StringComparison.OrdinalIgnoreCase))
            return ClipboardReadMode.Ask;
        if (text.Equals("allow", StringComparison.OrdinalIgnoreCase))
            return ClipboardReadMode.Allow;
        return ClipboardReadMode.Deny;
    }
}
