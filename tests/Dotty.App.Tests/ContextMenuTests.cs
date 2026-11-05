using System;
using System.Collections.Generic;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Prompt;
using Xunit;

namespace Dotty.App.Tests;

public class ContextMenuTests
{
    [Fact]
    public void ContextMenuLayout_ComputesCorrectMenuDimensions()
    {
        var items = new List<ContextMenuItem>
        {
            new("split-v", "Split Right", "Ctrl+Shift+D", null),
            new("split-h", "Split Down", "Ctrl+Shift+S", null),
            ContextMenuItem.Separator(),
            new("close", "Close", "Ctrl+Shift+W", null)
        };

        var model = new ContextMenuModel(x: 100f, y: 50f, items: items);
        var layout = ContextMenuLayout.Calculate(
            model: model,
            viewportWidth: 1000f,
            viewportHeight: 600f);

        Assert.NotNull(layout);
        Assert.Equal(4, layout.Items.Length);
        Assert.True(layout.Bounds.Width > 120f);
        Assert.True(layout.Bounds.Height > 80f);
        Assert.Equal(100f, layout.Bounds.X);
        Assert.Equal(50f, layout.Bounds.Y);
    }

    [Fact]
    public void ContextMenuLayout_AlignsShortcutsAndReservesSharedGap()
    {
        var items = new List<ContextMenuItem>
        {
            new("long", "A longer label", "Ctrl+Shift+E", null, icon: "◫"),
            new("short", "Short", "X", null, icon: "◫")
        };

        var model = new ContextMenuModel(x: 100f, y: 50f, items: items);
        var layout = ContextMenuLayout.Calculate(
            model: model,
            viewportWidth: 1000f,
            viewportHeight: 600f);

        var longestShortcut = layout.Items[0];
        var shorterShortcut = layout.Items[1];

        Assert.Equal(longestShortcut.ShortcutBounds.Right, shorterShortcut.ShortcutBounds.Right);
        Assert.Equal(
            longestShortcut.ShortcutBounds.Left - ContextMenuLayout.DefaultShortcutGap,
            longestShortcut.LabelBounds.Right);
        Assert.True(shorterShortcut.LabelBounds.Right < shorterShortcut.ShortcutBounds.Left);
        Assert.True(
            longestShortcut.Bounds.Right - longestShortcut.ShortcutBounds.Right >=
            ContextMenuLayout.DefaultTrailingTextInset);
    }

    [Fact]
    public void ContextMenuHitTester_ClickingItem_ReturnsItemIndex()
    {
        var items = new List<ContextMenuItem>
        {
            new("copy", "Copy", "Ctrl+Shift+C", null),
            new("paste", "Paste", "Ctrl+Shift+V", null)
        };

        var model = new ContextMenuModel(x: 100f, y: 50f, items: items);
        var layout = ContextMenuLayout.Calculate(
            model: model,
            viewportWidth: 1000f,
            viewportHeight: 600f);

        int hitIndex = ContextMenuHitTester.HitTest(layout, x: 120f, y: 65f);
        Assert.Equal(0, hitIndex);

        int outsideIndex = ContextMenuHitTester.HitTest(layout, x: 500f, y: 500f);
        Assert.Equal(-1, outsideIndex);
    }

    [Fact]
    public void DefaultContextMenus_BuildTabMenu_HasExpectedActions()
    {
        var splitRight = false;
        var menu = DefaultContextMenus.BuildTabMenu(
            onSplitRight: () => splitRight = true,
            onSplitDown: () => { },
            onNewTab: () => { },
            onClose: () => { });

        Assert.NotNull(menu);
        Assert.True(menu.Count >= 4);

        // Trigger Split Right action
        menu[0].Action?.Invoke();
        Assert.True(splitRight);
    }

    [Fact]
    public void ContextMenuModel_KeyboardFocusSkipsUnavailableRowsAndWraps()
    {
        bool invoked = false;
        var model = new ContextMenuModel(items: new[]
        {
            ContextMenuItem.Item("disabled", "Disabled", () => { }, isDisabled: true),
            ContextMenuItem.Separator(),
            ContextMenuItem.Item("first", "First", () => { }),
            ContextMenuItem.Item("last", "Last", () => invoked = true)
        });

        Assert.True(model.MoveFocus(1));
        Assert.Equal(2, model.HoveredIndex);
        Assert.True(model.MoveFocus(-1));
        Assert.Equal(3, model.HoveredIndex);
        Assert.True(model.ExecuteFocused());
        Assert.True(invoked);
        Assert.False(model.IsVisible);
    }

    [Fact]
    public void Close_WithoutExecution_FiresDismissedOnce()
    {
        int dismissed = 0;
        var menu = new ContextMenuModel(0f, 0f, new[] { new ContextMenuItem("a", "A", action: () => { }) })
        {
            Dismissed = () => dismissed++
        };

        menu.Close();
        menu.Close();

        Assert.Equal(1, dismissed);
        Assert.False(menu.IsVisible);
    }

    [Fact]
    public void ExecuteHovered_DoesNotFireDismissed()
    {
        int dismissed = 0;
        int ran = 0;
        var menu = new ContextMenuModel(0f, 0f, new[] { new ContextMenuItem("a", "A", action: () => ran++) })
        {
            Dismissed = () => dismissed++,
            HoveredIndex = 0
        };

        Assert.True(menu.ExecuteHovered());
        menu.Close();

        Assert.Equal(1, ran);
        Assert.Equal(0, dismissed);
    }

    [Fact]
    public void Close_WhenAlreadyHidden_DoesNotFireDismissed()
    {
        int dismissed = 0;
        var menu = new ContextMenuModel();
        menu.Dismissed = () => dismissed++;

        menu.Close();

        Assert.Equal(0, dismissed);
    }

    [Fact]
    public void Open_AfterExecution_ReArmsDismissed()
    {
        int dismissed = 0;
        var items = new[] { new ContextMenuItem("a", "A", action: () => { }) };
        var menu = new ContextMenuModel(0f, 0f, items) { HoveredIndex = 0 };
        menu.ExecuteHovered();

        menu.Open(1f, 1f, items);
        menu.Dismissed = () => dismissed++;
        menu.Close();

        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void HostPrompt_Build_LaysOutRowsAndFocusesInitialAction()
    {
        string? chosen = null;
        var menu = HostPrompt.Build(
            "Paste?",
            new[] { "line one", "line two" },
            new (string, Action)[] { ("Paste", () => chosen = "paste"), ("Cancel", () => chosen = "cancel") },
            initialFocus: 1,
            onDismissed: null);

        // title, sep, 2 lines, sep, 2 actions
        Assert.Equal(7, menu.Items.Count);
        Assert.True(menu.Items[0].IsDisabled);
        Assert.True(menu.Items[1].IsSeparator);
        Assert.True(menu.Items[2].IsDisabled);
        Assert.True(menu.Items[4].IsSeparator);
        Assert.Equal(6, menu.HoveredIndex);
        Assert.True(menu.IsVisible);

        Assert.True(menu.ExecuteFocused());
        Assert.Equal("cancel", chosen);
    }

    [Fact]
    public void HostPrompt_Build_NavigationSkipsDisabledRows()
    {
        var menu = HostPrompt.Build(
            "T",
            new[] { "x" },
            new (string, Action)[] { ("Yes", () => { }), ("No", () => { }) },
            initialFocus: 1,
            onDismissed: null);

        Assert.True(menu.MoveFocus(-1));
        Assert.Equal("Yes", menu.Items[menu.HoveredIndex].Label);
        Assert.True(menu.MoveFocus(-1));
        Assert.Equal("No", menu.Items[menu.HoveredIndex].Label);
    }

    [Fact]
    public void HostPrompt_Build_DismissedFiresOnCloseButNotOnAction()
    {
        int dismissed = 0;
        var closed = HostPrompt.Build("T", Array.Empty<string>(),
            new (string, Action)[] { ("No", () => { }) }, 0, () => dismissed++);
        closed.Close();
        Assert.Equal(1, dismissed);

        var executed = HostPrompt.Build("T", Array.Empty<string>(),
            new (string, Action)[] { ("No", () => { }) }, 0, () => dismissed++);
        executed.ExecuteFocused();
        Assert.Equal(1, dismissed);
    }

    [Fact]
    public void HostPrompt_Build_ClampsLinesAndSanitizesText()
    {
        var lines = new List<string>();
        for (int i = 0; i < 20; i++)
            lines.Add("l" + i);
        lines[0] = "evil\u001b[2Jtext";

        var menu = HostPrompt.Build(new string('t', 200), lines,
            new (string, Action)[] { ("Ok", () => { }) }, 0, null);

        // title + sep + 8 lines + sep + 1 action
        Assert.Equal(1 + 1 + HostPrompt.MaxLines + 1 + 1, menu.Items.Count);
        Assert.True(menu.Items[0].Label.Length <= HostPrompt.MaxLineChars);
        Assert.DoesNotContain('\u001b', menu.Items[2].Label);
    }

    [Fact]
    public void HostPrompt_BuildNotice_HasSingleFocusedDismissRow()
    {
        int dismissed = 0;
        var menu = HostPrompt.BuildNotice("Note", new[] { "something happened" }, () => dismissed++);

        var focused = menu.Items[menu.HoveredIndex];
        Assert.Equal("Dismiss", focused.Label);
        Assert.Equal(menu.Items.Count - 1, menu.HoveredIndex);

        Assert.True(menu.ExecuteFocused());
        Assert.Equal(0, dismissed);
    }

    [Fact]
    public void HostPrompt_SanitizeLine_EscapesControlsAndCollapsesBreaks()
    {
        string result = HostPrompt.SanitizeLine("a\u001b[31m\u0000b\u007f\u009bc\r\n\td", 100);

        Assert.Equal("a\\x1B[31m\\x00b\\x7F\\x9Bc d", result);
        foreach (char c in result)
            Assert.False(c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F));
    }

    [Fact]
    public void HostPrompt_SanitizeLine_EscapesBidiOverrides()
    {
        string result = HostPrompt.SanitizeLine("a\u202Eb", 100);

        Assert.Equal("a\\u{202E}b", result);
    }

    [Fact]
    public void HostPrompt_SanitizeLine_TruncatesWithEllipsis()
    {
        string result = HostPrompt.SanitizeLine(new string('x', 50), 10);

        Assert.Equal(10, result.Length);
        Assert.EndsWith("\u2026", result);
        Assert.Equal(string.Empty, HostPrompt.SanitizeLine(null, 10));
        Assert.Equal("short", HostPrompt.SanitizeLine("short", 5));
    }

    [Fact]
    public void HostPrompt_SanitizeLine_DoesNotSplitEscapeOrSurrogatePairOnTruncation()
    {
        string escaped = HostPrompt.SanitizeLine("ab\u001bcdef", 5);
        Assert.Equal("ab\u2026", escaped);

        string pair = HostPrompt.SanitizeLine("abc\U0001F600def", 5);
        Assert.Equal("abc\u2026", pair);
    }

    [Fact]
    public void HostPrompt_SanitizeLine_TrimsOnlyAnEscapeCutByTheEllipsisReserve()
    {
        // "ab" + "\x1B" fills the 6-char budget exactly; reserving a char for the ellipsis cuts the escape to "\x1".
        Assert.Equal("ab\u2026", HostPrompt.SanitizeLine("ab\u001bcd", 6));

        // "a" + "\u{202E}" fills 9 chars; the cut leaves "\u{202E" without its closing brace.
        Assert.Equal("a\u2026", HostPrompt.SanitizeLine("a\u202Eb", 9));
    }

    [Fact]
    public void HostPrompt_SanitizeLine_KeepsCompleteEscapesAndLiteralBackslashesBeforeTheEllipsis()
    {
        // A complete escape followed by more text is not a partial tail and must not erase everything after it.
        Assert.Equal("\\x1B[31mred\u2026", HostPrompt.SanitizeLine("\u001b[31mred text goes on", 12));
        Assert.Equal("\\u{202E}abc\u2026", HostPrompt.SanitizeLine("\u202Eabc defg", 12));

        // Backslashes in the input are ordinary text, even when followed by 'x' or 'u'.
        Assert.Equal("a\\xyz i\u2026", HostPrompt.SanitizeLine("a\\xyz is text", 8));
        Assert.Equal("path\\to\u2026", HostPrompt.SanitizeLine("path\\to\\file", 8));
    }
}
