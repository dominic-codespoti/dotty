using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>
    /// When false, OSC 8 sequences are ignored at ingest: nothing is stored and any
    /// active link is closed. Hosts set this from configuration.
    /// </summary>
    public bool Osc8HyperlinksEnabled { get; set; } = true;

    /// <summary>Handles the payload of OSC 8 (<c>params;uri</c>).</summary>
    private void HandleOsc8(ReadOnlySpan<char> payload)
    {
        if (!Osc8HyperlinksEnabled
            || !HyperlinkUri.TryParseOsc8Payload(payload, out _, out ReadOnlySpan<char> uri))
        {
            _currentAttributes.HyperlinkId = 0;
            return;
        }

        SetActiveHyperlink(uri);
    }

    /// <summary>
    /// Makes <paramref name="uri"/> the active link for subsequent text. An empty,
    /// invalid (control/format characters, over-length) or unstorable URI closes the
    /// active link; a previous link is never kept alive across garbage.
    /// </summary>
    private void SetActiveHyperlink(ReadOnlySpan<char> uri)
    {
        _currentAttributes.HyperlinkId =
            Osc8HyperlinksEnabled && HyperlinkUri.Validate(uri) == HyperlinkUriStatus.Ok
                ? _buffer.GetOrCreateHyperlinkId(uri)
                : (ushort)0;
    }
}
