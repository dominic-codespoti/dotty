using System.Linq;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Prompt;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class PastePromptBuilderTests
{
    [Fact]
    public void Build_ShowsTitlePreviewReasonAndFocusesCancel()
    {
        int pasted = 0;
        ContextMenuModel menu = PastePromptBuilder.Build("a\nb\n", PasteRisk.Newlines, () => pasted++);

        // title, sep, preview a, preview b, reason, sep, Paste, Cancel
        Assert.Equal(8, menu.Items.Count);
        Assert.Equal("Paste 2 lines, 4 characters?", menu.Items[0].Label);
        Assert.Equal("a", menu.Items[2].Label);
        Assert.Equal("b", menu.Items[3].Label);
        Assert.StartsWith("Multiple lines", menu.Items[4].Label);
        Assert.Equal(new[] { "Paste", "Cancel" }, menu.Items.Skip(6).Select(i => i.Label).ToArray());
        Assert.Equal("Cancel", menu.Items[menu.HoveredIndex].Label);

        Assert.True(menu.ExecuteFocused());
        Assert.Equal(0, pasted);
    }

    [Fact]
    public void Build_PasteRowRunsPasteOnce()
    {
        int pasted = 0;
        ContextMenuModel menu = PastePromptBuilder.Build("a\nb", PasteRisk.Newlines, () => pasted++);

        menu.HoveredIndex = menu.Items.Count - 2;
        Assert.Equal("Paste", menu.Items[menu.HoveredIndex].Label);
        Assert.True(menu.ExecuteFocused());
        Assert.Equal(1, pasted);
        Assert.False(menu.ExecuteFocused());
    }

    [Fact]
    public void Build_DismissPastesNothing()
    {
        int pasted = 0;
        ContextMenuModel menu = PastePromptBuilder.Build("a\nb", PasteRisk.Newlines, () => pasted++);

        menu.Close();

        Assert.Equal(0, pasted);
    }

    [Fact]
    public void Build_SingleLineTitleIsSingular()
    {
        ContextMenuModel menu = PastePromptBuilder.Build("x\x1b[201~", PasteRisk.ControlChars | PasteRisk.EndMarker, () => { });

        Assert.Equal("Paste 1 line, 6 characters?", menu.Items[0].Label);
    }

    [Fact]
    public void Build_ControlCharactersAreNeverRawInAnyRow()
    {
        ContextMenuModel menu = PastePromptBuilder.Build(
            "rm -rf /\x1b[201~\x07\u009b\u202e\n",
            PasteRisk.Newlines | PasteRisk.ControlChars | PasteRisk.EndMarker,
            () => { });

        foreach (ContextMenuItem item in menu.Items.Where(i => !i.IsSeparator))
        {
            Assert.DoesNotContain(item.Label, c => c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F));
            Assert.DoesNotContain('\u202e', item.Label);
        }
    }

    [Fact]
    public void Build_AllReasonsAndLongTextStayWithinLimits()
    {
        string text = string.Join('\n', Enumerable.Range(0, 200).Select(i => new string('x', 300) + i)) + "\x1b[201~";
        ContextMenuModel menu = PastePromptBuilder.Build(
            text, PasteRisk.Newlines | PasteRisk.ControlChars | PasteRisk.EndMarker, () => { });

        ContextMenuItem[] body = menu.Items.Where(i => i.Id.StartsWith(HostPrompt.LineIdPrefix)).ToArray();
        Assert.True(body.Length <= HostPrompt.MaxLines);
        Assert.All(menu.Items, i => Assert.True(i.Label.Length <= HostPrompt.MaxLineChars));
        // All three reason rows survive after the preview.
        Assert.Contains(body, i => i.Label.StartsWith("Multiple lines"));
        Assert.Contains(body, i => i.Label.StartsWith("Control characters"));
        Assert.Contains(body, i => i.Label.StartsWith("Contains a paste-end marker"));
        Assert.Contains(body, i => i.Label.Contains("more lines"));
    }
}
