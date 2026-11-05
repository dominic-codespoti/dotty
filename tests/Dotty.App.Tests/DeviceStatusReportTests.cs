using System.Text;
using Dotty.Terminal.Parser;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class DeviceStatusReportTests
{
    [Fact]
    public void OnDeviceStatusReport6_EmitsStandardCpr()
    {
        var adapter = new TerminalAdapter(rows: 24, columns: 80);
        string? reply = null;
        adapter.ReplyRequested += r => reply = r.ToString();

        adapter.OnDeviceStatusReport(6);

        Assert.Equal("\u001b[1;1R", reply);
    }

    [Fact]
    public void OnCursorPositionReport_EmitsPrivateCpr()
    {
        var adapter = new TerminalAdapter(rows: 24, columns: 80);
        string? reply = null;
        adapter.ReplyRequested += r => reply = r.ToString();

        adapter.OnCursorPositionReport();

        Assert.Equal("\u001b[?1;1;1R", reply);
    }

    [Theory]
    [InlineData(false, "\u001b[2;2R")]
    [InlineData(true, "\u001b[?2;2;1R")]
    public void CursorRepliesReportBothAxesRelativeToTheOriginRegion(bool privateReport, string expected)
    {
        using var adapter = new TerminalAdapter(rows: 8, columns: 12);
        var parser = new BasicAnsiParser { Handler = adapter };
        string? reply = null;
        adapter.ReplyRequested += r => reply = r.ToString();
        parser.Feed(Encoding.ASCII.GetBytes("\u001b[2;7r\u001b[?69h\u001b[4;10s\u001b[?6h\u001b[2;2H"));
        Assert.Equal((2, 4), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));

        parser.Feed(Encoding.ASCII.GetBytes(privateReport ? "\u001b[?6n" : "\u001b[6n"));

        Assert.Equal(expected, reply);
    }
}
