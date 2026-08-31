namespace Dotty.Runtime.Clipboard;

public interface ITerminalClipboard
{
    string? GetText();
    void SetText(string text);
    bool HasText { get; }
}
