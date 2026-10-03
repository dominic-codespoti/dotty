using System;
using System.Text;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Panes;
using Dotty.Silk.Input;
using Xunit;
using SilkKey = Silk.NET.Input.Key;

namespace Dotty.App.Tests;

public sealed class TerminalKeyboardDispatcherTests
{
    internal sealed class FakeHost : ITerminalKeyboardHost, IDisposable
    {
        public TerminalTabManager TabManager { get; } = new();
        public TerminalTab? ActiveTab { get; set; }
        public LuaScriptHost LuaHost { get; }
        public KeybindingManager Keybindings { get; } = new();
        public ContextMenuModel? ActiveContextMenu { get; set; }
        public int Rows { get; set; } = 24;
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }
        public bool AltGr { get; set; }
        public bool Super { get; set; }
        public TerminalKeyModifiers LockModifiers { get; set; }
        public int CopyCount { get; private set; }
        public int PasteCount { get; private set; }
        public int CreateTabCount { get; private set; }
        public int ClearCount { get; private set; }
        public int FullscreenCount { get; private set; }
        public int ZoomInCount { get; private set; }
        public int ZoomOutCount { get; private set; }
        public int ResetZoomCount { get; private set; }
        public int DuplicateTabCount { get; private set; }
        public int CloseOtherTabsCount { get; private set; }
        public int QuitCount { get; private set; }
        public FakeHost()
        {
            LuaHost = new LuaScriptHost(new FakeLuaHostServices(TabManager), TabManager);
            LuaHost.Evaluate(new DottyUserConfig(), null);
        }
        public List<byte> InputBytes { get; } = new();

        public void CopySelection() => CopyCount++;
        public void PasteClipboard() => PasteCount++;
        public void ToggleFullscreen() => FullscreenCount++;
        public void ZoomIn() => ZoomInCount++;
        public void ZoomOut() => ZoomOutCount++;
        public void ResetZoom() => ResetZoomCount++;
        public void DuplicateTab(TerminalTab activeTab) => DuplicateTabCount++;
        public void CloseOtherTabs(TerminalTab activeTab) => CloseOtherTabsCount++;
        public void Quit() => QuitCount++;
        public void CreateTab(TerminalTab activeTab) => CreateTabCount++;
        public void ClearTerminal(TerminalTab activeTab) => ClearCount++;
        public void WriteInput(TerminalTab activeTab, ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
                InputBytes.Add(bytes[i]);
        }

        public void Dispose()
        {
            ActiveTab?.Dispose();
            LuaHost.Dispose();
            TabManager.Dispose();
        }
    }

    private static FakeHost CreateHost()
    {
        var host = new FakeHost
        {
            ActiveTab = new TerminalTab(rows: 24, columns: 80)
        };
        return host;
    }
    private static void CreateScrollback(LeafPane pane, int lines)
    {
        var buffer = pane.Session.Adapter.Buffer;
        buffer.SetCursor(buffer.Rows - 1, 0);
        for (int i = 0; i < lines; i++)
            buffer.LineFeed();
    }


    [Fact]
    public void HandleKeyEvent_DefaultWindowActions_DispatchExactlyOnce()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);

        host.Ctrl = true;
        dispatcher.HandleKeyEvent(SilkKey.Equal, 0, '=', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Shift = true;
        dispatcher.HandleKeyEvent(SilkKey.Equal, 0, '+', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Shift = false;
        dispatcher.HandleKeyEvent(SilkKey.Minus, 0, '-', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.Number0, 0, '0', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Ctrl = false;
        dispatcher.HandleKeyEvent(SilkKey.F11, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyEvent(SilkKey.Q, 0, 'Q', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal(1, host.FullscreenCount);
        Assert.Equal(2, host.ZoomInCount);
        Assert.Equal(1, host.ZoomOutCount);
        Assert.Equal(1, host.ResetZoomCount);
        Assert.Equal(1, host.QuitCount);
    }

    [Fact]
    public void HandleKeyEvent_AltGrTextIsAccepted_WhileCtrlAndAltAreRejected()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyEvent(SilkKey.F, 0, 'F', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Shift = false;

        host.Ctrl = false;
        host.Alt = true;
        dispatcher.HandleKeyEvent(SilkKey.O, 0, 'o', TerminalKeyEventType.Press, "ordinary-alt".AsSpan());
        host.Ctrl = true;
        host.AltGr = false;
        dispatcher.HandleKeyEvent(SilkKey.O, 0, 'o', TerminalKeyEventType.Press, "ordinary-ctrl-alt".AsSpan());
        host.AltGr = true;
        dispatcher.HandleKeyEvent(SilkKey.A, 0, 'a', TerminalKeyEventType.Press, "altgr".AsSpan());

        Assert.Equal("altgr", dispatcher.SearchQuery);
        Assert.Empty(host.InputBytes);
    }

    [Fact]
    public void SearchQuery_EditingUsesCursorAndWordDeletion()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyEvent(SilkKey.F, 0, 'F', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Ctrl = false;
        host.Shift = false;
        dispatcher.HandleKeyEvent(SilkKey.A, 0, 'a', TerminalKeyEventType.Press, "abc def".AsSpan());

        dispatcher.HandleKeyEvent(SilkKey.Home, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        for (int i = 0; i < 4; i++)
            dispatcher.HandleKeyEvent(SilkKey.Right, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.X, 0, 'X', TerminalKeyEventType.Press, "X".AsSpan());
        Assert.Equal("abc Xdef", dispatcher.SearchQuery);
        Assert.Equal(5, dispatcher.SearchCursor);

        dispatcher.HandleKeyEvent(SilkKey.Home, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.Delete, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        Assert.Equal("bc Xdef", dispatcher.SearchQuery);
        dispatcher.HandleKeyEvent(SilkKey.End, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.Backspace, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        Assert.Equal("bc Xde", dispatcher.SearchQuery);

        host.Ctrl = true;
        dispatcher.HandleKeyEvent(SilkKey.Backspace, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        Assert.Equal("bc ", dispatcher.SearchQuery);
        dispatcher.HandleKeyEvent(SilkKey.Home, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.Delete, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        Assert.Equal(" ", dispatcher.SearchQuery);
        dispatcher.HandleKeyEvent(SilkKey.Delete, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        Assert.Equal(string.Empty, dispatcher.SearchQuery);
    }

    [Fact]
    public void HandleKeyEvent_SearchAction_TogglesSearchState()
    {
        using var host = CreateHost();
        host.Ctrl = true;
        host.Shift = true;
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.F, 0, 'F', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.True(dispatcher.SearchActive);
        Assert.Equal(string.Empty, dispatcher.SearchQuery);
        Assert.Equal(-1, dispatcher.ActiveMatchIndex);
    }
    [Fact]
    public void ConsumedPress_RepeatAndReleaseDoNotRepeatAction_AndNextPressWorks()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;

        dispatcher.HandleKeyEvent(SilkKey.F, 44, 'F', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.F, 44, 'F', TerminalKeyEventType.Repeat, ReadOnlySpan<char>.Empty);
        Assert.True(dispatcher.SearchActive);
        dispatcher.HandleKeyEvent(SilkKey.F, 44, 'F', TerminalKeyEventType.Release, ReadOnlySpan<char>.Empty);
        Assert.True(dispatcher.SearchActive);

        dispatcher.HandleKeyEvent(SilkKey.F, 44, 'F', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.False(dispatcher.SearchActive);
    }

    [Fact]
    public void ReleaseOfConsumedKeyAfterAnotherKeyPress_DoesNotLeakKittyRelease()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;
        host.ActiveTab!.Session.Parser.Feed("\x1b[>31u"u8);

        dispatcher.HandleKeyEvent(SilkKey.C, 46, 'C', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.F3, 32, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        Assert.NotEmpty(host.InputBytes);
        int bytesAfterF3 = host.InputBytes.Count;
        dispatcher.HandleKeyEvent(SilkKey.C, 46, 'C', TerminalKeyEventType.Release, ReadOnlySpan<char>.Empty);

        Assert.Equal(1, host.CopyCount);
        Assert.Equal(bytesAfterF3, host.InputBytes.Count);
    }
    [Fact]
    public void NativeKeyPhasesKeepKittyTextInOneValidSequenceAndIncludeLockState()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.LockModifiers = TerminalKeyModifiers.CapsLock | TerminalKeyModifiers.NumLock;
        host.ActiveTab!.Session.Parser.Feed("\x1b[>31u"u8);

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Press, "a".AsSpan());
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Repeat, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Repeat, "a".AsSpan());
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Release, "a".AsSpan());

        Assert.Equal("\x1b[97;193:1;97u\x1b[97;193:2;97u\x1b[97;193:3u", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void HandleCommittedText_UsesKittyKeyZeroWhenAssociatedTextIsNegotiated()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.ActiveTab!.Session.Parser.Feed("\x1b[>31u"u8);

        dispatcher.HandleCommittedText("å".AsSpan());

        Assert.Equal("\x1b[0;;229u", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void KittyAlternateBaseLayoutIsReportedWithoutShiftForNonUsLayout()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.ActiveTab!.Session.Parser.Feed("\x1b[>12u"u8);

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'q', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal("\x1b[113::97;1u", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }


    [Fact]
    public void KittyNativeF25UsesOfficialProtocolCodepoint()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.ActiveTab!.Session.Parser.Feed("\x1b[>8u"u8);

        dispatcher.HandleKeyEvent(SilkKey.F25, 100, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal("\x1b[57388;1u", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void LegacyTypingUsesCommittedLayoutTextInsteadOfPhysicalEscapeToken()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.Escape, 1, 'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.Escape, 1, 'a', TerminalKeyEventType.Press, "a".AsSpan());

        Assert.Equal("a", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void LegacyCtrlTypingUsesCurrentLayoutPrimaryCodepoint()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'q', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal(new byte[] { 0x11 }, host.InputBytes.ToArray());
    }


    [Theory]
    [InlineData(true, false, "\x1b[97;5:1u")]
    [InlineData(false, true, "\x1b[97;3:1u")]
    public void KittyPrintableCtrlAndAltPressesAreEncodedImmediately(bool ctrl, bool alt, string expected)
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = ctrl;
        host.Alt = alt;
        host.ActiveTab!.Session.Parser.Feed("\x1b[>31u"u8);

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal(expected, Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void DeferredKittyPressRetainsModifierSnapshotWhenTextArrivesAfterModifierRelease()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Shift = true;
        host.ActiveTab!.Session.Parser.Feed("\x1b[>31u"u8);

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'A', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Shift = false;
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'A', TerminalKeyEventType.Press, "A".AsSpan());

        Assert.Equal("\x1b[65::97;2:1;65u", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }
    [Fact]
    public void KittyPressPreservesAdditionalCommittedScalarsThroughKeyZeroEvents()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.ActiveTab!.Session.Parser.Feed("\x1b[>24u"u8);

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Press, "a".AsSpan());
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Press, "\u0308".AsSpan());

        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Repeat, ReadOnlySpan<char>.Empty);
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Repeat, "a".AsSpan());
        dispatcher.HandleKeyEvent(SilkKey.A, 30, 'a', TerminalKeyEventType.Repeat, "\u0308".AsSpan());

        Assert.Equal("\x1b[97;1;97u\x1b[0;;776u\x1b[97;1;97u\x1b[0;;776u", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void HandleKeyEvent_CopyAction_UsesHostCallback()
    {
        using var host = CreateHost();
        host.Ctrl = true;
        host.Shift = true;
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.C, 0, 'C', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal(1, host.CopyCount);
    }

    [Fact]
    public void HandleKeyEvent_Escape_ClosesContextMenuBeforeTerminalInput()
    {
        using var host = CreateHost();
        host.ActiveContextMenu = new ContextMenuModel(0, 0, new[] { ContextMenuItem.Item("item", "Item", () => { }) });
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.Escape, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

    }

    [Fact]
    public void HandleKeyEvent_ShiftNavigation_ScrollsWhenMouseReportingDisabled()
    {
        using var host = CreateHost();
        host.Shift = true;
        var pane = host.ActiveTab!.ActivePane;
        CreateScrollback(pane, 2);
        pane.ScrollUp(1, pane.Session.Adapter.Buffer.ScrollbackCount);
        pane.ScrollToBottom();
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.Up, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal(1, pane.ScrollOffset);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 2)]
    public void HandleKeyEvent_ShiftPageUp_UsesActivePaneHeightWithMinimumStep(int paneRows, int expectedStep)
    {
        using var host = new FakeHost
        {
            ActiveTab = new TerminalTab(rows: 2, columns: 80)
        };
        host.Rows = 24;
        host.Shift = true;
        var pane = host.ActiveTab!.ActivePane;
        pane.Rows = paneRows;
        CreateScrollback(pane, 10);
        Assert.True(pane.Session.Adapter.Buffer.ScrollbackCount > 0);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.PageUp, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal(expectedStep, pane.ScrollOffset);
    }
    [Fact]
    public void ScrollOffsets_AreIndependentPerPane()
    {
        using var host = new FakeHost
        {
            ActiveTab = new TerminalTab(rows: 4, columns: 80)
        };
        var tab = host.ActiveTab;
        var first = tab!.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        CreateScrollback(first, 8);
        CreateScrollback(second, 8);

        first.ScrollUp(3, first.Session.Adapter.Buffer.ScrollbackCount);
        second.ScrollUp(5, second.Session.Adapter.Buffer.ScrollbackCount);

        tab.PaneTree.ActivePane = first;
        Assert.Equal(3, first.ScrollOffset);
        Assert.Equal(5, second.ScrollOffset);
        tab.PaneTree.ActivePane = second;
        Assert.Equal(3, first.ScrollOffset);
        Assert.Equal(5, second.ScrollOffset);
    }

    [Fact]
    public void HandleKeyEvent_ClearsActivePaneSelectionAndReturnsToBottom()
    {
        using var host = CreateHost();
        var pane = host.ActiveTab!.ActivePane;
        pane.Selection.StartSelection(0, 0);
        pane.ScrollUp(3, 10);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.X, 0, 'x', TerminalKeyEventType.Press, "x".AsSpan());

        Assert.False(pane.Selection.HasSelection);
        Assert.Equal(0, pane.ScrollOffset);
    }

    [Fact]
    public void HandleKeyEvent_ClearsOnlyActivePaneSelection()
    {
        using var host = CreateHost();
        var tab = host.ActiveTab!;
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        first.Selection.StartSelection(0, 0);
        second.Selection.StartSelection(0, 0);
        tab.PaneTree.ActivePane = second;
        using var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.X, 0, 'x', TerminalKeyEventType.Press, "x".AsSpan());

        Assert.True(first.Selection.HasSelection);
        Assert.False(second.Selection.HasSelection);
    }

    [Fact]
    public void HandleKeyEvent_WhenSearchActive_AppendsQueryWithoutTerminalInput()
    {
        using var host = CreateHost();
        var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyEvent(SilkKey.F, 0, 'F', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);
        host.Ctrl = false;
        host.Shift = false;

        dispatcher.HandleKeyEvent(SilkKey.S, 0, 's', TerminalKeyEventType.Press, "s".AsSpan());
        dispatcher.HandleKeyEvent(SilkKey.H, 0, 'h', TerminalKeyEventType.Press, "h".AsSpan());

        Assert.Equal("sh", dispatcher.SearchQuery);
    }

    [Fact]
    public void HandleKeyEvent_ApplicationCursorMode_WritesApplicationArrow()
    {
        using var host = CreateHost();
        host.ActiveTab!.Session.Parser.Feed("\x1b[?1h"u8);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.Up, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal("\x1bOA", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void HandleKeyEvent_KittyMode_WritesKittyArrow()
    {
        using var host = CreateHost();
        host.ActiveTab!.Session.Parser.Feed("\x1b[?1u"u8);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.Up, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal("\x1b[A", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void HandleKeyEvent_SuperModifier_UsesMetaModifier()
    {
        using var host = CreateHost();
        host.Super = true;
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyEvent(SilkKey.Up, 0, 0, TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty);

        Assert.Equal("\x1b[1;9A", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }
}
