using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>Local text selection; the <c>selection</c> config object. Parsed leniently by <see cref="Selection.SelectionOptions"/>.</summary>
public sealed class SelectionUserConfig
{
    /// <summary>Punctuation that a double-click word keeps, besides letters, digits, marks, <c>_</c> and emoji.</summary>
    [JsonPropertyName("wordChars")]
    public string WordChars { get; set; } = Selection.SelectionOptions.DefaultWordChars;

    /// <summary>Clear the selection after the Copy action put it on the clipboard (default true).</summary>
    [JsonPropertyName("clearOnCopy")]
    public bool ClearOnCopy { get; set; } = true;

    /// <summary>Clear the selection when typed text is sent to the pane (default true).</summary>
    [JsonPropertyName("clearOnTyping")]
    public bool ClearOnTyping { get; set; } = true;

    /// <summary>Modifier that turns a drag into a rectangular (block) selection: <c>alt</c> (default), <c>ctrl</c>, <c>shift</c>, <c>super</c> or <c>none</c>.</summary>
    [JsonPropertyName("rectangleModifier")]
    public string RectangleModifier { get; set; } = "alt";
}
