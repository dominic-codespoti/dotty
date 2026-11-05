using System.Collections.Generic;
using System.Linq;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Input;
using Dotty.Runtime.Selection;
using Dotty.Runtime.Tabs;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class BindingActionsTests
{
    [Fact]
    public void KeybindingReport_ListsActionsBindingsAndRejectedEntries()
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        manager.ApplyCustomBindings(new Dictionary<string, string>
        {
            ["ctrl+alt+u"] = "send_text:\\x15",
            ["ctrl+shift+c"] = "none",
            ["hyper+x"] = "copy",
        });

        string report = KeybindingReport.Render(manager);

        foreach (var info in ActionRegistry.All)
            Assert.Contains("  " + info.Name, report);
        Assert.Contains("  send_text:<text> - ", report);
        Assert.Contains("  ctrl+alt+u = send_text:\\x15\n", report);
        Assert.Contains("  ctrl+shift+v = paste\n", report);
        Assert.DoesNotContain("ctrl+shift+c =", report);
        Assert.Contains("Rejected entries:\n  keybindings: \"hyper+x\": \"copy\": modifier 'hyper' is not supported", report);
    }

    [Fact]
    public void KeybindingReport_OmitsTheRejectedSectionWhenEverythingApplied()
    {
        Assert.DoesNotContain("Rejected", KeybindingReport.Render(new KeybindingManager(KeybindingPlatform.Linux)));
    }

    [Theory]
    // tabs a b c d, active index, tab to move, delta -> order, active index after
    [InlineData(1, 1, -1, "bacd", 0, true)]
    [InlineData(1, 1, 1, "acbd", 2, true)]
    [InlineData(2, 1, 1, "acbd", 1, true)]   // active c is pushed left by b
    [InlineData(0, 0, -1, "abcd", 0, false)] // already first: no wrap
    [InlineData(3, 3, 1, "abcd", 3, false)]  // already last
    [InlineData(0, 0, 0, "abcd", 0, false)]
    public void MoveTab_ReordersWithoutWrapping_AndKeepsTheActiveTab(int active, int moved, int delta, string order, int activeAfter, bool result)
    {
        using var manager = new TerminalTabManager(SilentPty.Create);
        var tabs = new List<TerminalTab>();
        for (int i = 0; i < 4; i++)
            tabs.Add(manager.CreateTab());
        manager.SelectTab(active);

        Assert.Equal(result, manager.MoveTab(tabs[moved], delta));
        Assert.Equal(order, string.Concat(manager.Tabs.Select(t => (char)('a' + tabs.IndexOf(t)))));
        Assert.Equal(activeAfter, manager.ActiveIndex);
        Assert.Same(tabs[active], manager.ActiveTab);
    }
}
