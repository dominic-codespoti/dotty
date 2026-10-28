using System;
using System.Buffers;
using System.Text;
using Dotty.Abstractions.Clipboard;

namespace Dotty.Terminal.Adapter;

// OSC 52 clipboard write/read requests.
public partial class TerminalAdapter
{
    /// <summary>Raised for OSC 52 "?" queries. Concrete adapter only; no subscribers yet.</summary>
    public event Action<ClipboardReadRequest>? ClipboardReadRequested;

    private void HandleOsc52(ReadOnlySpan<char> payload)
    {
        int semi = payload.IndexOf(';');
        if (semi < 0)
            return;

        var selection = Osc52Selectors.Parse(payload[..semi]);
        if (selection == ClipboardSelection.None)
            return;

        var data = payload[(semi + 1)..];
        if (data.Length == 1 && data[0] == '?')
        {
            ClipboardReadRequested?.Invoke(new ClipboardReadRequest(selection));
            return;
        }

        var handler = ClipboardWriteRequested;
        if (handler is null)
            return;

        if (data.IsEmpty)
        {
            handler(new ClipboardWriteRequest(selection, string.Empty));
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length / 4 * 3 + 3);
        try
        {
            if (!Convert.TryFromBase64Chars(data, rented, out int written))
                return;
            string text = Encoding.UTF8.GetString(rented, 0, written);
            handler(new ClipboardWriteRequest(selection, text));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
