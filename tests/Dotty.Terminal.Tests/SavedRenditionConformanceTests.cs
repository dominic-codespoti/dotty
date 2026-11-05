using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class SavedRenditionConformanceTests
{
    private const string Esc = "\x1b";

    [Theory]
    [InlineData(Esc + "7", Esc + "8")]
    [InlineData(Esc + "[?1048h", Esc + "[?1048l")]
    public void RestoringEachScreenRepeatedlyRestoresItsOwnRendition(string save, string restore)
    {
        using var adapter = new TerminalAdapter(4, 12);
        var parser = new BasicAnsiParser { Handler = adapter };
        void Feed(string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

        Feed(Esc + "[1;31mM" + save);
        ushort mainStyle = adapter.Buffer.GetCell(0, 0).StyleId;
        Feed(Esc + "[?47h" + Esc + "[3;34mA" + save);
        ushort alternateStyle = adapter.Buffer.GetCell(0, 1).StyleId;
        Assert.NotEqual(mainStyle, alternateStyle);

        Feed(Esc + "[?47l" + Esc + "[0m" + restore + "X");
        Assert.Equal("MX", adapter.Buffer.GetRowText(0)[..2]);
        Assert.Equal(mainStyle, adapter.Buffer.GetCell(0, 1).StyleId);

        Feed(Esc + "[?47h" + Esc + "[0m" + restore + "Y");
        Assert.Equal(" AY", adapter.Buffer.GetRowText(0)[..3]);
        Assert.Equal(alternateStyle, adapter.Buffer.GetCell(0, 2).StyleId);

        Feed(Esc + "[?47l" + Esc + "[0m" + restore + "Z");
        Assert.Equal("MZ", adapter.Buffer.GetRowText(0)[..2]);
        Assert.Equal(mainStyle, adapter.Buffer.GetCell(0, 1).StyleId);
    }

    [Fact]
    public void Mode1049RestoresMainRenditionAsWellAsCursor()
    {
        using var adapter = new TerminalAdapter(4, 12);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(Esc + "[1;31mM"));
        ushort style = adapter.Buffer.GetCell(0, 0).StyleId;
        parser.Feed(Encoding.UTF8.GetBytes(Esc + "[?1049h" + Esc + "[3;34mALT" + Esc + "[?1049lB"));
        Assert.Equal("MB", adapter.Buffer.GetRowText(0)[..2]);
        Assert.Equal(style, adapter.Buffer.GetCell(0, 1).StyleId);
    }

    [Fact]
    public void AlternateSoftResetDoesNotReplaceMainSavedRendition()
    {
        using var adapter = new TerminalAdapter(4, 12);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(Esc + "[1;31mM" + Esc + "7"));
        ushort style = adapter.Buffer.GetCell(0, 0).StyleId;
        parser.Feed(Encoding.UTF8.GetBytes(Esc + "[?47h" + Esc + "[!p" + Esc + "[?47l" + Esc + "8X"));
        Assert.Equal("MX", adapter.Buffer.GetRowText(0)[..2]);
        Assert.Equal(style, adapter.Buffer.GetCell(0, 1).StyleId);
    }
}
