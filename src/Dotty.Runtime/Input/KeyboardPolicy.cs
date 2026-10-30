using System;

namespace Dotty.Runtime.Input;

/// <summary>What one Alt/Option key does while it is held.</summary>
public enum AltKeyRole : byte
{
    /// <summary>Alt is a terminal modifier: chords are sent as <c>ESC</c>-prefixed (Meta) input and match <c>alt+…</c> bindings.</summary>
    Meta,

    /// <summary>Alt belongs to the keyboard layout: the character it composes (AltGr, Option) is typed as text.</summary>
    Compose,

    /// <summary>
    /// Compose when the layout consumed the key as AltGr/ISO Level 3, otherwise Meta. The decision uses the native
    /// modifier bits: an AltGr press reports no Alt bit (X11/Wayland Level 3) or Ctrl+Alt without a physical Ctrl key
    /// (Windows), while a plain Right Alt on a layout without AltGr reports Alt alone.
    /// </summary>
    ComposeOrMeta,
}

/// <summary>
/// Keyboard policy resolved from the <c>keyboard</c> config section for the running platform (decision K1):
/// <c>keyboard.leftAlt</c> / <c>keyboard.rightAlt</c> are <c>auto</c>, <c>meta</c> or <c>compose</c>.
/// <c>auto</c> is Meta on the left and Compose (with a Meta fallback when the layout has no AltGr) on the right on
/// Linux and Windows, and Compose on both sides on macOS.
/// </summary>
/// <param name="LeftAlt">Role of the left Alt/Option key.</param>
/// <param name="RightAlt">Role of the right Alt/Option key.</param>
/// <param name="MetaUsesCommittedText">
/// True when the character the platform delivers for a Meta chord is the layout character of the key (with Shift
/// applied), so a Meta chord sends <c>ESC</c> + that character. False on macOS, where the delivered character is the
/// Option-composed one and Meta chords are built from the key's unshifted name instead.
/// </param>
public readonly record struct KeyboardPolicy(AltKeyRole LeftAlt, AltKeyRole RightAlt, bool MetaUsesCommittedText)
{
    public const string AutoMode = "auto";
    public const string MetaMode = "meta";
    public const string ComposeMode = "compose";

    /// <summary>Resolves the config strings for this process's platform.</summary>
    public static KeyboardPolicy ForCurrentPlatform(string? leftAlt, string? rightAlt) =>
        Resolve(leftAlt, rightAlt, OperatingSystem.IsMacOS());

    /// <summary>Resolves <c>keyboard.leftAlt</c>/<c>keyboard.rightAlt</c>; unknown values mean <c>auto</c>.</summary>
    public static KeyboardPolicy Resolve(string? leftAlt, string? rightAlt, bool macOS) => new(
        ResolveSide(leftAlt, macOS, rightSide: false),
        ResolveSide(rightAlt, macOS, rightSide: true),
        MetaUsesCommittedText: !macOS);

    private static AltKeyRole ResolveSide(string? mode, bool macOS, bool rightSide)
    {
        if (string.Equals(mode?.Trim(), MetaMode, StringComparison.OrdinalIgnoreCase))
            return AltKeyRole.Meta;
        if (string.Equals(mode?.Trim(), ComposeMode, StringComparison.OrdinalIgnoreCase))
            return AltKeyRole.Compose;
        if (macOS)
            return AltKeyRole.Compose;
        return rightSide ? AltKeyRole.ComposeOrMeta : AltKeyRole.Meta;
    }
}
