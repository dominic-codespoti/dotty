using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class NativeTextInputBridgeTests
{
    [Fact]
    public void PreeditCopiesUnicodeScalarsAndMapsFocusedBlockToUtf16Selection()
    {
        uint[] scalars = ['A', 0x1F642, 'B'];
        int[] blocks = [1, 1, 1];

        bool accepted = ImePreeditState.TryCreate(scalars, blocks, focusedBlock: 1, out ImePreeditState state);

        Assert.True(accepted);
        Assert.True(state.IsActive);
        Assert.Equal("A🙂B", state.Text);
        Assert.Equal(1, state.SelectionStart);
        Assert.Equal(2, state.SelectionLength);
        Assert.Equal(3, state.CaretIndex);
    }

    [Fact]
    public void PreeditRejectsInvalidScalarsAndMalformedBlockSpansWithoutReturningPartialText()
    {
        uint[] invalidScalar = ['A', 0xD800, 'B'];
        int[] validBlocks = [1, 1, 1];
        Assert.False(ImePreeditState.TryCreate(invalidScalar, validBlocks, 1, out ImePreeditState invalidState));
        Assert.Equal(ImePreeditState.Empty, invalidState);

        uint[] text = ['x', 'y'];
        int[] overrunBlocks = [2, 1];
        Assert.False(ImePreeditState.TryCreate(text, overrunBlocks, 0, out ImePreeditState malformedState));
        Assert.Equal(ImePreeditState.Empty, malformedState);
    }
}
