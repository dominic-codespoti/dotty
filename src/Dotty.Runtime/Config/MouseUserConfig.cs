using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>Pointer behavior; the <c>mouse</c> config object. Values are strings so a typo cannot fail the whole config load; see <see cref="MouseOptions"/>.</summary>
public sealed class MouseUserConfig
{
    /// <summary>Where a finished local selection is copied automatically: <c>none</c> (default), <c>primary</c>, <c>clipboard</c> or <c>both</c>.</summary>
    [JsonPropertyName("copyOnSelect")]
    public string CopyOnSelect { get; set; } = "none";

    /// <summary>Middle-click action: <c>clipboard-paste</c> (default), <c>primary-paste</c> or <c>ignore</c>.</summary>
    [JsonPropertyName("middleClickAction")]
    public string MiddleClickAction { get; set; } = "clipboard-paste";
}
