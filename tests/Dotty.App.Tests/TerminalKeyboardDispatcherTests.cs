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
    public void HandleKeyDown_DefaultWindowActions_DispatchExactlyOnce()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);

        host.Ctrl = true;
        dispatcher.HandleKeyDown(SilkKey.Equal, 0);
        host.Shift = true;
        dispatcher.HandleKeyDown(SilkKey.Equal, 0);
        host.Shift = false;
        dispatcher.HandleKeyDown(SilkKey.Minus, 0);
        dispatcher.HandleKeyDown(SilkKey.Number0, 0);
        host.Ctrl = false;
        dispatcher.HandleKeyDown(SilkKey.F11, 0);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyDown(SilkKey.Q, 0);

        Assert.Equal(1, host.FullscreenCount);
        Assert.Equal(2, host.ZoomInCount);
        Assert.Equal(1, host.ZoomOutCount);
        Assert.Equal(1, host.ResetZoomCount);
        Assert.Equal(1, host.QuitCount);
    }

    [Fact]
    public void HandleText_AltGrIsAccepted_WhileCtrlAndAltAreRejected()
    {
        using var host = CreateHost();
        using var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyDown(SilkKey.F, 0);
        host.Shift = false;

        host.Ctrl = false;
        host.Alt = true;
        dispatcher.HandleText("ordinary-alt");
        host.Ctrl = true;
        host.AltGr = false;
        dispatcher.HandleText("ordinary-ctrl-alt");
        host.AltGr = true;
        dispatcher.HandleText("altgr");

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
        dispatcher.HandleKeyDown(SilkKey.F, 0);
        host.Ctrl = false;
        host.Shift = false;
        dispatcher.HandleText("abc def");

        dispatcher.HandleKeyDown(SilkKey.Home, 0);
        for (int i = 0; i < 4; i++)
            dispatcher.HandleKeyDown(SilkKey.Right, 0);
        dispatcher.HandleText("X");
        Assert.Equal("abc Xdef", dispatcher.SearchQuery);
        Assert.Equal(5, dispatcher.SearchCursor);

        dispatcher.HandleKeyDown(SilkKey.Home, 0);
        dispatcher.HandleKeyDown(SilkKey.Delete, 0);
        Assert.Equal("bc Xdef", dispatcher.SearchQuery);
        dispatcher.HandleKeyDown(SilkKey.End, 0);
        dispatcher.HandleKeyDown(SilkKey.Backspace, 0);
        Assert.Equal("bc Xde", dispatcher.SearchQuery);

        host.Ctrl = true;
        dispatcher.HandleKeyDown(SilkKey.Backspace, 0);
        Assert.Equal("bc ", dispatcher.SearchQuery);
        dispatcher.HandleKeyDown(SilkKey.Home, 0);
        dispatcher.HandleKeyDown(SilkKey.Delete, 0);
        Assert.Equal(" ", dispatcher.SearchQuery);
        dispatcher.HandleKeyDown(SilkKey.Delete, 0);
        Assert.Equal(string.Empty, dispatcher.SearchQuery);
    }

    [Fact]
    public void HandleKeyDown_SearchAction_TogglesSearchState()
    {
        using var host = CreateHost();
        host.Ctrl = true;
        host.Shift = true;
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.F, 0);

        Assert.True(dispatcher.SearchActive);
        Assert.Equal(string.Empty, dispatcher.SearchQuery);
        Assert.Equal(-1, dispatcher.ActiveMatchIndex);
    }

    [Fact]
    public void HandleKeyDown_CopyAction_UsesHostCallback()
    {
        using var host = CreateHost();
        host.Ctrl = true;
        host.Shift = true;
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.C, 0);

        Assert.Equal(1, host.CopyCount);
    }

    [Fact]
    public void HandleKeyDown_Escape_ClosesContextMenuBeforeTerminalInput()
    {
        using var host = CreateHost();
        host.ActiveContextMenu = new ContextMenuModel(0, 0, new[] { ContextMenuItem.Item("item", "Item", () => { }) });
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.Escape, 0);

    }

    [Fact]
    public void HandleKeyDown_ShiftNavigation_ScrollsWhenMouseReportingDisabled()
    {
        using var host = CreateHost();
        host.Shift = true;
        var pane = host.ActiveTab!.ActivePane;
        CreateScrollback(pane, 2);
        pane.ScrollUp(1, pane.Session.Adapter.Buffer.ScrollbackCount);
        pane.ScrollToBottom();
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.Up, 0);

        Assert.Equal(1, pane.ScrollOffset);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 2)]
    public void HandleKeyDown_ShiftPageUp_UsesActivePaneHeightWithMinimumStep(int paneRows, int expectedStep)
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

        dispatcher.HandleKeyDown(SilkKey.PageUp, 0);

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
    public void HandleKeyChar_ClearsActivePaneSelectionAndReturnsToBottom()
    {
        using var host = CreateHost();
        var pane = host.ActiveTab!.ActivePane;
        pane.Selection.StartSelection(0, 0);
        pane.ScrollUp(3, 10);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyChar('x');

        Assert.False(pane.Selection.HasSelection);
        Assert.Equal(0, pane.ScrollOffset);
    }

    [Fact]
    public void HandleKeyChar_ClearsOnlyActivePaneSelection()
    {
        using var host = CreateHost();
        var tab = host.ActiveTab!;
        var first = tab.ActivePane;
        var second = tab.PaneTree.Split(first, SplitDirection.Vertical);
        first.Selection.StartSelection(0, 0);
        second.Selection.StartSelection(0, 0);
        tab.PaneTree.ActivePane = second;
        using var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyChar('x');

        Assert.True(first.Selection.HasSelection);
        Assert.False(second.Selection.HasSelection);
    }

    [Fact]
    public void HandleKeyChar_WhenSearchActive_AppendsQueryWithoutTerminalInput()
    {
        using var host = CreateHost();
        var dispatcher = new TerminalKeyboardDispatcher(host);
        host.Ctrl = true;
        host.Shift = true;
        dispatcher.HandleKeyDown(SilkKey.F, 0);
        host.Ctrl = false;
        host.Shift = false;

        dispatcher.HandleKeyChar('s');
        dispatcher.HandleKeyChar('h');

        Assert.Equal("sh", dispatcher.SearchQuery);
    }

    [Fact]
    public void HandleKeyDown_ApplicationCursorMode_WritesApplicationArrow()
    {
        using var host = CreateHost();
        host.ActiveTab!.Session.Parser.Feed("\x1b[?1h"u8);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.Up, 0);

        Assert.Equal("\x1bOA", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void HandleKeyDown_KittyMode_WritesKittyArrow()
    {
        using var host = CreateHost();
        host.ActiveTab!.Session.Parser.Feed("\x1b[?1u"u8);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.Up, 0);

        Assert.Equal("\x1b[A", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }

    [Fact]
    public void HandleKeyDown_SuperModifier_UsesMetaModifier()
    {
        using var host = CreateHost();
        host.Super = true;
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.Up, 0);

        Assert.Equal("\x1b[1;9A", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
    }
}
