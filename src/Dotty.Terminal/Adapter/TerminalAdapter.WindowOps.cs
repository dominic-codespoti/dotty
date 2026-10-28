using System;

namespace Dotty.Terminal.Adapter;

// Window manipulation (CSI Ps ; Ps ; Ps t) and the reports it requests.
public partial class TerminalAdapter
{
    public void OnWindowOperation(int operation, int arg1, int arg2)
    {
        switch (operation)
        {
            case 14:
                // CSI 14 t → report window pixel size: CSI 4 ; height ; width t
                SendTwoNumberReply("\x1b[4;".AsSpan(), _windowPixelHeight, _windowPixelWidth, 't');
                break;
            case 18:
                // CSI 18 t → report window cell size: CSI 8 ; rows ; cols t
                SendTwoNumberReply("\x1b[8;".AsSpan(), _buffer.Rows, _buffer.Columns, 't');
                break;
            case 20:
            case 21:
                // Icon title (20) / window title (21) — respond with empty for now.
                ReplyRequested?.Invoke("\x1b]0;\x1b\\".AsSpan());
                break;
        }
    }
}
