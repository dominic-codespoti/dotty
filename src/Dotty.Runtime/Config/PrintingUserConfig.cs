using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>Trusted host configuration for the optional file-backed text printer.</summary>
public sealed class PrintingUserConfig
{
    /// <summary>
    /// File receiving printed text. Null or empty disables printing. Terminal escape
    /// sequences cannot choose this path or launch an external printer command.
    /// </summary>
    [JsonPropertyName("outputFile")]
    public string? OutputFile { get; set; }
}
