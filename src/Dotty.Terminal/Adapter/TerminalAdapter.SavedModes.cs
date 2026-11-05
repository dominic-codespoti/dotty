namespace Dotty.Terminal.Adapter;

/// <summary>One-level XTSAVE/XTRESTORE state for implemented persistent DEC private modes.</summary>
public partial class TerminalAdapter
{
    // xterm stores one bit per persistent mode, with shared slots for the three mode families below.
    // The two mouse-family slots retain the selected value, not merely an enabled bit.
    private const uint SavedAppCursor = 1u << 0;
    private const uint SavedColumnCount = 1u << 1;
    private const uint SavedReverseVideo = 1u << 2;
    private const uint SavedOrigin = 1u << 3;
    private const uint SavedAutoWrap = 1u << 4;
    private const uint SavedCursorVisible = 1u << 5;
    private const uint SavedColumnPermission = 1u << 6;
    private const uint SavedMoreFix = 1u << 7;
    private const uint SavedReverseWrap = 1u << 8;
    private const uint SavedLeftRightMargins = 1u << 9;
    private const uint SavedNoClearColumns = 1u << 10;
    private const uint SavedExtendedReverseWrap = 1u << 11;
    private const uint SavedAlternateBuffer = 1u << 12;
    private const uint SavedKeypadApplication = 1u << 13;
    private const uint SavedBackarrow = 1u << 14;
    private const uint SavedFocusReports = 1u << 15;
    private const uint SavedAlternateScroll = 1u << 16;
    private const uint SavedBracketedPaste = 1u << 17;
    private const uint SavedSynchronizedUpdate = 1u << 18;
    private const uint SavedVisibilityReports = 1u << 19;
    private const uint SavedSmoothScroll = 1u << 20;
    private const uint SavedPrintFormFeed = 1u << 21;
    private const uint SavedPrintExtent = 1u << 22;
    private const uint SavedFontShift = 1u << 23;
    private const uint SavedNationalReplacement = 1u << 24;

    private uint _savedPrivateModeBits;
    private int _savedMouseTrackingMode;
    private int _savedMouseEncodingMode;

    /// <summary>Save the live value of a persistent, implemented DEC private mode.</summary>
    public void OnSavePrivateMode(int mode)
    {
        switch (mode)
        {
            case 1048: OnSaveCursor(); break;
            case 1: SaveMode(SavedAppCursor, ApplicationCursorKeysEnabled); break;
            case 3:
                // xterm only maintains the DECCOLM save slot while column changes are allowed.
                if (ColumnModeChangesAllowed) SaveMode(SavedColumnCount, _buffer.Columns == 132);
                break;
            case 4: SaveMode(SavedSmoothScroll, SmoothScrollEnabled); break;
            case 5: SaveMode(SavedReverseVideo, ReverseVideo); break;
            case 6: SaveMode(SavedOrigin, _buffer.OriginMode); break;
            case 7: SaveMode(SavedAutoWrap, _buffer.AutoWrap); break;
            case 18: SaveMode(SavedPrintFormFeed, PrintFormFeedEnabled); break;
            case 19: SaveMode(SavedPrintExtent, PrintExtentFullPage); break;
            case 25: SaveMode(SavedCursorVisible, _buffer.CursorVisible); break;
            case 35: SaveMode(SavedFontShift, FontShiftModeEnabled); break;
            case 40: SaveMode(SavedColumnPermission, ColumnModeChangesAllowed); break;
            case 41: SaveMode(SavedMoreFix, MoreFixEnabled); break;
            case 42: SaveMode(SavedNationalReplacement, NationalReplacementEnabled); break;
            case 45: SaveMode(SavedReverseWrap, _buffer.ReverseWraparound); break;
            case 69: SaveMode(SavedLeftRightMargins, _buffer.LeftRightMarginMode); break;
            case 95:
                if (_conformanceLevel >= 5) SaveMode(SavedNoClearColumns, NoClearOnColumnChange);
                break;
            case 1045: SaveMode(SavedExtendedReverseWrap, _buffer.ExtendedReverseWraparound); break;
            case 47:
            case 1047:
            case 1049: SaveMode(SavedAlternateBuffer, _buffer.IsAlternateScreenActive); break;
            case 66: SaveMode(SavedKeypadApplication, KeypadApplicationMode); break;
            case 67: SaveMode(SavedBackarrow, BackarrowSendsBackspace); break;
            case 9:
            case 1000:
            case 1002:
            case 1003: _savedMouseTrackingMode = (int)CurrentMouseMode; break;
            case 1004: SaveMode(SavedFocusReports, FocusReportingEnabled); break;
            case 1005:
            case 1006:
            case 1015:
            case 1016: _savedMouseEncodingMode = (int)CurrentMouseEncoding; break;
            case 1007: SaveMode(SavedAlternateScroll, AlternateScrollMode); break;
            case 2004: SaveMode(SavedBracketedPaste, _buffer.BracketedPasteMode); break;
            case 2026: SaveMode(SavedSynchronizedUpdate, SynchronizedUpdateActive); break;
            case 2033: SaveMode(SavedVisibilityReports, VisibilityReportsEnabled); break;
                // Action-only (1048), readonly, recognized-but-ignored, and unsupported modes have no slot.
        }
    }

    /// <summary>Restore the cached value of a persistent, implemented DEC private mode.</summary>
    public void OnRestorePrivateMode(int mode)
    {
        switch (mode)
        {
            case 1048: OnRestoreCursor(); break;
            case 1: OnSetApplicationCursorKeys(HasSavedMode(SavedAppCursor)); break;
            case 3:
                if (ColumnModeChangesAllowed) OnSetColumnMode(3, HasSavedMode(SavedColumnCount));
                break;
            case 4: OnSetSmoothScroll(HasSavedMode(SavedSmoothScroll)); break;
            case 5: OnSetReverseVideo(HasSavedMode(SavedReverseVideo)); break;
            case 6: OnSetOriginMode(HasSavedMode(SavedOrigin)); break;
            case 7: OnSetAutoWrap(HasSavedMode(SavedAutoWrap)); break;
            case 18: OnSetPrinterMode(18, HasSavedMode(SavedPrintFormFeed)); break;
            case 19: OnSetPrinterMode(19, HasSavedMode(SavedPrintExtent)); break;
            case 25: OnSetCursorVisibility(HasSavedMode(SavedCursorVisible)); break;
            case 35: OnSetFontShiftMode(HasSavedMode(SavedFontShift)); break;
            case 40: OnSetColumnMode(40, HasSavedMode(SavedColumnPermission)); break;
            case 41: OnSetMoreFix(HasSavedMode(SavedMoreFix)); break;
            case 42: OnSetNationalReplacementMode(HasSavedMode(SavedNationalReplacement)); break;
            case 45: OnSetReverseWrapMode(45, HasSavedMode(SavedReverseWrap)); break;
            case 69: OnSetLeftRightMarginMode(HasSavedMode(SavedLeftRightMargins)); break;
            case 95: OnSetColumnMode(95, HasSavedMode(SavedNoClearColumns)); break;
            case 1045: OnSetReverseWrapMode(1045, HasSavedMode(SavedExtendedReverseWrap)); break;
            case 47:
            case 1047:
            case 1049: OnSwitchAlternateScreen(47, HasSavedMode(SavedAlternateBuffer)); break;
            case 66: OnSetKeypadApplicationMode(HasSavedMode(SavedKeypadApplication)); break;
            case 67: OnSetBackarrowKeyMode(HasSavedMode(SavedBackarrow)); break;
            case 9:
            case 1000:
            case 1002:
            case 1003:
                RestoreMouseTrackingMode();
                break;
            case 1004: OnSetFocusReporting(HasSavedMode(SavedFocusReports)); break;
            case 1005:
            case 1006:
            case 1015:
            case 1016:
                RestoreMouseEncodingMode();
                break;
            case 1007: OnSetMouseMode(1007, HasSavedMode(SavedAlternateScroll)); break;
            case 2004: OnSetBracketedPasteMode(HasSavedMode(SavedBracketedPaste)); break;
            case 2026: OnSetSynchronizedUpdate(HasSavedMode(SavedSynchronizedUpdate)); break;
            case 2033: OnSetVisibilityReports(HasSavedMode(SavedVisibilityReports)); break;
        }
    }

    private void RestoreMouseTrackingMode()
    {
        if (_savedMouseTrackingMode != 0)
            OnSetMouseMode(_savedMouseTrackingMode, true);
        else if (CurrentMouseMode != MouseMode.None)
            OnSetMouseMode((int)CurrentMouseMode, false);
    }

    private void RestoreMouseEncodingMode()
    {
        if (_savedMouseEncodingMode != 0)
            OnSetMouseMode(_savedMouseEncodingMode, true);
        else if (CurrentMouseEncoding != MouseEncoding.Default)
            OnSetMouseMode((int)CurrentMouseEncoding, false);
    }

    private void SaveMode(uint slot, bool enabled)
    {
        if (enabled) _savedPrivateModeBits |= slot;
        else _savedPrivateModeBits &= ~slot;
    }

    private bool HasSavedMode(uint slot) => (_savedPrivateModeBits & slot) != 0;
}
