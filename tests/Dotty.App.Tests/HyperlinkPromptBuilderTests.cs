using System.Collections.Generic;
using System.Linq;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Prompt;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkPromptBuilderTests
{
    private static HyperlinkTarget Target(string canonical, string scheme = "ssh") =>
        new(canonical, scheme, "host", null, HyperlinkSource.Explicit);

    [Fact]
    public void BuildConfirm_ListsRiskAndTargetAndFocusesCancel()
    {
        int opened = 0, copied = 0;
        ContextMenuModel menu = HyperlinkPromptBuilder.BuildConfirm(
            Target("ssh://host/x"), HyperlinkRisk.NonWebScheme, displayText: null,
            open: () => opened++, copyAddress: () => copied++);

        // title, sep, risk, target, sep, Open, Copy link address, Cancel
        Assert.Equal(8, menu.Items.Count);
        Assert.Equal("Open link?", menu.Items[0].Label);
        Assert.Equal("Non-web link (ssh)", menu.Items[2].Label);
        Assert.Equal("ssh://host/x", menu.Items[3].Label);
        Assert.Equal(new[] { "Open", "Copy link address", "Cancel" },
            menu.Items.Skip(5).Select(i => i.Label).ToArray());
        Assert.Equal("Cancel", menu.Items[menu.HoveredIndex].Label);

        Assert.True(menu.ExecuteFocused());
        Assert.Equal(0, opened);
        Assert.Equal(0, copied);
    }

    [Fact]
    public void BuildConfirm_OpenRowRunsOpenOnly()
    {
        int opened = 0, copied = 0;
        ContextMenuModel menu = HyperlinkPromptBuilder.BuildConfirm(
            Target("ssh://host/x"), HyperlinkRisk.NonWebScheme, null,
            open: () => opened++, copyAddress: () => copied++);

        Assert.True(menu.MoveFocus(-1));
        Assert.True(menu.MoveFocus(-1));
        Assert.Equal("Open", menu.Items[menu.HoveredIndex].Label);
        Assert.True(menu.ExecuteFocused());

        Assert.Equal(1, opened);
        Assert.Equal(0, copied);
    }

    [Fact]
    public void BuildConfirm_DismissalRunsNoAction()
    {
        int ran = 0;
        ContextMenuModel menu = HyperlinkPromptBuilder.BuildConfirm(
            Target("ssh://host/x"), HyperlinkRisk.NonWebScheme, null,
            open: () => ran++, copyAddress: () => ran++);

        menu.Close();

        Assert.Equal(0, ran);
        Assert.False(menu.IsVisible);
    }

    [Fact]
    public void BuildConfirm_TextMismatchShowsSanitizedLinkText()
    {
        ContextMenuModel menu = HyperlinkPromptBuilder.BuildConfirm(
            Target("https://evil.test/", "https"), HyperlinkRisk.TextMismatch, "https://pay\u001bpal.com",
            () => { }, () => { });

        List<string> labels = menu.Items.Select(i => i.Label).ToList();
        Assert.Contains("https://pay\\x1Bpal.com", labels);
        Assert.Contains("https://evil.test/", labels);
        Assert.DoesNotContain(labels, l => l.Contains('\u001b'));
    }

    [Fact]
    public void BuildConfirm_LongTargetIsWrappedWithinPromptLimitsAndMarkedTruncated()
    {
        string canonical = "https://example.com/" + new string('a', 600);
        ContextMenuModel menu = HyperlinkPromptBuilder.BuildConfirm(
            Target(canonical, "https"),
            HyperlinkRisk.NonWebScheme | HyperlinkRisk.Userinfo | HyperlinkRisk.Idn | HyperlinkRisk.LocalFile,
            null, () => { }, () => { });

        // title + sep + <= MaxLines body rows + sep + 3 actions
        Assert.True(menu.Items.Count <= 2 + HostPrompt.MaxLines + 1 + 3);
        Assert.All(menu.Items, i => Assert.True(i.Label.Length <= HostPrompt.MaxLineChars));
        // The host part of the target is on the first target row.
        Assert.Contains(menu.Items, i => i.Label.StartsWith("https://example.com/", System.StringComparison.Ordinal));
        Assert.Contains(menu.Items, i => i.Label.EndsWith("\u2026", System.StringComparison.Ordinal));
    }

    [Fact]
    public void BuildNotice_HasSingleDismissRow()
    {
        ContextMenuModel menu = HyperlinkPromptBuilder.BuildNotice("Link blocked", "scheme is not allowed");

        Assert.Equal("Dismiss", menu.Items[menu.HoveredIndex].Label);
        Assert.Equal("scheme is not allowed", menu.Items.Single(i => i.Id.StartsWith(HostPrompt.LineIdPrefix)).Label);
    }

    [Theory]
    [InlineData(60, 2, new[] { 60 })]
    [InlineData(61, 2, new[] { 60, 1 })]
    [InlineData(150, 2, new[] { 60, 60 })]
    public void AppendWrapped_SplitsAtPromptWidth(int length, int maxRows, int[] expectedLengths)
    {
        var lines = new List<string>();

        HyperlinkPromptBuilder.AppendWrapped(lines, new string('x', length), maxRows);

        Assert.Equal(expectedLengths, lines.Select(l => l.Length).ToArray());
    }

    [Fact]
    public void AppendWrapped_OverflowEndsLastRowWithEllipsis()
    {
        var lines = new List<string>();

        HyperlinkPromptBuilder.AppendWrapped(lines, new string('x', 150), 2);

        Assert.Equal(2, lines.Count);
        Assert.Equal(60, lines[0].Length);
        Assert.Equal(60, lines[1].Length);
        Assert.EndsWith("\u2026", lines[1]);
    }
}
