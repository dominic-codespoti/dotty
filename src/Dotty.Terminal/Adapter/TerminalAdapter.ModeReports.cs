namespace Dotty.Terminal.Adapter;

// DECRQM status table (xterm VT420 profile). Reply states: 0 not recognized,
// 1 set, 2 reset, 3 permanently set, 4 permanently reset. Allocation-free:
// a constant-pattern switch over the mode number reading live adapter state.
public partial class TerminalAdapter
{
    private const int ModeNotRecognized = 0;
    private const int ModeSet = 1;
    private const int ModeReset = 2;
    private const int ModePermanentlySet = 3;
    private const int ModePermanentlyReset = 4;

    private static int ModeFlag(bool set) => set ? ModeSet : ModeReset;

    /// <summary>
    /// DECRQM state for <paramref name="mode"/>. Private (DEC) modes: every mode the parser
    /// dispatches reports its live state; modes Dotty deliberately ignores report permanently
    /// reset (4), always-on ones permanently set (3); everything else is 0. ANSI modes follow
    /// xterm's table with Dotty's actual behavior (no CSI Ps h/l support at all).
    /// </summary>
    internal int GetModeStatus(int mode, bool isPrivate)
    {
        return isPrivate ? GetPrivateModeStatus(mode) : GetAnsiModeStatus(mode);
    }

    private int GetPrivateModeStatus(int mode)
    {
        switch (mode)
        {
            case 1: return ModeFlag(ApplicationCursorKeysEnabled); // DECCKM
            case 2: return ModePermanentlySet; // DECANM: there is no VT52 mode
            case 6: return ModeFlag(_buffer.OriginMode); // DECOM
            case 7: return ModeFlag(_buffer.AutoWrap); // DECAWM
            case 25: return ModeFlag(_buffer.CursorVisible); // DECTCEM
            case 47:
            case 1047:
            case 1049:
                return ModeFlag(_buffer.IsAlternateScreenActive);
            case 66: return ModeFlag(KeypadApplicationMode); // DECNKM
            case 67: return ModeFlag(BackarrowSendsBackspace); // DECBKM
            case 1000: return ModeFlag(CurrentMouseMode == MouseMode.Normal);
            case 1002: return ModeFlag(CurrentMouseMode == MouseMode.ButtonEvent);
            case 1003: return ModeFlag(CurrentMouseMode == MouseMode.AnyEvent);
            case 1004: return ModeFlag(FocusReportingEnabled);
            case 1005: return ModeFlag(CurrentMouseEncoding == MouseEncoding.UTF8);
            case 1006: return ModeFlag(CurrentMouseEncoding == MouseEncoding.SGR);
            case 1015: return ModeFlag(CurrentMouseEncoding == MouseEncoding.URXVT);
            case 2004: return ModeFlag(_buffer.BracketedPasteMode);
            case 2026: return ModeFlag(SynchronizedUpdateActive);
            case 2027: return ModePermanentlySet; // grapheme clustering is always on
            case 2033: return ModeFlag(VisibilityReportsEnabled);

            // Recognized but ignored (SET/RESET are accepted and have no effect).
            case 3: // DECCOLM
            case 4: // DECSCLM
            case 5: // DECSCNM
            case 8: // DECARM
            case 9: // X10 mouse: deliberately not routed (no press-only encoding)
            case 12: // AT&T 610 blink
            case 18: // DECPFF
            case 19: // DECPEX
            case 40: // 80/132 enable
            case 42: // DECNRCM
            case 45: // reverse wrap
            case 69: // DECLRMM
            case 80: // DECSDM
            case 95: // DECNCSM
            case 1007: // alternate scroll
            case 1016: // SGR pixel mouse
            case 1048: // save/restore cursor: an action, not a persistent mode
            // xterm "recognized, unimplemented" VT3xx/VT4xx modes:
            case 11:
            case 16:
            case 53:
            case 59:
            case 60:
            case 61:
            case 64:
            case 68:
            case 73:
            case 81:
                return ModePermanentlyReset;
            default:
                return ModeNotRecognized;
        }
    }

    private static int GetAnsiModeStatus(int mode)
    {
        switch (mode)
        {
            case 1: // GATM
            case 2: // KAM
            case 3: // CRM
            case 4: // IRM (not implemented)
            case 5: // SRTM
            case 7: // VEM
            case 10: // HEM
            case 11: // PUM
            case >= 13 and <= 19: // FEAM..TSM family, always reset
            case 20: // LNM (not implemented)
                return ModePermanentlyReset;
            case 12: // SRM: Dotty never echoes locally, i.e. "send/receive" is set
                return ModePermanentlySet;
            default:
                return ModeNotRecognized;
        }
    }
}
