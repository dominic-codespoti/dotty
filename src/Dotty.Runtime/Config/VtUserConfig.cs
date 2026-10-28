using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

/// <summary>
/// Opt-in VT behaviors that expose host or screen state to the running program; the <c>vt</c> config object.
/// Declared <c>partial</c> so independent features can add their own switches in separate files.
/// </summary>
public sealed partial class VtUserConfig
{
    /// <summary>
    /// Answer DECRQCRA (<c>CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y</c>) checksum requests. Off by default:
    /// the reply lets a program read back screen contents. When false no reply is sent at all.
    /// </summary>
    [JsonPropertyName("checksumReportEnabled")]
    public bool ChecksumReportEnabled { get; set; }
}
