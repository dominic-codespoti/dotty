using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class ContinuationClearTests
{
    [Fact]
    public void ClearingContinuationColumnClearsBaseAndContinuation()
    {
        var tb = new TerminalBuffer(rows: 3, columns: 10);

        // Write a double-width grapheme at (0,0)
        tb.SetCursor(0, 0);
        tb.WriteText("界".AsSpan(), CellAttributes.Default);

        var beforeBase = tb.GetCell(0, 0);
        var beforeCont = tb.GetCell(0, 1);
        Assert.Equal("界", beforeBase.Grapheme);
        Assert.True(beforeCont.IsContinuation);

        // Move cursor into the continuation column and clear from cursor
        tb.SetCursor(0, 1);
        tb.EraseLine(0);

        var afterBase = tb.GetCell(0, 0);
        var afterCont = tb.GetCell(0, 1);

        Assert.True(afterBase.IsEmpty, $"Base cell not empty after clear: '{afterBase.Grapheme}' cont={afterBase.IsContinuation}");
        Assert.True(afterCont.IsEmpty, $"Continuation cell not empty after clear: '{afterCont.Grapheme}' cont={afterCont.IsContinuation}");
    }
}
