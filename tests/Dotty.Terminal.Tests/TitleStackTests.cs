using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class TitleStackTests
{
    [Fact]
    public void PairedPushCanPopBothOrConsumeOnlyTheSelectedHalf()
    {
        using var adapter = new TerminalAdapter();
        var changed = new List<string>();
        adapter.TitleChanged += changed.Add;
        adapter.OnSetTitle(0, "saved"u8);
        adapter.OnPushTitle();
        adapter.OnSetTitle(0, "current"u8);

        adapter.OnPopTitle(1);
        Assert.Equal("saved", adapter.IconTitle);
        Assert.Equal("current", adapter.WindowTitle);
        Assert.Equal("current", changed[^1]);

        adapter.OnPopTitle(2);
        Assert.Equal("saved", adapter.IconTitle);
        Assert.Equal("current", adapter.WindowTitle);
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void PartialPushFramesInheritTheOtherHalfAndSelectivePopConsumesWholeFrame()
    {
        using var adapter = new TerminalAdapter();
        adapter.OnSetTitle(2, "window-before"u8);
        adapter.OnSetTitle(1, "icon-before"u8);
        adapter.OnPushTitle(1);
        adapter.OnSetTitle(2, "window-saved"u8);
        adapter.OnPushTitle(2);
        adapter.OnSetTitle(0, "current"u8);

        adapter.OnPopTitle();
        Assert.Equal("icon-before", adapter.IconTitle);
        Assert.Equal("window-saved", adapter.WindowTitle);

        adapter.OnSetTitle(0, "changed"u8);
        adapter.OnPopTitle(2);
        Assert.Equal("changed", adapter.IconTitle);
        Assert.Equal("changed", adapter.WindowTitle);
        adapter.OnPopTitle(1);
        Assert.Equal("changed", adapter.IconTitle);
    }

    [Fact]
    public void EmptyAndUnderflowPopsDoNotChangeTitlesOrPublish()
    {
        using var adapter = new TerminalAdapter();
        var changed = new List<string>();
        adapter.TitleChanged += changed.Add;

        adapter.OnPopTitle();
        Assert.Null(adapter.WindowTitle);
        Assert.Null(adapter.IconTitle);
        Assert.Empty(changed);

        adapter.OnPushTitle();
        adapter.OnSetTitle(0, "after-initial-save"u8);
        adapter.OnPopTitle();
        Assert.Equal(string.Empty, adapter.WindowTitle);
        Assert.Equal(string.Empty, adapter.IconTitle);

        adapter.OnSetTitle(0, ""u8);
        adapter.OnPushTitle();
        adapter.OnSetTitle(0, "nonempty"u8);
        adapter.OnPopTitle();
        Assert.Equal(string.Empty, adapter.WindowTitle);
        Assert.Equal(string.Empty, adapter.IconTitle);
        Assert.Equal(string.Empty, changed[^1]);
        int count = changed.Count;

        adapter.OnPopTitle();
        Assert.Equal(count, changed.Count);
    }

    [Fact]
    public void StackOverflowKeepsTheNewestTenFramesInTheSharedRing()
    {
        using var adapter = new TerminalAdapter();
        for (int i = 0; i < TerminalAdapter.MaxTitleStackDepth + 2; i++)
        {
            string title = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            adapter.OnSetTitle(0, System.Text.Encoding.UTF8.GetBytes(title));
            adapter.OnPushTitle();
        }

        adapter.OnSetTitle(0, "changed"u8);
        for (int i = 0; i < TerminalAdapter.MaxTitleStackDepth; i++)
            adapter.OnPopTitle();

        Assert.Equal("2", adapter.WindowTitle);
        Assert.Equal("2", adapter.IconTitle);
        adapter.OnPopTitle();
        Assert.Equal("2", adapter.WindowTitle);
    }

    [Fact]
    public void IconOnlyRestoresNeverRaiseWindowTitleChanged()
    {
        using var adapter = new TerminalAdapter();
        int windowNotifications = 0;
        adapter.TitleChanged += _ => windowNotifications++;
        adapter.OnSetTitle(2, "window"u8);
        adapter.OnSetTitle(1, "old-icon"u8);
        int afterWindowSet = windowNotifications;
        adapter.OnPushTitle(1);
        adapter.OnSetTitle(1, "new-icon"u8);
        adapter.OnPopTitle(1);

        Assert.Equal("old-icon", adapter.IconTitle);
        Assert.Equal("window", adapter.WindowTitle);
        Assert.Equal(afterWindowSet, windowNotifications);
    }

    [Fact]
    public void DirectSlotsWrapModuloTenReplaceThePairedFrameAndDoNotMoveTheStack()
    {
        using var adapter = new TerminalAdapter();
        adapter.OnSetTitle(0, "first-pair"u8);
        adapter.OnPushTitle(0, 1);
        adapter.OnSetTitle(1, "direct-icon"u8);
        adapter.OnPushTitle(1, TerminalAdapter.MaxTitleStackDepth + 1);
        adapter.OnSetTitle(0, "current"u8);

        adapter.OnPopTitle(0, 1);
        Assert.Equal("direct-icon", adapter.IconTitle);
        Assert.Equal("current", adapter.WindowTitle);

        adapter.OnSetTitle(0, "stack-current"u8);
        adapter.OnPopTitle();
        Assert.Equal("stack-current", adapter.WindowTitle);
        Assert.Equal("stack-current", adapter.IconTitle);
    }
}
