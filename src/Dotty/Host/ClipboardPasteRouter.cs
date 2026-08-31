using Dotty.Runtime.Input;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk;

public static class ClipboardPasteRouter
{
    public static byte[] Encode(string text, TerminalAdapter adapter) =>
        BracketedPasteEncoder.Encode(text, adapter.Buffer.BracketedPasteMode);
}
