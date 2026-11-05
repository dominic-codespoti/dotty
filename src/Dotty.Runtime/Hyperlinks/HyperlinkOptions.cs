using System;
using System.Collections.Generic;
using Dotty.Runtime.Config;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>When a prompt is required before opening an allowed link.</summary>
public enum HyperlinkConfirmMode
{
    /// <summary>Prompt only when the target is non-web, local, ambiguous or mismatched.</summary>
    Risky,

    /// <summary>Prompt for every allowed link.</summary>
    Always,

    /// <summary>Never prompt. Hard blocks still apply.</summary>
    Never,
}

/// <summary>Resolved hyperlink settings consumed by the policy and activator.</summary>
public sealed record HyperlinkOptions(
    bool Osc8,
    HyperlinkConfirmMode Confirm,
    IReadOnlyList<string> AllowedSchemes)
{
    private static readonly string[] DefaultSchemes = ["http", "https", "mailto", "file", "ssh", "git"];

    public static HyperlinkOptions Default { get; } = new(true, HyperlinkConfirmMode.Risky, DefaultSchemes);

    /// <summary>Builds options from user configuration; unknown <c>confirm</c> values mean <see cref="HyperlinkConfirmMode.Risky"/>.</summary>
    public static HyperlinkOptions From(HyperlinksUserConfig? config)
    {
        if (config is null)
            return Default;

        HyperlinkConfirmMode mode = config.Confirm?.Trim().ToLowerInvariant() switch
        {
            "always" => HyperlinkConfirmMode.Always,
            "never" => HyperlinkConfirmMode.Never,
            _ => HyperlinkConfirmMode.Risky,
        };

        IReadOnlyList<string> schemes = config.AllowedSchemes is null
            ? DefaultSchemes
            : config.AllowedSchemes.ToArray();
        return new HyperlinkOptions(config.Osc8, mode, schemes);
    }
}
