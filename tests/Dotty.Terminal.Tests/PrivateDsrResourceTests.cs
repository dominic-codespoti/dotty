using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PrivateDsrResourceTests
{
    [Fact]
    public void AbsentResourcesReportDefinedUnavailableStates()
    {
        var adapter = new TerminalAdapter();
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());

        adapter.OnPrivateDeviceStatusReport(25, 0);
        adapter.OnPrivateDeviceStatusReport(55, 0);
        adapter.OnPrivateDeviceStatusReport(56, 0);
        adapter.OnPrivateDeviceStatusReport(62, 0);
        adapter.OnPrivateDeviceStatusReport(75, 0);
        adapter.OnPrivateDeviceStatusReport(85, 0);

        Assert.Equal(new[]
        {
            "\u001b[?21n", "\u001b[?53n", "\u001b[?57;0n", "\u001b[0*{", "\u001b[?70n", "\u001b[?83n",
        }, replies);
    }

    [Fact]
    public void MacroChecksumKeepsIdentifierAndPrinterStateIsTruthful()
    {
        var adapter = new TerminalAdapter();
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());

        adapter.OnPrivateDeviceStatusReport(63, 123);
        adapter.OnPrivateDeviceStatusReport(15, 0);

        Assert.Equal("\u001bP123!~0000\u001b\\", replies[0]);
        Assert.Contains(replies[1], new[] { "\u001b[?10n", "\u001b[?11n", "\u001b[?13n" });
    }

    [Fact]
    public void KeyboardStatusUsesEffectiveConformanceAndHostKeyboardProfile()
    {
        var adapter = new TerminalAdapter();
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());

        adapter.OnPrivateDeviceStatusReport(26, 0);
        adapter.OnSetConformanceLevel(61, 1);
        adapter.OnPrivateDeviceStatusReport(26, 0);

        Assert.Equal(new[] { "\u001b[?27;0;0;5n", "\u001b[?27;0n" }, replies);
    }
}
