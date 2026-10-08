using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class RenderRequestInvariantTests
{
    private const string Seed = "12345678\r\nabcdefgh\r\nABCDEFGH\r\nijklmnop";

    [Theory]
    [InlineData("Printable ASCII", "Z")]
    [InlineData("UTF-8 text", "é漢")]
    [InlineData("Automatic wrapping", "\u001b[?7h123456789")]
    [InlineData("Carriage return and bottom line feed scroll", "\u001b[4;1H\r\nZ")]
    [InlineData("Newline mode scroll", "\u001b[20h\u001b[4;1H\nZ")]
    [InlineData("CUP then text", "\u001b[2;3HQ")]
    [InlineData("CUU then text", "\u001b[2Aq")]
    [InlineData("CUD then text", "\u001b[2Bq")]
    [InlineData("CUF then text", "\u001b[2Cq")]
    [InlineData("CUB then text", "\u001b[2Dq")]
    [InlineData("ED parameter 0", "\u001b[1;1H\u001b[0J")]
    [InlineData("ED parameter 1", "\u001b[2;2H\u001b[1J")]
    [InlineData("ED parameter 2", "\u001b[2J")]
    [InlineData("EL parameter 0", "\u001b[2;2H\u001b[0K")]
    [InlineData("EL parameter 1", "\u001b[2;2H\u001b[1K")]
    [InlineData("EL parameter 2", "\u001b[2;2H\u001b[2K")]
    [InlineData("ICH then text", "\u001b[2;2H\u001b[2@Z")]
    [InlineData("DCH", "\u001b[2;2H\u001b[2P")]
    [InlineData("ECH", "\u001b[2;2H\u001b[2X")]
    [InlineData("IL", "\u001b[2;1H\u001b[2L")]
    [InlineData("DL", "\u001b[2;1H\u001b[2M")]
    [InlineData("DECSTBM and scroll up", "\u001b[2;3r\u001b[2S")]
    [InlineData("DECSTBM and scroll down", "\u001b[2;3r\u001b[2T")]
    [InlineData("SGR then text", "\u001b[31;1mZ")]
    [InlineData("Set tab stop and tab then text", "\u001b[1;1H\u001bH\tZ")]
    [InlineData("Alternate screen 1049 enter and leave", "\u001b[?1049hALT\u001b[?1049l")]
    [InlineData("Alternate screen 47 enter and leave", "\u001b[?47hALT\u001b[?47l")]
    [InlineData("Reverse index", "\u001b[1;1H\u001bM")]
    [InlineData("Save and restore cursor with text", "\u001b[s\u001b[2;2HQ\u001b[uZ")]
    [InlineData("Full reset RIS", "\u001b[c\u001b[31m\u001b[2J\u001bc")]
    [InlineData("DECALN", "\u001b#8")]
    public void VisibleBufferChangesAlwaysRequestRender(string name, string input)
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8, scrollbackCapacity: 16);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(Seed));
        adapter.FlushRender();

        int notifications = 0;
        adapter.RenderRequested += _ => notifications++;
        ulong before = adapter.Buffer.Generation;

        parser.Feed(Encoding.UTF8.GetBytes(input));
        adapter.FlushRender();

        ulong after = adapter.Buffer.Generation;
        Assert.True(after == before || notifications > 0,
            $"Input '{name}' ({Escape(input)}) changed buffer generation from {before} to {after} without RenderRequested.");
    }

    [Fact]
    public void ResizeBufferRequestsRenderWhenGenerationChanges()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8, scrollbackCapacity: 16);
        int notifications = 0;
        adapter.RenderRequested += _ => notifications++;
        ulong before = adapter.Buffer.Generation;

        adapter.ResizeBuffer(rows: 5, columns: 9);
        adapter.FlushRender();

        ulong after = adapter.Buffer.Generation;
        Assert.True(after == before || notifications > 0,
            $"Resize changed buffer generation from {before} to {after} without RenderRequested.");
    }

    private static string Escape(string value) => value
        .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
        .Replace("\r", "<CR>", StringComparison.Ordinal)
        .Replace("\n", "<LF>", StringComparison.Ordinal)
        .Replace("\t", "<TAB>", StringComparison.Ordinal);
}
