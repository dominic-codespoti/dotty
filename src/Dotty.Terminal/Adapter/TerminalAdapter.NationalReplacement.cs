namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>DEC private mode 42 enables national replacement character sets.</summary>
    public bool NationalReplacementEnabled { get; private set; }

    public void OnSetNationalReplacementMode(bool enabled) => NationalReplacementEnabled = enabled;
}
