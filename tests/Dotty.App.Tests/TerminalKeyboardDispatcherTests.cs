using System;
using System.Text;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Input;
using Xunit;
using SilkKey = Silk.NET.Input.Key;

namespace Dotty.App.Tests;

public sealed class TerminalKeyboardDispatcherTests
{
    private sealed class FakeHost : ITerminalKeyboardHost, IDisposable
    {
        public TerminalTabManager TabManager { get; } = new();
        public TerminalTab? ActiveTab { get; set; }
        public TextSelectionService SelectionService { get; } = new();
        public LuaScriptHost LuaHost { get; } = new();
        public KeybindingManager Keybindings { get; } = new();
        public ContextMenuModel? ActiveContextMenu { get; set; }
        public int Rows { get; set; } = 24;
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }
        public bool Super { get; set; }
        public int CopyCount { get; private set; }
        public int PasteCount { get; private set; }
        public int CreateTabCount { get; private set; }
        public int ClearCount { get; private set; }
        public List<byte> InputBytes { get; } = new();

        public void CopySelection() => CopyCount++;
        public void PasteClipboard() => PasteCount++;
        public void CreateTab(TerminalTab activeTab) => CreateTabCount++;
        public void ClearTerminal(TerminalTab activeTab) => ClearCount++;
        public void WriteInput(TerminalTab activeTab, byte[] bytes) => InputBytes.AddRange(bytes);

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

        Assert.Null(host.ActiveContextMenu);
    }

    [Fact]
    public void HandleKeyDown_ShiftNavigation_ScrollsWhenMouseReportingDisabled()
    {
        using var host = CreateHost();
        host.Shift = true;
        host.ActiveTab!.Session.Adapter.Buffer.ScrollUpLines(2);
        host.ActiveTab.ScrollUp(1, host.ActiveTab.Session.Adapter.Buffer.ScrollbackCount);
        host.ActiveTab.ScrollToBottom();
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyDown(SilkKey.Up, 0);

        Assert.Equal(1, host.ActiveTab.ScrollOffset);
    }

    [Fact]
    public void HandleKeyChar_ClearsSelectionAndReturnsToBottom()
    {
        using var host = CreateHost();
        host.SelectionService.StartSelection(0, 0);
        host.ActiveTab!.ScrollUp(3, 10);
        var dispatcher = new TerminalKeyboardDispatcher(host);

        dispatcher.HandleKeyChar('x');

        Assert.False(host.SelectionService.HasSelection);
        Assert.Equal(0, host.ActiveTab.ScrollOffset);
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

        Assert.Equal("\x1b[1:", Encoding.ASCII.GetString(host.InputBytes.ToArray()));
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
