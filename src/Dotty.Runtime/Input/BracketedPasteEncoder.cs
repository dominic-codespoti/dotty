using System;
using System.Text;

namespace Dotty.Runtime.Input;

public static class BracketedPasteEncoder
{
    private const string StartMarker = "\x1b[200~";
    private const string EndMarker = "\x1b[201~";

    public static byte[] Encode(string text, bool bracketed)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<byte>();

        if (!bracketed)
            return Encoding.UTF8.GetBytes(text);

        var payload = Encoding.UTF8.GetBytes(text);
        var start = Encoding.ASCII.GetBytes(StartMarker);
        var end = Encoding.ASCII.GetBytes(EndMarker);
        var result = new byte[start.Length + payload.Length + end.Length];
        start.CopyTo(result, 0);
        payload.CopyTo(result, start.Length);
        end.CopyTo(result, start.Length + payload.Length);
        return result;
    }
}
