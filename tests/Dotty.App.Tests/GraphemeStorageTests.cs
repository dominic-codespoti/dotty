using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class GraphemeStorageTests
{
    [Fact]
    public void Stores_BoxDrawing_Grapheme()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.WriteText("│".AsSpan(), CellAttributes.Default);
        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal("│", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
    }

    [Fact]
    public void Stores_Powerline_PUA_Grapheme()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.WriteText("\uE0B0".AsSpan(), CellAttributes.Default);
        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal("\uE0B0", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
    }

    [Fact]
    public void StoreGrapheme_DeduplicatesIdenticalClusters()
    {
        short first = GraphemeHelper.StoreGrapheme("e\u0301");
        short second = GraphemeHelper.StoreGrapheme("e\u0301");
        short third = GraphemeHelper.StoreGrapheme("e\u0301".AsSpan());

        Assert.True(first > 0);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal("e\u0301", GraphemeHelper.Resolve('e', first));
    }

    [Fact]
    public void RepeatedDecomposedAccentWrites_DoNotGrowTheStoreAndStayResolvable()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 4);
        buffer.WriteText("\re\u0301".AsSpan(), CellAttributes.Default);
        int count = GraphemeHelper.StoredGraphemeCount;

        // More writes than a 16-bit index could address: without dedupe the index wraps negative at 32 767
        // and aliases earlier entries at 65 536.
        for (int i = 0; i < 70_000; i++)
        {
            buffer.WriteText("\re\u0301".AsSpan(), CellAttributes.Default);
            if ((i & 0x3FF) == 0 || i > 69_990)
            {
                var cell = buffer.GetCell(0, 0);
                var cold = buffer.GetColdCell(0, 0);
                Assert.Equal("e\u0301", GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex));
            }
        }

        // The store is process-wide and other tests add clusters concurrently, so assert "bounded", not "unchanged".
        Assert.True(GraphemeHelper.StoredGraphemeCount - count < 1000);
    }

    [Fact]
    public void GraphemeStore_WhenFull_ReturnsMinusOneWithoutAliasing()
    {
        var store = new GraphemeStore(maxEntries: 3);
        short a = store.Store("a\u0301");
        short b = store.Store("b\u0301");
        short c = store.Store("c\u0301");
        short overflow = store.Store("d\u0301");

        Assert.Equal(new short[] { 1, 2, 3 }, new[] { a, b, c });
        Assert.Equal(-1, overflow);
        Assert.Equal(-1, store.Store("e\u0301"));
        Assert.Equal(3, store.Count);
        Assert.Equal("a\u0301", store.Get(a));
        Assert.Equal("b\u0301", store.Get(b));
        Assert.Equal("c\u0301", store.Get(c));
        Assert.Null(store.Get(4));
        // Existing clusters still resolve to their own index when the table is full.
        Assert.Equal(b, store.Store("b\u0301"));
    }

    [Fact]
    public void GraphemeStore_FillsToTheShortLimitWithoutWrapping()
    {
        var store = new GraphemeStore();
        for (int i = 1; i <= short.MaxValue; i++)
            Assert.Equal((short)i, store.Store("x" + i));
        Assert.Equal((int)short.MaxValue, store.Count);
        Assert.Equal(-1, store.Store("one too many"));
        Assert.Equal("x1", store.Get(1));
        Assert.Equal("x" + short.MaxValue, store.Get(short.MaxValue));
        Assert.Equal((short)12345, store.Store("x12345"));
    }

    [Fact]
    public void GraphemeStore_RejectsEmptyValue()
    {
        var store = new GraphemeStore(maxEntries: 2);
        Assert.Equal(-1, store.Store(string.Empty));
        Assert.Equal(0, store.Count);
        Assert.Equal(string.Empty, store.Get(0));
    }

    [Fact]
    public async System.Threading.Tasks.Task GraphemeStore_ConcurrentReadersSeeCompleteEntries()
    {
        var store = new GraphemeStore();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var done = new System.Threading.CancellationTokenSource();
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var readers = new System.Threading.Tasks.Task[3];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = System.Threading.Tasks.Task.Run(() =>
            {
                while (!done.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    int count = store.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        if (store.Get(i) != "g" + i) failures.Enqueue("entry " + i);
                    }
                }
            }, cancellationToken);
        }

        try
        {
            for (int i = 1; i <= 5000; i++) Assert.Equal((short)i, store.Store("g" + i));
        }
        finally
        {
            done.Cancel();
            await System.Threading.Tasks.Task.WhenAll(readers);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ScalarHelpers_TreatSurrogatePairsAsOneScalar()
    {
        const string pair = "\U0001D165";
        Assert.Equal(0, GraphemeHelper.CountScalars(""));
        Assert.Equal(3, GraphemeHelper.CountScalars("a" + pair + "b"));
        Assert.Equal(2, GraphemeHelper.CountScalars("\uD800\uD800")); // lone surrogates count once each

        Assert.Equal(0, GraphemeHelper.TruncateToScalars("abc", 0));
        Assert.Equal(2, GraphemeHelper.TruncateToScalars("abc", 2));
        Assert.Equal(3, GraphemeHelper.TruncateToScalars("abc", 10));
        Assert.Equal(3, GraphemeHelper.TruncateToScalars("a" + pair + "b", 2)); // keeps the whole pair
        Assert.Equal(1, GraphemeHelper.TruncateToScalars("a" + pair + "b", 1)); // never ends between halves
    }
}
