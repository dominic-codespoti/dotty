namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>
    /// DECBKM (mode 67): true = Backspace sends BS (0x08); false (default) = DEL (0x7F).
    /// DECSTR leaves it untouched; RIS resets it via <see cref="ResetBackarrowKeyMode"/>.
    /// </summary>
    public bool BackarrowSendsBackspace { get; private set; }

    public void OnSetBackarrowKeyMode(bool sendsBackspace) => BackarrowSendsBackspace = sendsBackspace;

    /// <summary>Restores the default (DEL). To be called from the RIS path only.</summary>
    internal void ResetBackarrowKeyMode() => BackarrowSendsBackspace = false;
}
