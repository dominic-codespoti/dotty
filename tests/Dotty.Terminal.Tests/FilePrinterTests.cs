using System;
using System.IO;
using System.Text;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class FilePrinterTests
{
    [Fact]
    public void PrintScreen_WritesGraphemeTextAndFormFeedToConfiguredFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dotty-printer-{Guid.NewGuid():N}.txt");
        using var adapter = new TerminalAdapter(rows: 2, columns: 20);
        try
        {
            adapter.ConfigurePrinterOutput(path);
            Assert.Equal(PrinterStatus.Ready, adapter.PrinterStatus);
            adapter.OnPrint("Aé界e\u0301");
            adapter.OnSetPrinterMode(18, enabled: true);
            adapter.OnSetPrinterMode(19, enabled: true);
            Assert.True(adapter.PrintFormFeedEnabled);
            Assert.True(adapter.PrintExtentFullPage);

            adapter.OnPrintScreen();
            adapter.DisposePrinterOutput();

            Assert.Equal("Aé界e\u0301\n\n\f", File.ReadAllText(path, Encoding.UTF8));
            Assert.Equal(PrinterStatus.Unavailable, adapter.PrinterStatus);
        }
        finally
        {
            adapter.DisposePrinterOutput();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DisabledPrinterWritesNothingAndUnavailableSinkIsNotReady()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dotty-printer-{Guid.NewGuid():N}.txt");
        using var adapter = new TerminalAdapter(rows: 1, columns: 8);
        adapter.OnPrint("ignored");
        adapter.OnPrintScreen();
        Assert.Equal(PrinterStatus.Unavailable, adapter.PrinterStatus);
        Assert.False(File.Exists(path));

        adapter.ConfigurePrinterOutput(Path.Combine(path, "missing", "printer.txt"));
        Assert.Equal(PrinterStatus.NotReady, adapter.PrinterStatus);
        adapter.OnPrintScreen();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void PrintScreen_DefaultExtentUsesCurrentScrollRegion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dotty-printer-{Guid.NewGuid():N}.txt");
        using var adapter = new TerminalAdapter(rows: 4, columns: 12);
        try
        {
            adapter.ConfigurePrinterOutput(path);
            adapter.Buffer.SetCursor(0, 0);
            adapter.OnPrint("first");
            adapter.Buffer.SetCursor(1, 0);
            adapter.OnPrint("middle");
            adapter.Buffer.SetCursor(2, 0);
            adapter.OnPrint("third");
            adapter.Buffer.SetCursor(3, 0);
            adapter.OnPrint("last");
            adapter.OnSetScrollRegion(2, 3);

            adapter.OnPrintScreen();
            adapter.DisposePrinterOutput();

            Assert.Equal("middle\nthird\n", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            adapter.DisposePrinterOutput();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FormFeedModeResetChangesSubsequentPrintedData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dotty-printer-{Guid.NewGuid():N}.txt");
        using var adapter = new TerminalAdapter(rows: 1, columns: 8);
        try
        {
            adapter.ConfigurePrinterOutput(path);
            adapter.OnSetPrinterMode(18, enabled: true);
            adapter.OnPrint("x");
            adapter.OnPrintScreen();
            adapter.OnSetPrinterMode(18, enabled: false);
            adapter.OnPrintScreen();
            adapter.DisposePrinterOutput();
            Assert.Equal("x\n\fx\n", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            adapter.DisposePrinterOutput();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReconfiguredSinkDrainsAcceptedOldWritesAndUsesNewFile()
    {
        string oldPath = Path.Combine(Path.GetTempPath(), $"dotty-printer-old-{Guid.NewGuid():N}.txt");
        string newPath = Path.Combine(Path.GetTempPath(), $"dotty-printer-new-{Guid.NewGuid():N}.txt");
        using var adapter = new TerminalAdapter(rows: 1, columns: 8);
        try
        {
            adapter.ConfigurePrinterOutput(oldPath);
            adapter.OnPrint("old");
            adapter.OnPrintScreen();
            adapter.ConfigurePrinterOutput(newPath);
            adapter.Buffer.SetCursor(0, 0);
            adapter.OnPrint("new");
            adapter.OnPrintScreen();
            adapter.DisposePrinterOutput();

            Assert.Equal("old\n", File.ReadAllText(oldPath, Encoding.UTF8));
            Assert.Equal("new\n", File.ReadAllText(newPath, Encoding.UTF8));
        }
        finally
        {
            adapter.DisposePrinterOutput();
            if (File.Exists(oldPath)) File.Delete(oldPath);
            if (File.Exists(newPath)) File.Delete(newPath);
        }
    }

    [Fact]
    public void MultipleAdaptersAppendAcceptedJobsInGlobalFifoOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dotty-printer-shared-{Guid.NewGuid():N}.txt");
        using var first = new TerminalAdapter(rows: 1, columns: 12);
        using var second = new TerminalAdapter(rows: 1, columns: 12);
        try
        {
            first.ConfigurePrinterOutput(path);
            second.ConfigurePrinterOutput(path);
            first.OnPrint("first");
            second.OnPrint("second");

            first.OnPrintScreen();
            second.OnPrintScreen();
            first.DisposePrinterOutput();
            second.DisposePrinterOutput();

            Assert.Equal("first\nsecond\n", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            first.DisposePrinterOutput();
            second.DisposePrinterOutput();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
