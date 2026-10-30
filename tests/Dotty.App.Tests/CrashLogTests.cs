using Dotty.Runtime.Diagnostics;
using Xunit;

namespace Dotty.App.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dotty-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static CrashEntry Entry(string details) => new(
        new DateTimeOffset(2026, 10, 30, 13, 4, 5, 678, TimeSpan.FromHours(2)),
        "unhandled exception (process terminating)",
        "0.3.0",
        ".NET 11.0.0 (linux-x64)",
        "Linux 6.6 (Arm64)",
        details);

    [Fact]
    public void FormatWritesAHeaderThenTheDetails()
    {
        string text = CrashLog.Format(Entry("System.InvalidOperationException: boom\r\n   at A.B()\r\n"));

        Assert.Equal(
            "=== Dotty crash report 2026-10-30T11:04:05.678Z ===\n" +
            "Source:  unhandled exception (process terminating)\n" +
            "Version: 0.3.0\n" +
            "Runtime: .NET 11.0.0 (linux-x64)\n" +
            "OS:      Linux 6.6 (Arm64)\n" +
            "\n" +
            "System.InvalidOperationException: boom\n" +
            "   at A.B()\n" +
            "\n",
            text);
    }

    [Fact]
    public void FormatBoundsTheDetails()
    {
        string details = new('x', CrashLog.MaxDetailChars + 10);

        string text = CrashLog.Format(Entry(details));

        Assert.Contains(new string('x', CrashLog.MaxDetailChars) + "\n[10 more characters omitted]\n\n", text);
        Assert.DoesNotContain(new string('x', CrashLog.MaxDetailChars + 1), text);
    }

    [Theory]
    [InlineData(0, 500, 100, false)]
    [InlineData(50, 50, 100, false)]
    [InlineData(51, 50, 100, true)]
    [InlineData(100, 1, 100, true)]
    public void RotationStartsWhenAnAppendWouldPassTheLimit(long existing, long entry, long max, bool expected)
    {
        Assert.Equal(expected, CrashLog.ShouldRotate(existing, entry, max));
    }

    [Theory]
    [InlineData(0, "crash.log")]
    [InlineData(1, "crash.1.log")]
    [InlineData(2, "crash.2.log")]
    public void RotatedFilesAreNumbered(int generation, string expected)
    {
        Assert.Equal(expected, CrashLog.RotatedFileName(generation));
    }

    [Fact]
    public void LogsLiveInTheLogsDirectoryOfTheStateRoot()
    {
        Assert.Equal(Path.Combine("/state/dotty", "logs"), CrashLog.DirectoryFor("/state/dotty"));
    }

    [Fact]
    public void AppendRotatesAndKeepsAtMostMaxFiles()
    {
        var log = new CrashLog(_directory, maxFileBytes: 10, maxFiles: 3);

        Assert.Equal(Path.Combine(_directory, "crash.log"), log.Append("first\n"));
        log.Append("second\n");
        log.Append("third\n");
        log.Append("fourth\n");

        Assert.Equal("fourth\n", File.ReadAllText(Path.Combine(_directory, "crash.log")));
        Assert.Equal("third\n", File.ReadAllText(Path.Combine(_directory, "crash.1.log")));
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(_directory, "crash.2.log")));
        Assert.Equal(3, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void AppendKeepsSmallEntriesTogether()
    {
        var log = new CrashLog(_directory, maxFileBytes: 100, maxFiles: 2);

        log.Append("a\n");
        log.Append("b\n");

        Assert.Equal("a\nb\n", File.ReadAllText(log.CurrentPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ASingleFileLogStartsOverWhenFull()
    {
        var log = new CrashLog(_directory, maxFileBytes: 4, maxFiles: 1);

        log.Append("aaa\n");
        log.Append("bbb\n");

        Assert.Equal("bbb\n", File.ReadAllText(log.CurrentPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ReportingBeforeInstallWritesNothing()
    {
        Assert.Null(CrashReporter.Log);
        Assert.Null(CrashReporter.Report("test", "details"));
        CrashReporter.ReportGlfwError("10008", "test");
    }
}
