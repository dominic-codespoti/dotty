using System.Text.Json.Serialization;
using Dotty.Runtime.Input;

namespace Dotty.Runtime.Config;

/// <summary>Keyboard behavior; the <c>keyboard</c> config object. Values are strings so a typo cannot fail the whole config load; see <see cref="KeyboardPolicy"/>.</summary>
public sealed class KeyboardUserConfig
{
    /// <summary>Left Alt/Option: <c>auto</c> (default), <c>meta</c> or <c>compose</c>.</summary>
    [JsonPropertyName("leftAlt")]
    public string LeftAlt { get; set; } = KeyboardPolicy.AutoMode;

    /// <summary>Right Alt/Option (AltGr): <c>auto</c> (default), <c>meta</c> or <c>compose</c>.</summary>
    [JsonPropertyName("rightAlt")]
    public string RightAlt { get; set; } = KeyboardPolicy.AutoMode;
}
