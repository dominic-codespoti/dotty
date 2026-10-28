using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dotty.NativePty.Unix;
using FluentAssertions;
using Xunit;

namespace Dotty.NativePty.Tests;

/// <summary>Pixel-size plumbing of the Unix PTY: control-line format and the winsize the child observes.</summary>
public class UnixPtyPixelTests : IDisposable
{
    private const uint TIOCGWINSZ = 0x5413;

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, byte[] data);

    private UnixPty? _pty;

    public void Dispose() => PtyTestHelpers.SafeCleanup(_pty);

    private static string Format(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        Span<byte> buffer = stackalloc byte[UnixPty.ResizeMessageMaxBytes];
        int length = UnixPty.FormatResizeMessage(buffer, cols, rows, pixelWidth, pixelHeight);
        return Encoding.UTF8.GetString(buffer[..length]);
    }

    [Fact]
    public void FormatResizeMessage_WritesExactNewlineTerminatedLine()
    {
        Format(120, 30, 960, 600)
            .Should().Be("{\"type\":\"resize\",\"cols\":120,\"rows\":30,\"xpixel\":960,\"ypixel\":600}\n");
    }

    [Fact]
    public void FormatResizeMessage_UnknownPixelsAreZero()
    {
        Format(80, 24, 0, 0)
            .Should().Be("{\"type\":\"resize\",\"cols\":80,\"rows\":24,\"xpixel\":0,\"ypixel\":0}\n");
    }

    [Theory]
    [InlineData(65535, 65535, 65535, 65535)]
    [InlineData(65536, 70000, 65535, 65535)]
    [InlineData(int.MaxValue, int.MaxValue, 65535, 65535)]
    [InlineData(-1, -65536, 0, 0)]
    [InlineData(int.MinValue, 1, 0, 1)]
    public void ClampPixels_StaysWithinUnsignedShort(int w, int h, int expectedW, int expectedH)
    {
        UnixPty.ClampPixels(w).Should().Be(expectedW);
        UnixPty.ClampPixels(h).Should().Be(expectedH);
    }

    [Fact]
    public void FormatResizeMessage_ClampsPixelsAndFitsWorstCaseBuffer()
    {
        string line = Format(int.MaxValue, int.MaxValue, int.MaxValue, -7);

        Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(UnixPty.ResizeMessageMaxBytes);
        using var doc = JsonDocument.Parse(line.TrimEnd('\n'));
        doc.RootElement.GetProperty("xpixel").GetInt32().Should().Be(65535);
        doc.RootElement.GetProperty("ypixel").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("cols").GetInt32().Should().Be(int.MaxValue);
    }

    [Fact]
    public void FormatResizeMessage_ProducesOneParseableJsonLine()
    {
        string line = Format(7, 9, 1234, 5678);

        line.Count(c => c == '\n').Should().Be(1);
        line.EndsWith('\n').Should().BeTrue();
        using var doc = JsonDocument.Parse(line.TrimEnd('\n'));
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("resize");
        root.GetProperty("cols").GetInt32().Should().Be(7);
        root.GetProperty("rows").GetInt32().Should().Be(9);
        root.GetProperty("xpixel").GetInt32().Should().Be(1234);
        root.GetProperty("ypixel").GetInt32().Should().Be(5678);
    }

    // ---- Real helper: what the child's tty reports -------------------------------------------------

    private static (int Rows, int Cols, int XPixel, int YPixel)? ReadWinsize(int helperPid)
    {
        string childrenPath = $"/proc/{helperPid}/task/{helperPid}/children";
        if (!File.Exists(childrenPath))
            return null;
        int child = File.ReadAllText(childrenPath)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static s => int.TryParse(s, out int id) ? id : -1)
            .FirstOrDefault(static id => id > 0);
        if (child <= 0)
            return null;

        try
        {
            using var tty = File.OpenRead($"/proc/{child}/fd/0");
            var size = new byte[8];
            if (ioctl(tty.SafeFileHandle.DangerousGetHandle().ToInt32(), TIOCGWINSZ, size) != 0)
                return null;
            return (BitConverter.ToUInt16(size, 0), BitConverter.ToUInt16(size, 2),
                BitConverter.ToUInt16(size, 4), BitConverter.ToUInt16(size, 6));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (int Rows, int Cols, int XPixel, int YPixel)? WaitForWinsize(
        int helperPid, Func<(int Rows, int Cols, int XPixel, int YPixel), bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        (int Rows, int Cols, int XPixel, int YPixel)? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(6))
        {
            last = ReadWinsize(helperPid);
            if (last is { } size && predicate(size))
                return size;
            Thread.Sleep(50);
        }
        return last;
    }

    [ConditionalFacts.LinuxOnlyFact]
    public void Start_InitialPixelSize_IsVisibleToChild()
    {
        _pty = new UnixPty();
        _pty.Start(
            command: new[] { "/bin/sh", "-c", "sleep 30" },
            columns: 100, rows: 30, initialPixelWidth: 1000, initialPixelHeight: 600);

        var size = WaitForWinsize(_pty.ProcessId, s => s.XPixel != 0 || s.YPixel != 0);

        size.Should().NotBeNull();
        size!.Value.Should().Be((30, 100, 1000, 600));
    }

    [ConditionalFacts.LinuxOnlyFact]
    public void Start_WithoutPixelSize_ReportsZeroPixels()
    {
        _pty = new UnixPty();
        _pty.Start(command: new[] { "/bin/sh", "-c", "sleep 30" }, columns: 90, rows: 20);

        var size = WaitForWinsize(_pty.ProcessId, s => s.Cols == 90);

        size.Should().NotBeNull();
        size!.Value.Should().Be((20, 90, 0, 0));
    }

    [ConditionalFacts.LinuxOnlyFact]
    public void Resize_PixelSize_IsVisibleToChildAndPixelOnlyChangeApplies()
    {
        _pty = new UnixPty();
        _pty.Start(command: new[] { "/bin/sh", "-c", "sleep 30" }, columns: 80, rows: 24);

        _pty.Resize(100, 30, 1000, 600);
        var first = WaitForWinsize(_pty.ProcessId, s => s.XPixel == 1000);
        first.Should().NotBeNull();
        first!.Value.Should().Be((30, 100, 1000, 600));

        // Same grid, different pixels (zoom / DPI change).
        _pty.Resize(100, 30, 1100, 660);
        var second = WaitForWinsize(_pty.ProcessId, s => s.XPixel == 1100);
        second.Should().NotBeNull();
        second!.Value.Should().Be((30, 100, 1100, 660));

        // Oversized values are clamped to the unsigned short range rather than wrapping.
        _pty.Resize(100, 30, 70000, 66000);
        var clamped = WaitForWinsize(_pty.ProcessId, s => s.XPixel == 65535);
        clamped.Should().NotBeNull();
        clamped!.Value.Should().Be((30, 100, 65535, 65535));
    }
}
