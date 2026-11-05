using System;

namespace Dotty.Terminal.Adapter;

/// <summary>Mouse pointer shapes an application can request with OSC 22 (and the host uses for its own chrome).</summary>
public enum PointerShape
{
    /// <summary>No request: the host's own choice (text beam over pane content).</summary>
    Default,
    Arrow,
    Text,
    Pointer,
    Crosshair,
    EwResize,
    NsResize,
    NwseResize,
    NeswResize,
    Move,
    NotAllowed,
    Wait,
    Progress,

    /// <summary>Pointer hidden (only produced by the host, e.g. <c>mouse.hideWhileTyping</c>).</summary>
    Hidden,
}

/// <summary>Parses OSC 22 shape names: CSS cursor names (kitty, foot) and the common X11 cursor-font names (xterm).</summary>
public static class PointerShapeNames
{
    /// <summary>False for unknown names; an empty name (or <c>default</c>) resets to <see cref="PointerShape.Default"/>.</summary>
    public static bool TryParse(ReadOnlySpan<char> name, out PointerShape shape)
    {
        name = name.Trim();
        shape = name.Length == 0 ? PointerShape.Default : name.ToString().ToLowerInvariant() switch
        {
            "default" => PointerShape.Default,
            "left_ptr" or "arrow" or "top_left_arrow" => PointerShape.Arrow,
            "text" or "xterm" or "ibeam" or "vertical-text" => PointerShape.Text,
            "pointer" or "hand" or "hand1" or "hand2" or "pointing_hand" => PointerShape.Pointer,
            "crosshair" or "cross" or "tcross" or "cell" => PointerShape.Crosshair,
            "ew-resize" or "col-resize" or "e-resize" or "w-resize" or "sb_h_double_arrow" or "h_double_arrow" => PointerShape.EwResize,
            "ns-resize" or "row-resize" or "n-resize" or "s-resize" or "sb_v_double_arrow" or "v_double_arrow" => PointerShape.NsResize,
            "nwse-resize" or "nw-resize" or "se-resize" or "bottom_right_corner" or "top_left_corner" => PointerShape.NwseResize,
            "nesw-resize" or "ne-resize" or "sw-resize" or "bottom_left_corner" or "top_right_corner" => PointerShape.NeswResize,
            "move" or "all-scroll" or "fleur" or "grab" or "grabbing" => PointerShape.Move,
            "not-allowed" or "no-drop" or "crossed_circle" => PointerShape.NotAllowed,
            "wait" or "watch" => PointerShape.Wait,
            "progress" or "left_ptr_watch" => PointerShape.Progress,
            _ => (PointerShape)(-1),
        };
        if ((int)shape < 0)
        {
            shape = PointerShape.Default;
            return false;
        }
        return true;
    }
}
