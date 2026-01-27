using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class ScreenContinuationTests
{
    [Fact]
    public void Screen_ClearCell_OnContinuation_ClearsBaseAndContinuation()
    {
        var screen = new Screen(rows: 3, columns: 10);

        // write a double-width grapheme at (0,0)
        ref var baseCell = ref screen.GetCellRef(0, 0);
        baseCell.Grapheme = "界";
        baseCell.Width = 2;
        baseCell.IsContinuation = false;

        // mark continuation column
        ref var cont = ref screen.GetCellRef(0, 1);
        cont.Reset();
        cont.IsContinuation = true;

        // clear the continuation column (simulate eraser attacking continuation)
        screen.ClearCell(0, 1);

        var afterBase = screen.GetCell(0, 0);
        var afterCont = screen.GetCell(0, 1);

        Assert.True(afterBase.IsEmpty, "Base cell should be empty after clearing continuation column");
        Assert.True(afterCont.IsEmpty, "Continuation cell should be empty after clear");
    }

    [Fact]
    public void Screen_ClearCell_WithMissingContinuationFlag_ShouldClearBaseToo()
    {
        var screen = new Screen(rows: 3, columns: 10);

        // Simulate a corrupted state where the base thinks it's width=2
        // but the continuation cell has lost its IsContinuation marker.
        ref var baseCell = ref screen.GetCellRef(0, 0);
        baseCell.Grapheme = "界";
        baseCell.Width = 2;
        baseCell.IsContinuation = false;

        // continuation cell appears not marked as continuation (corrupted)
        ref var cont = ref screen.GetCellRef(0, 1);
        cont.Reset();
        cont.IsContinuation = false;

        // Historically, calling ClearCell on the continuation column could
        // leave the base grapheme orphaned. We assert the desired behavior
        // (clear the base) so this test will fail if the implementation
        // doesn't handle this corrupted case.
        screen.ClearCell(0, 1);

        var afterBase = screen.GetCell(0, 0);
        var afterCont = screen.GetCell(0, 1);

        // Desired invariant: both should be cleared even if continuation flag missing.
        Assert.True(afterBase.IsEmpty, "(Regression) Base cell should be empty after clear even when continuation flag missing");
        Assert.True(afterCont.IsEmpty, "Continuation cell should be empty after clear");
    }
}
