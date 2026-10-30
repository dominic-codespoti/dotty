using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// The capabilities <c>terminfo/xterm-dotty.src</c> adds on top of <c>xterm-256color</c>. Each one is implemented and
/// covered by a test that runs it through Dotty (<c>TerminfoCapabilityTests</c>), and the same table answers XTGETTCAP,
/// so the compiled entry, the query replies and the implementation cannot drift apart.
/// </summary>
public static class TerminfoCapabilities
{
    /// <summary>A capability name and its terminfo string value with escapes as real characters; null for a boolean.</summary>
    public readonly record struct Capability(string Name, string? Value);

    /// <summary>Every capability the entry declares besides <c>use=xterm-256color</c>.</summary>
    public static ReadOnlySpan<Capability> Implemented => Table;

    private static readonly Capability[] Table =
    [
        // Direct (24-bit) colour through SGR 38;2 / 48;2 (tmux and others read this extension).
        new("Tc", null),
        // DECSCUSR cursor style, and its reset to the steady block.
        new("Ss", "\u001b[%p1%d q"),
        new("Se", "\u001b[2 q"),
        // Synchronized output (mode 2026): parameter 1 begins, anything else ends.
        new("Sync", "\u001b[?2026%?%p1%{1}%=%th%el%;"),
        // Bracketed paste: enable/disable (mode 2004) and the markers Dotty wraps pasted text in.
        new("BE", "\u001b[?2004h"),
        new("BD", "\u001b[?2004l"),
        new("PS", "\u001b[200~"),
        new("PE", "\u001b[201~"),
    ];

    /// <summary>The string value of an implemented capability; false for unknown names and booleans.</summary>
    public static bool TryGetString(scoped ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
    {
        foreach (Capability capability in Table)
        {
            if (capability.Value is not null && name.SequenceEqual(capability.Name))
            {
                value = capability.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
