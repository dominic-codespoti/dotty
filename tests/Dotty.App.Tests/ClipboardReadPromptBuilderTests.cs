using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Prompt;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ClipboardReadPromptBuilderTests
{
    private static bool HasControl(string text) =>
        text.Any(c => c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F));

    private static bool HasLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    return true;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Disabled, non-separator rows after the title row: the preview rows and the count row.</summary>
    private static List<string> BodyLines(ContextMenuModel menu) =>
        menu.Items.Skip(1).Where(i => i.IsDisabled && !i.IsSeparator).Select(i => i.Label).ToList();

    private static string Label(ContextMenuModel menu, int index) => menu.Items[index].Label;

    [Fact]
    public void Build_ShowsTabTitleAndPreviewWithDenyFocusedAndAllowSecond()
    {
        int allowed = 0;
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("build server", "hello world", () => allowed++);

        Assert.True(menu.IsVisible);
        Assert.Equal("Tab \"build server\" wants to read the clipboard", Label(menu, 0));
        Assert.True(menu.Items[0].IsDisabled);
        Assert.Equal(new[] { "hello world", "(11 characters)" }, BodyLines(menu));
        // Rows: title, separator, preview, count, separator, Deny, Allow.
        Assert.Equal("Deny", menu.Items[menu.Items.Count - 2].Label);
        Assert.Equal("Allow", menu.Items[menu.Items.Count - 1].Label);
        Assert.Equal("Deny", menu.Items[menu.HoveredIndex].Label);
        Assert.Equal(0, allowed);
    }

    [Fact]
    public void ExecuteFocused_OnInitialFocusDeniesWithoutAllowing()
    {
        int allowed = 0;
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", "secret", () => allowed++);

        Assert.True(menu.ExecuteFocused());

        Assert.Equal(0, allowed);
        Assert.False(menu.IsVisible);
    }

    [Fact]
    public void AllowRow_RunsAllowExactlyOnce()
    {
        int allowed = 0;
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", "secret", () => allowed++);

        Assert.True(menu.MoveFocus(1));
        Assert.Equal("Allow", menu.Items[menu.HoveredIndex].Label);
        Assert.True(menu.ExecuteFocused());
        Assert.False(menu.ExecuteFocused());
        menu.Close();

        Assert.Equal(1, allowed);
    }

    [Fact]
    public void Dismissal_SendsNothing()
    {
        int allowed = 0;
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", "secret", () => allowed++);

        menu.Close();
        menu.Close();

        Assert.Equal(0, allowed);
        Assert.False(menu.IsVisible);
    }

    [Fact]
    public void HostileClipboardText_IsShownAsVisibleEscapes()
    {
        const string hostile = "ab\u001b]0;pwned\u0007cd\u0000\u009b31m\u007f\u202Eevil\r\nline two";
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", hostile, () => { });

        Assert.All(menu.Items, item => Assert.False(HasControl(item.Label), item.Label));
        List<string> body = BodyLines(menu);
        Assert.Contains("ab\\x1B]0;pwned\\x07cd\\x00\\x9B31m\\x7F\\u{202E}evil", body);
        Assert.Contains("line two", body);
    }

    [Fact]
    public void HostileTabTitle_IsSanitizedAndTruncatedSoTheQuestionStaysVisible()
    {
        string hostile = "\u001b[31m" + new string('x', 200) + "\u0007";
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build(hostile, "text", () => { });

        string title = Label(menu, 0);
        Assert.False(HasControl(title));
        Assert.True(title.Length <= HostPrompt.MaxLineChars, title);
        Assert.EndsWith("\" wants to read the clipboard", title, StringComparison.Ordinal);
        Assert.StartsWith("Tab \"\\x1B[31m", title, StringComparison.Ordinal);
        Assert.Contains("\u2026", title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingTabTitle_UsesGenericQuestion(string? tabTitle)
    {
        Assert.Equal("A tab wants to read the clipboard", ClipboardReadPromptBuilder.BuildTitle(tabTitle));
    }

    [Fact]
    public void EmptyClipboard_IsDescribedNotPreviewed()
    {
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", string.Empty, () => { });

        Assert.Equal(new[] { "(clipboard is empty)" }, BodyLines(menu));
        Assert.Equal("Deny", menu.Items[menu.HoveredIndex].Label);
    }

    [Fact]
    public void SingleCharacter_UsesSingularCount()
    {
        Assert.Equal(new[] { "x", "(1 character)" }, ClipboardReadPromptBuilder.BuildPreviewLines("x"));
    }

    [Fact]
    public void MultilineText_KeepsLogicalLinesAndTreatsCrLfAsOneBreak()
    {
        List<string> lines = ClipboardReadPromptBuilder.BuildPreviewLines("a\nb\r\nc\rd\n\ne");

        Assert.Equal(new[] { "a", "b", "c", "d", string.Empty, "e", "(11 characters)" }, lines);
    }

    [Fact]
    public void TrailingNewline_DoesNotAddABlankRow()
    {
        Assert.Equal(new[] { "line", "(5 characters)" }, ClipboardReadPromptBuilder.BuildPreviewLines("line\n"));
    }

    [Fact]
    public void LongLine_WrapsAtPromptWidthAndMarksCutOff()
    {
        string text = new string('x', 10_000);
        List<string> lines = ClipboardReadPromptBuilder.BuildPreviewLines(text);

        // MaxPreviewRows preview rows plus the count row fill every body line the prompt allows.
        Assert.Equal(HostPrompt.MaxLines, lines.Count);
        Assert.Equal("(10000 characters)", lines[^1]);
        List<string> rows = lines.Take(lines.Count - 1).ToList();
        Assert.Equal(ClipboardReadPromptBuilder.MaxPreviewRows, rows.Count);
        Assert.All(rows, row => Assert.True(row.Length <= HostPrompt.MaxLineChars, row));
        Assert.All(rows.Take(rows.Count - 1), row => Assert.Equal(HostPrompt.MaxLineChars, row.Length));
        Assert.EndsWith("\u2026", rows[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("\u2026", string.Concat(rows.Take(rows.Count - 1)));
    }

    [Fact]
    public void ManyLines_StopAtRowBudgetWithEllipsis()
    {
        string text = string.Join("\n", Enumerable.Range(0, 50).Select(i => "row" + i));
        List<string> lines = ClipboardReadPromptBuilder.BuildPreviewLines(text);

        Assert.Equal(HostPrompt.MaxLines, lines.Count);
        Assert.Equal("row0", lines[0]);
        Assert.Equal("row5", lines[5]);
        Assert.Equal("row6\u2026", lines[6]);
        Assert.Equal("(" + text.Length + " characters)", lines[7]);
    }

    [Fact]
    public void TextExactlyFillingTheBudget_IsNotMarkedCutOff()
    {
        string text = string.Join("\n", Enumerable.Range(0, ClipboardReadPromptBuilder.MaxPreviewRows).Select(i => "r" + i));
        List<string> lines = ClipboardReadPromptBuilder.BuildPreviewLines(text);

        Assert.Equal(HostPrompt.MaxLines, lines.Count);
        Assert.DoesNotContain(lines, line => line.Contains('\u2026'));
        Assert.Equal("r6", lines[6]);
    }

    [Fact]
    public void WrappedRows_NeverSplitSurrogatePairs()
    {
        // 59 ASCII chars then emoji: the pair would straddle the 60-char row boundary.
        string text = new string('a', 59) + "\U0001F600" + new string('b', 30);
        List<string> lines = ClipboardReadPromptBuilder.BuildPreviewLines(text);

        Assert.All(lines, line => Assert.False(HasLoneSurrogate(line), line));
        Assert.All(lines, line => Assert.True(line.Length <= HostPrompt.MaxLineChars, line));
        Assert.Equal(new string('a', 59), lines[0]);
        Assert.True(lines[1].StartsWith("\U0001F600", StringComparison.Ordinal), lines[1]);
    }

    [Fact]
    public void SourceLimitInsideSurrogatePair_DoesNotLeaveALoneSurrogate()
    {
        string text = new string('a', ClipboardReadPromptBuilder.MaxPreviewSourceChars - 1) + "\U0001F600" + "tail";
        List<string> lines = ClipboardReadPromptBuilder.BuildPreviewLines(text);

        Assert.All(lines, line => Assert.False(HasLoneSurrogate(line), line));
        Assert.All(lines, line => Assert.True(line.Length <= HostPrompt.MaxLineChars, line));
    }

    [Fact]
    public void EveryRowFitsPromptLimits_ForAdversarialText()
    {
        string text = string.Concat(Enumerable.Repeat("\u001b\u0000\u009f\u202Ex\t", 500));
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("t\u001b", text, () => { });

        Assert.All(menu.Items, item => Assert.False(HasControl(item.Label)));
        Assert.All(menu.Items, item => Assert.True(item.Label.Length <= HostPrompt.MaxLineChars, item.Label));
        // title, sep, <= 8 body rows, sep, Deny, Allow
        Assert.True(menu.Items.Count <= 2 + HostPrompt.MaxLines + 3);
    }

    [Fact]
    public void Center_PlacesPromptInTheMiddleOfTheViewport()
    {
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", "hello", () => { });
        const float width = 1200f;
        const float height = 800f;
        const float cell = 9f;
        const float row = 18f;

        ClipboardReadPromptBuilder.Center(menu, width, height, cell, row);

        ContextMenuLayout layout = ContextMenuLayout.Calculate(menu, width, height, cell, row);
        Assert.Equal((width - layout.Width) / 2f, layout.X, 0.01f);
        Assert.Equal((height - layout.Height) / 2f, layout.Y, 0.01f);
        Assert.True(layout.X >= 0f && layout.Y >= 0f);
        Assert.True(layout.X + layout.Width <= width);
        Assert.True(layout.Y + layout.Height <= height);
    }

    [Fact]
    public void Center_ClampsIntoAViewportSmallerThanThePrompt()
    {
        ContextMenuModel menu = ClipboardReadPromptBuilder.Build("tab", "hello", () => { });

        ClipboardReadPromptBuilder.Center(menu, 100f, 50f, 9f, 18f);

        Assert.Equal(0f, menu.X);
        Assert.Equal(0f, menu.Y);
    }
}
