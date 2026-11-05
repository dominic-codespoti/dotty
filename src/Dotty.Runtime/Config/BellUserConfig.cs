using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>What a terminal bell (BEL, 0x07) does besides the tab marker and the Lua <c>bell</c> event; the <c>bell</c> config object.</summary>
public sealed class BellUserConfig
{
    /// <summary>Request window attention (taskbar flash, dock bounce, urgency hint) when the window is not focused.</summary>
    [JsonPropertyName("attention")]
    public bool Attention { get; set; } = true;

    /// <summary>Briefly flash the window contents.</summary>
    [JsonPropertyName("visual")]
    public bool Visual { get; set; }

    /// <summary>Play a sound. Accepted but not yet implemented on any platform.</summary>
    [JsonPropertyName("audible")]
    public bool Audible { get; set; }
}
