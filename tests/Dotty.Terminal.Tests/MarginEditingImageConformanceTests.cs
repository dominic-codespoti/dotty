using System;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class MarginEditingImageConformanceTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        private uint _nextId;
        public TerminalAdapter Adapter { get; } = new(rows: 5, columns: 7, scrollbackCapacity: 20);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;

        public Rig()
        {
            Adapter.SetCellPixelSize(10, 20);
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public void Feed(string value) => Parser.Feed(Encoding.ASCII.GetBytes(value));

        public uint Put(int row, int col)
        {
            uint id = ++_nextId;
            Feed(Esc + $"[{row + 1};{col + 1}H");
            Parser.Feed(KittyTestData.ApcBytes($"a=T,i={id},s=10,v=20,f=24,c=1,r=1,C=1", Convert.ToBase64String(new byte[600])));
            return id;
        }

        public long? Row(uint id)
        {
            var placement = Buffer.ActiveImageStore.Placements.SingleOrDefault(p => p.Image.ClientId == id);
            return placement is null ? null : placement.AnchorRow - Buffer.ActiveImageStore.ViewTop;
        }

        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void PartialInsertAndDeleteLinesMoveContainedImagesOnlyWithoutHistoryOrViewTopChanges()
    {
        using var t = new Rig();
        uint inside = t.Put(2, 2);
        uint outsideColumn = t.Put(2, 0);
        uint outsideRegion = t.Put(0, 2);
        var store = t.Buffer.ActiveImageStore;
        Assert.Equal(0, store.ViewTop);
        int history = t.Buffer.ScrollbackCount;

        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;4r" + Esc + "[2;3H" + Esc + "[1L");
        Assert.Equal(3L, t.Row(inside));
        Assert.Equal(2L, t.Row(outsideColumn));
        Assert.Equal(0L, t.Row(outsideRegion));
        Assert.Equal(0, store.ViewTop);
        Assert.Equal(history, t.Buffer.ScrollbackCount);

        t.Feed(Esc + "[3;3H" + Esc + "[1M");
        Assert.Equal(2L, t.Row(inside));
        Assert.Equal(2L, t.Row(outsideColumn));
        Assert.Equal(0L, t.Row(outsideRegion));
        Assert.Equal(0, store.ViewTop);
        Assert.Equal(history, t.Buffer.ScrollbackCount);
        Assert.Empty(t.Buffer.ValidateInvariants());
    }
}
