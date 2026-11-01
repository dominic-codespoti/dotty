using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>File drag-and-drop onto a pane.</summary>
public sealed class DropUserConfig
{
    /// <summary><c>paste-path</c> (default): paste the shell-escaped paths through the paste router; <c>ignore</c>: do nothing.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "paste-path";

    /// <summary><c>auto</c> (default: from the pane's launch shell), <c>posix</c>, <c>fish</c>, <c>powershell</c> or <c>cmd</c>.</summary>
    [JsonPropertyName("quote")]
    public string Quote { get; set; } = "auto";
}
