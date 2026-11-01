using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>Pointer shape last requested with OSC 22; <see cref="PointerShape.Default"/> until set and after RIS.</summary>
    public PointerShape RequestedPointerShape { get; private set; }

    /// <summary>
    /// OSC 22 ; name[,fallback…]: the first known name wins (kitty's <c>=</c>/<c>&gt;</c> prefixes set, <c>&lt;</c>
    /// resets). Queries (<c>?</c>) and lists of unknown names leave the shape unchanged.
    /// </summary>
    private void HandleOsc22(ReadOnlySpan<char> payload)
    {
        if (!payload.IsEmpty && payload[0] == '?')
            return;
        if (!payload.IsEmpty && payload[0] == '<')
        {
            RequestedPointerShape = PointerShape.Default;
            return;
        }
        if (!payload.IsEmpty && payload[0] is '=' or '>')
            payload = payload[1..];
        if (payload.IsEmpty)
        {
            RequestedPointerShape = PointerShape.Default;
            return;
        }
        foreach (Range part in payload.Split(','))
        {
            if (PointerShapeNames.TryParse(payload[part], out var shape))
            {
                RequestedPointerShape = shape;
                return;
            }
        }
    }
}
