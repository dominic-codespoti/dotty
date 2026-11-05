namespace Dotty.Terminal.Adapter;

/// <summary>DEC private column-change modes: permission (40), clear behavior (95), and column count (3).</summary>
public partial class TerminalAdapter
{
    /// <summary>DEC private mode 40 allows DECCOLM column changes.</summary>
    public bool ColumnModeChangesAllowed { get; private set; }

    /// <summary>DEC private mode 95 suppresses DECCOLM clearing at conformance level 5 or later.</summary>
    public bool NoClearOnColumnChange { get; private set; }

    /// <summary>Applies DEC private modes 40, 95, and 3.</summary>
    public void OnSetColumnMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 40:
                ColumnModeChangesAllowed = enabled;
                break;
            case 95:
                NoClearOnColumnChange = enabled && _conformanceLevel >= 5;
                break;
            case 3:
                if (!ColumnModeChangesAllowed)
                    return;

                lock (_buffer.SyncRoot)
                {
                    _buffer.SetColumnMode(clearScreen: !NoClearOnColumnChange);
                    RequestWindowResize(0, enabled ? 132 : 80);
                }
                RequestRender();
                break;
        }
    }

    /// <summary>
    /// Applies the column-mode reset portion of DECSTR or RIS. DECSTR preserves the terminal's mode-40
    /// permission while resetting mode 95; RIS passes <paramref name="resetPermission"/> to clear both.
    /// A caller performing RIS must request 80 columns when the buffer was actually 132 columns.
    /// </summary>
    internal void ResetColumnModes(bool resetPermission = false)
    {
        NoClearOnColumnChange = false;
        if (resetPermission)
            ColumnModeChangesAllowed = false;
    }
}
