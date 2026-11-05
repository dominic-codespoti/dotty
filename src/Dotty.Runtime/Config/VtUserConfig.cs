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

    /// <summary>Answer icon-title (<c>CSI 20 t</c>) and window-title (<c>CSI 21 t</c>) report queries. Off by default.</summary>
    [JsonPropertyName("titleReportEnabled")]
    public bool TitleReportEnabled { get; set; }

    /// <summary>
    /// Allows applications to move, minimize, and restore the native window with
    /// <c>CSI 3 ; x ; y t</c>, <c>CSI 2 t</c>, and <c>CSI 1 t</c>. Off by default.
    /// </summary>
    [JsonPropertyName("windowControlAllowed")]
    public bool WindowControlAllowed { get; set; }
}
