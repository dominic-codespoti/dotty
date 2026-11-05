namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>Insert mode (IRM, DEC private mode 4) for printable output.</summary>
    public bool InsertMode { get; internal set; }

    /// <summary>New-line mode (LNM, DEC private mode 20) for line feed output.</summary>
    public bool NewLineMode { get; internal set; }
}
