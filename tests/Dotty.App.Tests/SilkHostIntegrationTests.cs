using System;
using System.IO;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Config;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using SilkKey = Silk.NET.Input.Key;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalTabManagerTests
{
    [Fact]
    public void CreateTab_InitializesSession_AndAddsToTabList()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
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
    public void CreateTab_PreservesWorkingDirectory()
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dotty-working-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            using (var manager = new TerminalTabManager(SilentPty.Create))
            {
                var tab = manager.CreateTab(
                    cols: 80,
                    rows: 24,
                    workingDirectory: workingDirectory);

                Assert.Equal(workingDirectory, tab.WorkingDirectory);
            }
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CloseBackgroundTabs_FromSnapshot_PreservesActiveTab()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var first = manager.CreateTab(cols: 80, rows: 24);
        var active = manager.CreateTab(cols: 80, rows: 24);
        var last = manager.CreateTab(cols: 80, rows: 24);
        manager.SelectTab(active);

        var snapshot = new List<TerminalTab>(manager.Tabs);
        foreach (var tab in snapshot)
        {
            if (!ReferenceEquals(tab, active))
                manager.CloseTab(tab);
        }
        manager.SelectTab(active);

        Assert.Single(manager.Tabs);
        Assert.Same(active, manager.ActiveTab);
        Assert.False(first.IsActive);
        Assert.True(active.IsActive);
        Assert.False(last.IsActive);
    }

    [Fact]
    public void CloseTab_UpdatesActiveIndex_AndDisposesSession()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
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
    public void CloseExitedPane_SplitLeafClosesOnlyThatLeaf()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tab = manager.CreateTab(cols: 80, rows: 24);
        var exitedLeaf = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Vertical);

        Assert.True(manager.CloseExitedPane(tab, exitedLeaf));
        Assert.Single(tab.PaneTree.Leaves);
        Assert.Single(manager.Tabs);
        Assert.Same(tab, manager.ActiveTab);
    }

    [Fact]
    public void CloseExitedPane_SoleLeafClosesTabAndClearsFinalActiveTab()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tab = manager.CreateTab(cols: 80, rows: 24);
        TerminalTab? lastActive = tab;
        manager.ActiveTabChanged += changed => lastActive = changed;

        Assert.True(manager.CloseExitedPane(tab, tab.ActivePane));
        Assert.Empty(manager.Tabs);
        Assert.Null(manager.ActiveTab);
        Assert.Null(lastActive);
    }

    [Fact]
    public void CloseExitedPane_StaleDuplicateExitIsNoOp()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tab = manager.CreateTab(cols: 80, rows: 24);
        var leaf = tab.ActivePane;

        Assert.True(manager.CloseExitedPane(tab, leaf));
        Assert.False(manager.CloseExitedPane(tab, leaf));
        Assert.Empty(manager.Tabs);
    }

    [Fact]
    public void CloseExitedPane_ClosesBackgroundOwnerWithoutChangingActiveTab()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var background = manager.CreateTab(cols: 80, rows: 24);
        var active = manager.CreateTab(cols: 80, rows: 24);
        manager.SelectTab(active);

        Assert.True(manager.CloseExitedPane(background, background.ActivePane));
        Assert.Single(manager.Tabs);
        Assert.Same(active, manager.ActiveTab);
    }

    [Fact]
    public void SplitAndClosePane_RaiseTopologyAndActivePaneChangesOnce()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tab = manager.CreateTab(cols: 80, rows: 24);
        int topologyChanges = 0;
        var activeChanges = new List<(LeafPane OldPane, LeafPane NewPane)>();
        tab.PaneTree.TopologyChanged += () => topologyChanges++;
        tab.PaneTree.ActivePaneChanged += (oldPane, newPane) =>
            activeChanges.Add((oldPane, newPane));

        var splitLeaf = tab.PaneTree.Split(tab.ActivePane, SplitDirection.Vertical);
        Assert.Equal(1, topologyChanges);
        Assert.Single(activeChanges);
        Assert.Same(tab.ActivePane, activeChanges[0].NewPane);

        Assert.True(manager.CloseExitedPane(tab, splitLeaf));
        Assert.Equal(2, topologyChanges);
        Assert.Equal(2, activeChanges.Count);
        Assert.Same(tab.ActivePane, activeChanges[1].NewPane);
    }

    [Fact]
    public void SelectNextTab_And_SelectPreviousTab_CycleThroughTabs()
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
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
        using var manager = new TerminalTabManager(SilentPty.Create);
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
        using var manager = new TerminalTabManager(SilentPty.Create);
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

public class LeafPaneSelectionOwnershipTests
{
    [Fact]
    public void LeafPanes_OwnIndependentSelections()
    {
        using var first = new LeafPane(rows: 2, columns: 8);
        using var second = new LeafPane(rows: 2, columns: 8);

        first.Selection.StartSelection(0, 0, SelectionMode.Character);
        first.Selection.UpdateSelection(0, 2);

        Assert.True(first.Selection.HasSelection);
        Assert.False(second.Selection.HasSelection);

        second.Selection.StartSelection(1, 1, SelectionMode.Character);
        second.Selection.UpdateSelection(1, 3);
        first.Selection.ClearSelection();

        Assert.False(first.Selection.HasSelection);
        Assert.True(second.Selection.HasSelection);
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

    [Fact]
    public void SelectWord_SelectsWordAndPunctuationRuns()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 24);
        buffer.WriteText("echo foo.bar".AsSpan(), CellAttributes.Default);
        var service = new TextSelectionService();

        service.SelectWord(buffer, row: 0, col: 6);
        Assert.Equal(SelectionMode.Word, service.Mode);
        Assert.Equal("foo", service.GetSelectedText(buffer));

        service.SelectWord(buffer, row: 0, col: 8);
        Assert.Equal(".", service.GetSelectedText(buffer));
    }

    [Fact]
    public void CharacterCopy_TrimsPaddingAndJoinsSoftWrappedRows()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 5);
        buffer.WriteText("ab  cd".AsSpan(), CellAttributes.Default);
        var service = new TextSelectionService();
        service.StartSelection(0, 0);
        service.UpdateSelection(1, 0);

        Assert.Equal("ab  cd", service.GetSelectedText(buffer));
    }

    [Fact]
    public void BlockCopy_PreservesRequestedCellWidth()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 5);
        buffer.SetCursor(0, 0);
        buffer.WriteText("x".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 0);
        buffer.WriteText("y".AsSpan(), CellAttributes.Default);
        var service = new TextSelectionService();
        service.StartSelection(0, 0, SelectionMode.Block);
        service.UpdateSelection(1, 3);

        // Copied text uses the platform line ending (CRLF on Windows clipboards).
        Assert.Equal("x   " + Environment.NewLine + "y   ", service.GetSelectedText(buffer));
    }
}

public class SilkKeyMapperTests
{
    [Fact]
    public void Map_TranslatesSilkKeyAndModifierFlags()
    {
        Assert.Equal(
            (TerminalKey.C, TerminalKeyModifiers.Control),
            SilkKeyMapper.Map(SilkKey.C, ctrl: true, shift: false, alt: false));
        Assert.Equal(
            (TerminalKey.Up, TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Meta),
            SilkKeyMapper.Map(SilkKey.Up, ctrl: false, shift: true, alt: true, super: true));
        Assert.Equal(
            (TerminalKey.KeypadAdd, TerminalKeyModifiers.None),
            SilkKeyMapper.Map(SilkKey.KeypadAdd, ctrl: false, shift: false, alt: false));
    }

    [Fact]
    public void Encode_UnknownSilkKey_ReturnsZeroWithoutWritingDestination()
    {
        Span<byte> destination = stackalloc byte[] { 0xAA, 0xBB };

        var length = SilkKeyMapper.Encode(
            (SilkKey)(-1),
            ctrl: false,
            shift: false,
            alt: false,
            keypadAppMode: false,
            destination);

        Assert.Equal(0, length);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, destination.ToArray());
    }
}

public class SilkConfigTests
{
    [Fact]
    public void LoadActiveTheme_Default_ReturnsDarkPlus()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DOTTY_THEME");
        var originalConfigHome = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
        string isolatedHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_THEME", null);
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", isolatedHome);
            Dotty.Runtime.Config.UserConfigService.Load();
            SilkConfig.ClearThemeCache();
            var theme = SilkConfig.LoadActiveTheme();

            Assert.NotNull(theme);
            Assert.Equal(DottyDefaults.DefaultColorScheme.Background, theme.Background);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTTY_THEME", originalEnv);
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", originalConfigHome);
            SilkConfig.ClearThemeCache();
        }
    }

    [Fact]
    public void GetActiveThemeName_RespectsEnvVariable()
    {
        var originalEnv = Environment.GetEnvironmentVariable("DOTTY_THEME");
        var originalConfigHome = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
        string isolatedHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", isolatedHome);
            Dotty.Runtime.Config.UserConfigService.Load();

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
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", originalConfigHome);
            SilkConfig.ClearThemeCache();
        }
    }
}
