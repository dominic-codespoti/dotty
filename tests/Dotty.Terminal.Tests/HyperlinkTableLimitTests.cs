using System;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class HyperlinkTableLimitTests
{
    [Fact]
    public void Table_RefusesNewEntriesAtCap_AndEarlierIdsKeepTheirUrls()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 20, scrollbackCapacity: 10);
        var ids = new ushort[TerminalBuffer.MaxHyperlinkEntries];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = buffer.GetOrCreateHyperlinkId("https://e.test/" + i);
            Assert.Equal(i + 1, ids[i]);
        }

        // The 65 536th distinct URI must not wrap around onto an existing id.
        Assert.Equal(0, buffer.GetOrCreateHyperlinkId("https://e.test/overflow"));
        Assert.Equal(0, buffer.GetOrCreateHyperlinkId("https://e.test/overflow-2"));

        for (int i = 0; i < ids.Length; i++)
            Assert.Equal("https://e.test/" + i, buffer.GetHyperlinkUrl(ids[i]));

        // Already-stored URIs are still found after the cap.
        Assert.Equal(ids[41], buffer.GetOrCreateHyperlinkId("https://e.test/41"));
    }

    [Fact]
    public void Table_RefusesNewEntriesWhenCharacterBudgetIsExhausted()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 20, scrollbackCapacity: 10);
        string big = new string('a', 8000);
        int stored = 0;
        ushort lastId = 0;
        string? lastUrl = null;
        while (true)
        {
            string url = "https://e.test/" + stored.ToString("D8") + big;
            ushort id = buffer.GetOrCreateHyperlinkId(url);
            if (id == 0)
                break;
            lastId = id;
            lastUrl = url;
            stored++;
            Assert.True(stored < 1000, "budget never enforced");
        }

        Assert.True(stored > 0);
        Assert.True((long)stored * 8000 <= TerminalBuffer.MaxHyperlinkChars);
        Assert.Equal(lastUrl, buffer.GetHyperlinkUrl(lastId));
        Assert.Equal(lastId, buffer.GetOrCreateHyperlinkId(lastUrl!));
        Assert.Equal(0, buffer.GetOrCreateHyperlinkId("https://e.test/after-budget" + big));
    }
}
