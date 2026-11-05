using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>Hyperlink (OSC 8 and detected URL) behavior; the <c>hyperlinks</c> config object.</summary>
public sealed class HyperlinksUserConfig
{
    /// <summary>Honor OSC 8 explicit hyperlinks. Detected plain URLs are unaffected.</summary>
    [JsonPropertyName("osc8")]
    public bool Osc8 { get; set; } = true;

    /// <summary>Confirmation policy: <c>risky</c> (default), <c>always</c> or <c>never</c>. Unknown values mean <c>risky</c>.</summary>
    [JsonPropertyName("confirm")]
    public string Confirm { get; set; } = "risky";

    /// <summary>URI schemes that may be opened. Hard-blocked schemes cannot be added.</summary>
    [JsonPropertyName("allowedSchemes")]
    public List<string> AllowedSchemes { get; set; } = ["http", "https", "mailto", "file", "ssh", "git"];
}
