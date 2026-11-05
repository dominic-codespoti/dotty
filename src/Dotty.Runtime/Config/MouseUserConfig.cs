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

    /// <summary>Lines per wheel notch for local scrolling and alternate scroll (default 3); clamped to 0.01 through 100.</summary>
    [JsonPropertyName("scrollMultiplier")]
    public double ScrollMultiplier { get; set; } = MouseOptions.DefaultScrollMultiplier;

    /// <summary>Wheel to cursor up/down on the alternate screen without mouse reporting (mode 1007): <c>auto</c> (default) or <c>off</c>.</summary>
    [JsonPropertyName("alternateScroll")]
    public string AlternateScroll { get; set; } = "auto";

    /// <summary>Modifier that keeps clicks, drags and the wheel local while an application tracks the mouse: <c>shift</c> (default), <c>ctrl</c>, <c>alt</c>, <c>super</c> or <c>none</c>.</summary>
    [JsonPropertyName("reportBypassModifier")]
    public string ReportBypassModifier { get; set; } = "shift";

    /// <summary>Hide the pointer while typing in a pane until it moves again (default false).</summary>
    [JsonPropertyName("hideWhileTyping")]
    public bool HideWhileTyping { get; set; }
}
