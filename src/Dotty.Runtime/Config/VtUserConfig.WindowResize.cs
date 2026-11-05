using System.Text.Json.Serialization;

namespace Dotty.Runtime.Config;

// Window-resize gate of the `vt` config section (the section type itself and the
// root `vt` property are declared with the rest of the section).
public sealed partial class VtUserConfig
{
    /// <summary>
    /// Allows applications to resize the window with <c>CSI 8 ; rows ; cols t</c>,
    /// <c>CSI Ps t</c> (Ps &gt;= 24), and DECCOLM when mode 40 permits it. Default false.
    /// Even when enabled, requests are ignored with several tabs, split panes, or a non-normal window.
    /// </summary>
    [JsonPropertyName("windowResizeAllowed")]
    public bool WindowResizeAllowed { get; set; }
}
