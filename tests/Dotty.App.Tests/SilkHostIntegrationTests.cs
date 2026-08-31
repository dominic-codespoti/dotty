using System;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Runtime.Input;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk;
using Dotty.Silk.Config;
using Dotty.Terminal.Adapter;
using SilkKey = Silk.NET.Input.Key;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalTabManagerTests
{
    [Fact]
    public void CreateTab_InitializesSession_AndAddsToTabList()
    {
        using var manager = new TerminalTabManager();
        TerminalTab? addedTab = null;
        manager.TabAdded += tab => addedTab = tab;

        var tab = manager.CreateTab(cols: 100, rows: 30);

        Assert.NotNull(tab);
        Assert.Same(tab, addedTab);
        Assert.Single(manager.Tabs);
        Assert.Same(tab, manager.Tabs[0]);
        Assert.Equal(1, manager.Count);
        Assert.Equal(0, manager.ActiveIndex);
        Assert.Same(tab, manager.ActiveTab);
        Assert.True(tab.IsActive);
        Assert.NotNull(tab.Session);
        Assert.Equal(100, tab.Session.Adapter.Buffer.Columns);
        Assert.Equal(30, tab.Session.Adapter.Buffer.Rows);
    }

    [Fact]
    public void CloseTab_UpdatesActiveIndex_AndDisposesSession()
    {
        using var manager = new TerminalTabManager();
        var tab0 = manager.CreateTab(cols: 80, rows: 24);
        var tab1 = manager.CreateTab(cols: 80, rows: 24);
        var tab2 = manager.CreateTab(cols: 80, rows: 24);

        Assert.Equal(3, manager.Count);
        Assert.Equal(2, manager.ActiveIndex);
        Assert.Same(tab2, manager.ActiveTab);

        // Close active tab (tab2) -> active index should shift to tab1 (index 1)
        var closedTab2 = manager.CloseTab(tab2);
        Assert.True(closedTab2);
        Assert.Equal(2, manager.Count);
        Assert.Equal(1, manager.ActiveIndex);
        Assert.Same(tab1, manager.ActiveTab);
        Assert.True(tab1.IsActive);

        // Close middle/active tab (tab1) -> active index should shift to tab0 (index 0)
        var closedTab1 = manager.CloseTabAt(1);
        Assert.True(closedTab1);
        Assert.Single(manager.Tabs);
        Assert.Equal(0, manager.ActiveIndex);
        Assert.Same(tab0, manager.ActiveTab);

        // Close final tab -> manager should have no active tab
        var closedTab0 = manager.CloseTab(tab0);
        Assert.True(closedTab0);
        Assert.Equal(0, manager.Count);
        Assert.Equal(-1, manager.ActiveIndex);
        Assert.Null(manager.ActiveTab);
    }

    [Fact]
    public void SelectNextTab_And_SelectPreviousTab_CycleThroughTabs()
    {
        using var manager = new TerminalTabManager();
        var tab0 = manager.CreateTab(cols: 80, rows: 24);
        var tab1 = manager.CreateTab(cols: 80, rows: 24);
        var tab2 = manager.CreateTab(cols: 80, rows: 24);

        manager.SelectTab(0);
        Assert.Equal(0, manager.ActiveIndex);
        Assert.Same(tab0, manager.ActiveTab);

        // Cycle forward
        manager.SelectNextTab();
        Assert.Equal(1, manager.ActiveIndex);
        Assert.Same(tab1, manager.ActiveTab);

        manager.SelectNextTab();
        Assert.Equal(2, manager.ActiveIndex);
        Assert.Same(tab2, manager.ActiveTab);

        // Wrap around to start
        manager.SelectNextTab();
        Assert.Equal(0, manager.ActiveIndex);
        Assert.Same(tab0, manager.ActiveTab);

        // Cycle backward (wrap around to end)
        manager.SelectPreviousTab();
        Assert.Equal(2, manager.ActiveIndex);
        Assert.Same(tab2, manager.ActiveTab);

        manager.SelectPreviousTab();
        Assert.Equal(1, manager.ActiveIndex);
        Assert.Same(tab1, manager.ActiveTab);
    }

    [Fact]
    public void ResizeAll_ResizesAllTabSessions()
    {
        using var manager = new TerminalTabManager();
        var tab0 = manager.CreateTab(cols: 80, rows: 24);
        var tab1 = manager.CreateTab(cols: 80, rows: 24);

        manager.ResizeAll(120, 40);

        Assert.Equal(120, tab0.Session.Adapter.Buffer.Columns);
        Assert.Equal(40, tab0.Session.Adapter.Buffer.Rows);
        Assert.Equal(120, tab1.Session.Adapter.Buffer.Columns);
        Assert.Equal(40, tab1.Session.Adapter.Buffer.Rows);
    }

    [Fact]
    public void TabTitleChanged_FiresWhenSessionTitleChanges()
    {
        using var manager = new TerminalTabManager();
        var tab = manager.CreateTab(cols: 80, rows: 24);

        TerminalTab? reportedTab = null;
        string? reportedTitle = null;
        manager.TabTitleChanged += (t, title) =>
        {
            reportedTab = t;
            reportedTitle = title;
        };

        // Feed OSC 0 / 2 title sequence into session adapter
        var titleSeq = "\x1b]0;My Custom Tab Title\x07"u8.ToArray();
        tab.Session.Parser.Feed(titleSeq);

        Assert.Same(tab, reportedTab);
        Assert.Equal("My Custom Tab Title", reportedTitle);
        Assert.Equal("My Custom Tab Title", tab.Title);
    }
}

public class TextSelectionServiceTests
{
    [Fact]
    public void CharacterSelection_ProducesNormalizedRange()
    {
        var service = new TextSelectionService();
        service.StartSelection(row: 5, col: 10, SelectionMode.Character);
        service.UpdateSelection(row: 2, col: 4);

        Assert.True(service.HasSelection);
        Assert.Equal(SelectionMode.Character, service.Mode);

        var range = service.GetNormalizedRange();
        Assert.False(range.IsEmpty);
        Assert.Equal(2, range.StartRow);
        Assert.Equal(4, range.StartColumn);
        Assert.Equal(5, range.EndRow);
        Assert.Equal(10, range.EndColumn);
    }

    [Fact]
    public void BlockSelection_ProducesBoxRange()
    {
        var service = new TextSelectionService();
        service.StartSelection(row: 5, col: 20, SelectionMode.Block);
        service.UpdateSelection(row: 2, col: 10);

        Assert.True(service.HasSelection);
        Assert.Equal(SelectionMode.Block, service.Mode);

        var range = service.GetNormalizedRange();
        Assert.False(range.IsEmpty);
        Assert.Equal(2, range.StartRow);
        Assert.Equal(10, range.StartColumn);
        Assert.Equal(5, range.EndRow);
        Assert.Equal(20, range.EndColumn);
    }

    [Fact]
    public void GetSelectedText_ExtractsCharactersFromBuffer()
    {
        var buffer = new TerminalBuffer(rows: 5, columns: 20);
        buffer.SetCursor(0, 0);
        buffer.WriteText("Hello World".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("Second Line".AsSpan(), CellAttributes.Default);

        var service = new TextSelectionService();
        service.StartSelection(row: 0, col: 6, SelectionMode.Character);
        service.UpdateSelection(row: 0, col: 10);

        var selected = service.GetSelectedText(buffer);
        Assert.Equal("World", selected);
    }
    [Fact]
    public void SelectLine_SelectsEntireRowFromFirstToLastColumn()
    {
        var service = new TextSelectionService();
        service.SelectLine(row: 3, totalColumns: 80);

        Assert.True(service.HasSelection);
        Assert.Equal(SelectionMode.Line, service.Mode);

        var range = service.GetNormalizedRange();
        Assert.Equal(3, range.StartRow);
        Assert.Equal(0, range.StartColumn);
        Assert.Equal(3, range.EndRow);
        Assert.Equal(79, range.EndColumn);

        Assert.True(service.IsCellSelected(3, 0));
        Assert.True(service.IsCellSelected(3, 40));
        Assert.True(service.IsCellSelected(3, 79));
        Assert.False(service.IsCellSelected(2, 40));
        Assert.False(service.IsCellSelected(4, 40));
    }

    [Fact]
    public void UpdateLineSelection_DraggingDownwards_SpansMultipleFullRows()
    {
        var service = new TextSelectionService();
        service.SelectLine(row: 2, totalColumns: 80);
        service.UpdateLineSelection(row: 4, totalColumns: 80);

        var range = service.GetNormalizedRange();
        Assert.Equal(2, range.StartRow);
        Assert.Equal(0, range.StartColumn);
        Assert.Equal(4, range.EndRow);
        Assert.Equal(79, range.EndColumn);

        Assert.True(service.IsCellSelected(2, 0));
        Assert.True(service.IsCellSelected(3, 50));
        Assert.True(service.IsCellSelected(4, 79));
        Assert.False(service.IsCellSelected(1, 0));
        Assert.False(service.IsCellSelected(5, 0));
    }

    [Fact]
    public void IsCellSelected_CorrectlyIdentifiesCells()
    {
        var service = new TextSelectionService();
        // Character mode test
        service.StartSelection(row: 1, col: 5, SelectionMode.Character);
        service.UpdateSelection(row: 2, col: 10);

        Assert.False(service.IsCellSelected(0, 5));
        Assert.False(service.IsCellSelected(1, 4));
        Assert.True(service.IsCellSelected(1, 5));
        Assert.True(service.IsCellSelected(1, 15));
        Assert.True(service.IsCellSelected(2, 0));
        Assert.True(service.IsCellSelected(2, 10));
        Assert.False(service.IsCellSelected(2, 11));
        Assert.False(service.IsCellSelected(3, 0));

        // Block mode test
        service.StartSelection(row: 1, col: 5, SelectionMode.Block);
        service.UpdateSelection(row: 3, col: 10);

        Assert.True(service.IsCellSelected(1, 5));
        Assert.True(service.IsCellSelected(2, 8));
        Assert.True(service.IsCellSelected(3, 10));
        Assert.False(service.IsCellSelected(1, 11));
        Assert.False(service.IsCellSelected(2, 4));
        Assert.False(service.IsCellSelected(4, 8));
    }
}

public class SilkKeyMapperTests
{
    [Fact]
    public void Map_Letters_WithControl_EncodesControlBytes()
    {
        // Ctrl+C -> 0x03 (ETX)
        var ctrlC = SilkKeyMapper.Encode(SilkKey.C, ctrl: true, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(ctrlC);
        Assert.Equal(new byte[] { 0x03 }, ctrlC);

        // Ctrl+A -> 0x01 (SOH)
        var ctrlA = SilkKeyMapper.Encode(SilkKey.A, ctrl: true, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(ctrlA);
        Assert.Equal(new byte[] { 0x01 }, ctrlA);

        // Ctrl+Z -> 0x1A (SUB)
        var ctrlZ = SilkKeyMapper.Encode(SilkKey.Z, ctrl: true, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(ctrlZ);
        Assert.Equal(new byte[] { 0x1A }, ctrlZ);
    }

    [Fact]
    public void Map_Arrows_EncodesXtermSequences()
    {
        // Plain Up -> \e[A
        var up = SilkKeyMapper.Encode(SilkKey.Up, ctrl: false, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(up);
        Assert.Equal("\x1b[A", Encoding.UTF8.GetString(up!));

        // Plain Down -> \e[B
        var down = SilkKeyMapper.Encode(SilkKey.Down, ctrl: false, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(down);
        Assert.Equal("\x1b[B", Encoding.UTF8.GetString(down!));

        // Plain Right -> \e[C
        var right = SilkKeyMapper.Encode(SilkKey.Right, ctrl: false, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(right);
        Assert.Equal("\x1b[C", Encoding.UTF8.GetString(right!));

        // Plain Left -> \e[D
        var left = SilkKeyMapper.Encode(SilkKey.Left, ctrl: false, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(left);
        Assert.Equal("\x1b[D", Encoding.UTF8.GetString(left!));

        // Shift+Up -> \e[1;2A
        var shiftUp = SilkKeyMapper.Encode(SilkKey.Up, ctrl: false, shift: true, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(shiftUp);
        Assert.Equal("\x1b[1;2A", Encoding.UTF8.GetString(shiftUp!));

        // Ctrl+Up -> \e[1;5A
        var ctrlUp = SilkKeyMapper.Encode(SilkKey.Up, ctrl: true, shift: false, alt: false, keypadAppMode: false, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(ctrlUp);
        Assert.Equal("\x1b[1;5A", Encoding.UTF8.GetString(ctrlUp!));
    }

    [Fact]
    public void Map_Keypad_EncodesApplicationSequences()
    {
        // Keypad 0 in application mode -> \eOp
        var kp0 = SilkKeyMapper.Encode(SilkKey.Keypad0, ctrl: false, shift: false, alt: false, keypadAppMode: true, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(kp0);
        Assert.Equal("\x1bOp", Encoding.UTF8.GetString(kp0!));

        // Keypad 5 in application mode -> \eOu
        var kp5 = SilkKeyMapper.Encode(SilkKey.Keypad5, ctrl: false, shift: false, alt: false, keypadAppMode: true, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(kp5);
        Assert.Equal("\x1bOu", Encoding.UTF8.GetString(kp5!));

        // Keypad Enter in application mode -> \eOM (or standard enter if not mapped specifically in application mode)
        var kpAdd = SilkKeyMapper.Encode(SilkKey.KeypadAdd, ctrl: false, shift: false, alt: false, keypadAppMode: true, kittyMode: 0, super: false, applicationCursorKeys: false);
        Assert.NotNull(kpAdd);
        Assert.Equal("\x1bOm", Encoding.UTF8.GetString(kpAdd!));
    }
}

public class SilkConfigTests
{
    [Fact]
    public void LoadActiveTheme_Default_ReturnsDarkPlus()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DOTTY_THEME");
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_THEME", null);
            var theme = SilkConfig.LoadActiveTheme();

            Assert.NotNull(theme);
            Assert.Equal(DottyDefaults.DefaultColorScheme.Background, theme.Background);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTTY_THEME", originalEnv);
        }
    }

    [Fact]
    public void GetActiveThemeName_RespectsEnvVariable()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DOTTY_THEME");
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_THEME", "Dracula");
            var name = SilkConfig.GetActiveThemeName();
            Assert.Equal("Dracula", name);

            Environment.SetEnvironmentVariable("DOTTY_THEME", "  SolarizedDark  ");
            var trimmedName = SilkConfig.GetActiveThemeName();
            Assert.Equal("SolarizedDark", trimmedName);

            Environment.SetEnvironmentVariable("DOTTY_THEME", "");
            var defaultName = SilkConfig.GetActiveThemeName();
            Assert.Equal(DottyDefaults.DefaultThemeName, defaultName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTTY_THEME", originalEnv);
        }
    }
}
