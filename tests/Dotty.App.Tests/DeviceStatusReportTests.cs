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
        adapter.ReplyRequested += r => reply = r;

        adapter.OnDeviceStatusReport(6);

        Assert.Equal("\u001b[1;1R", reply);
    }

    [Fact]
    public void OnCursorPositionReport_EmitsPrivateCpr()
    {
        var adapter = new TerminalAdapter(rows: 24, columns: 80);
        string? reply = null;
        adapter.ReplyRequested += r => reply = r;

        adapter.OnCursorPositionReport();

        Assert.Equal("\u001b[?1;1R", reply);
    }
}
