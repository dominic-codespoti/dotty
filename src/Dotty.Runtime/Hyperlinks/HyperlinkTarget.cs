namespace Dotty.Runtime.Hyperlinks;

/// <summary>Where a hyperlink came from; the policy treats them differently.</summary>
public enum HyperlinkSource
{
    /// <summary>Found by scanning visible text; the target is what the user can read.</summary>
    Detected,

    /// <summary>OSC 8 hyperlink; the target is chosen by the program and may differ from the text.</summary>
    Explicit,
}

/// <summary>
/// A fully validated, canonical link target. <paramref name="Canonical"/> is the ASCII,
/// percent-escaped absolute URI (IDN hosts as punycode) and is the only form ever shown
/// to the user or handed to an opener.
/// </summary>
/// <param name="Canonical">Canonical absolute URI.</param>
/// <param name="Scheme">Lower-case scheme.</param>
/// <param name="Host">Host as shown to the user (punycode for IDN); empty when none.</param>
/// <param name="LocalPath">Decoded local path for <c>file</c> targets that passed the file rules; otherwise null.</param>
/// <param name="Source">Origin of the link.</param>
public sealed record HyperlinkTarget(
    string Canonical,
    string Scheme,
    string Host,
    string? LocalPath,
    HyperlinkSource Source);
