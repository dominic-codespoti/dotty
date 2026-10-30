using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>DECSCNM (<c>CSI ? 5 h/l</c>): adapter state, repaint notification and reset behaviour.</summary>
public sealed class ReverseVideoTests
{
    private const string Esc = "\x1b";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Setup()
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 10);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void SetAndReset_ToggleTheFlagAndNotifyOnlyOnChange()
    {
        var (adapter, parser) = Setup();
        int changes = 0;
        adapter.PaletteChanged += () => changes++;
        Assert.False(adapter.ReverseVideo);

        Feed(parser, $"{Esc}[?5h");
        Assert.True(adapter.ReverseVideo);
        Assert.Equal(1, changes);

        Feed(parser, $"{Esc}[?5h");
        Assert.Equal(1, changes);

        Feed(parser, $"{Esc}[?5l");
        Assert.False(adapter.ReverseVideo);
        Assert.Equal(2, changes);

        Feed(parser, $"{Esc}[?5l");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void SetInsideACombinedModeList_IsApplied()
    {
        var (adapter, parser) = Setup();

        Feed(parser, $"{Esc}[?7;5;25h");

        Assert.True(adapter.ReverseVideo);
    }

    [Fact]
    public void Decstr_KeepsReverseVideo_RisClearsIt()
    {
        var (adapter, parser) = Setup();
        Feed(parser, $"{Esc}[?5h");

        Feed(parser, $"{Esc}[!p");
        Assert.True(adapter.ReverseVideo);

        Feed(parser, $"{Esc}c");
        Assert.False(adapter.ReverseVideo);
    }

    [Fact]
    public void ReverseVideo_DoesNotTouchTheOscDefaultColorOverrides()
    {
        var (adapter, parser) = Setup();
        Feed(parser, $"{Esc}]10;#010203\a{Esc}]11;#040506\a{Esc}[?5h");

        Assert.Equal(0xFF010203u, adapter.DefaultForegroundOverrideArgb);
        Assert.Equal(0xFF040506u, adapter.DefaultBackgroundOverrideArgb);
        Assert.True(adapter.ReverseVideo);
    }
}
