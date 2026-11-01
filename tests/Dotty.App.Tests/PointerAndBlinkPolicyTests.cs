using Dotty.Runtime.Input;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class PointerAndBlinkPolicyTests
{
    [Theory]
    [InlineData(PointerShape.Text, PointerShape.Default, false, PointerShape.Text)]
    [InlineData(PointerShape.Text, PointerShape.Pointer, false, PointerShape.Pointer)]
    [InlineData(PointerShape.Text, PointerShape.Crosshair, true, PointerShape.Hidden)]
    [InlineData(PointerShape.EwResize, PointerShape.Crosshair, false, PointerShape.EwResize)]
    [InlineData(PointerShape.Pointer, PointerShape.Wait, false, PointerShape.Pointer)]
    [InlineData(PointerShape.Default, PointerShape.Wait, false, PointerShape.Default)]
    [InlineData(PointerShape.NsResize, PointerShape.Default, true, PointerShape.Hidden)]
    [InlineData(PointerShape.Text, PointerShape.Hidden, false, PointerShape.Text)]
    public void ShapePriority(PointerShape host, PointerShape app, bool hidden, PointerShape expected) =>
        Assert.Equal(expected, PointerShapePolicy.Resolve(host, app, hidden));

    [Theory]
    // appDefault, appBlink, config, system, expected
    [InlineData(true, false, true, null, true)]
    [InlineData(true, false, false, null, false)]
    [InlineData(true, true, false, null, false)]
    [InlineData(false, true, false, null, true)]
    [InlineData(false, false, true, null, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, false, true, true)]
    public void BlinkTruthTable(bool appDefault, bool appBlink, bool config, bool? system, bool expected) =>
        Assert.Equal(expected, CursorBlinkPolicy.Blinks(appDefault, appBlink, config, system));

    [Theory]
    [InlineData("pointer", PointerShape.Pointer)]
    [InlineData("hand2", PointerShape.Pointer)]
    [InlineData(" XTERM ", PointerShape.Text)]
    [InlineData("col-resize", PointerShape.EwResize)]
    [InlineData("sb_v_double_arrow", PointerShape.NsResize)]
    [InlineData("", PointerShape.Default)]
    [InlineData("default", PointerShape.Default)]
    [InlineData("left_ptr_watch", PointerShape.Progress)]
    public void ParseNames(string name, PointerShape expected)
    {
        Assert.True(PointerShapeNames.TryParse(name, out var shape));
        Assert.Equal(expected, shape);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("zoom-in-please")]
    public void UnknownNamesAreRejected(string name) => Assert.False(PointerShapeNames.TryParse(name, out _));

    [Theory]
    [InlineData("\u001b]22;pointer\u001b\\", PointerShape.Pointer)]
    [InlineData("\u001b]22;bogus,crosshair\u0007", PointerShape.Crosshair)]
    [InlineData("\u001b]22;wait\u0007\u001b]22;bogus\u0007", PointerShape.Wait)]
    [InlineData("\u001b]22;wait\u0007\u001b]22;?__current__\u0007", PointerShape.Wait)]
    [InlineData("\u001b]22;wait\u0007\u001b]22;\u0007", PointerShape.Default)]
    [InlineData("\u001b]22;>move\u0007", PointerShape.Move)]
    [InlineData("\u001b]22;wait\u0007\u001bc", PointerShape.Default)]
    public void Osc22UpdatesAdapter(string input, PointerShape expected)
    {
        using var adapter = new TerminalAdapter(24, 80);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = adapter };
        parser.Feed(System.Text.Encoding.UTF8.GetBytes(input));
        Assert.Equal(expected, adapter.RequestedPointerShape);
    }
}
