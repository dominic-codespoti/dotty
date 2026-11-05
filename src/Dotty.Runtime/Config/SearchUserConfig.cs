using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>Initial matching mode of the search overlay (Alt+R/C/W toggle it while open); the <c>search</c> config object.</summary>
public sealed class SearchUserConfig
{
    /// <summary>Treat the query as a .NET regular expression.</summary>
    [JsonPropertyName("regex")]
    public bool Regex { get; set; }

    /// <summary>Match letter case exactly.</summary>
    [JsonPropertyName("caseSensitive")]
    public bool CaseSensitive { get; set; }

    /// <summary>Only match where neither end touches a letter, digit, or underscore.</summary>
    [JsonPropertyName("wholeWord")]
    public bool WholeWord { get; set; }
}
