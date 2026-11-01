using Dotty.Runtime.Input;
using Xunit;

namespace Dotty.App.Tests;

public class DropPathEscaperTests
{
    [Theory]
    [InlineData("/home/u/file.txt", "/home/u/file.txt")]
    [InlineData("/home/u/my file.txt", "'/home/u/my file.txt'")]
    [InlineData("/tmp/it's", "'/tmp/it'\\''s'")]
    [InlineData("/tmp/$(rm -rf ~)", "'/tmp/$(rm -rf ~)'")]
    [InlineData("/tmp/`id`", "'/tmp/`id`'")]
    [InlineData("/tmp/a;b&c|d", "'/tmp/a;b&c|d'")]
    [InlineData("/tmp/x'; rm -rf / #", "'/tmp/x'\\''; rm -rf / #'")]
    [InlineData("~/notexpanded", "'~/notexpanded'")]
    [InlineData("-rf", "'-rf'")]
    [InlineData("/tmp/*.txt", "'/tmp/*.txt'")]
    [InlineData("/tmp/日本語 ファイル", "'/tmp/日本語 ファイル'")]
    [InlineData("/tmp/!history", "'/tmp/!history'")]
    public void Posix(string path, string expected) => AssertEscaped(path, ShellQuoteStyle.Posix, expected);

    [Theory]
    [InlineData("/home/u/file.txt", "/home/u/file.txt")]
    [InlineData("/tmp/it's", "'/tmp/it\\'s'")]
    [InlineData("/tmp/back\\slash", "'/tmp/back\\\\slash'")]
    [InlineData("/tmp/(echo hi)", "'/tmp/(echo hi)'")]
    [InlineData("/tmp/x\\'; rm -rf / #", "'/tmp/x\\\\\\'; rm -rf / #'")]
    [InlineData("%self", "'%self'")]
    public void Fish(string path, string expected) => AssertEscaped(path, ShellQuoteStyle.Fish, expected);

    [Theory]
    [InlineData(@"C:\Users\u\file.txt", @"'C:\Users\u\file.txt'")]
    [InlineData(@"C:\it's", @"'C:\it''s'")]
    [InlineData("C:\\a\u2019; calc; \u2019", "'C:\\a\u2019\u2019; calc; \u2019\u2019'")]
    [InlineData(@"C:\$(calc)", @"'C:\$(calc)'")]
    public void PowerShell(string path, string expected) => AssertEscaped(path, ShellQuoteStyle.PowerShell, expected);

    [Theory]
    [InlineData(@"C:\Program Files\x.txt", "\"C:\\Program Files\\x.txt\"")]
    [InlineData(@"C:\a&calc", "\"C:\\a&calc\"")]
    public void Cmd(string path, string expected) => AssertEscaped(path, ShellQuoteStyle.Cmd, expected);

    [Theory]
    [InlineData("C:\\a\"&calc", ShellQuoteStyle.Cmd)]
    [InlineData(@"C:\%PATH%", ShellQuoteStyle.Cmd)]
    [InlineData(@"C:\a!b!", ShellQuoteStyle.Cmd)]
    [InlineData("/tmp/a\nrm -rf /", ShellQuoteStyle.Posix)]
    [InlineData("/tmp/a\rb", ShellQuoteStyle.Fish)]
    [InlineData("/tmp/\u001b]52;c;Zm9v\u0007", ShellQuoteStyle.Posix)]
    [InlineData("/tmp/a\u0000b", ShellQuoteStyle.PowerShell)]
    [InlineData("/tmp/a\u007fb", ShellQuoteStyle.Posix)]
    [InlineData("/tmp/a\u009bb", ShellQuoteStyle.Posix)]
    [InlineData("/tmp/a\u2028b", ShellQuoteStyle.Posix)]
    [InlineData("", ShellQuoteStyle.Posix)]
    public void Rejects(string path, ShellQuoteStyle style) =>
        Assert.False(DropPathEscaper.TryEscape(path, style, out _));

    [Fact]
    public void LoneSurrogateIsRejected() =>
        Assert.False(DropPathEscaper.TryEscape("/tmp/a" + (char)0xD800 + "b", ShellQuoteStyle.Posix, out _));

    [Fact]
    public void SurrogatePairsAreKept() => AssertEscaped("/tmp/😀", ShellQuoteStyle.Posix, "'/tmp/😀'");

    [Fact]
    public void Build_JoinsAndSkipsRejected()
    {
        string? text = DropPathEscaper.Build(["/a", "/b c", "/bad\n", null], ShellQuoteStyle.Posix, out int rejected);
        Assert.Equal("/a '/b c'", text);
        Assert.Equal(2, rejected);
        Assert.Null(DropPathEscaper.Build(["/x\u0007"], ShellQuoteStyle.Posix, out rejected));
        Assert.Equal(1, rejected);
    }

    [Theory]
    [InlineData("auto", "/usr/bin/fish", false, ShellQuoteStyle.Fish)]
    [InlineData("auto", "/bin/zsh", false, ShellQuoteStyle.Posix)]
    [InlineData("auto", @"C:\Program Files\PowerShell\7\pwsh.exe", true, ShellQuoteStyle.PowerShell)]
    [InlineData("auto", "powershell.exe", true, ShellQuoteStyle.PowerShell)]
    [InlineData("auto", @"C:\Windows\System32\CMD.EXE", true, ShellQuoteStyle.Cmd)]
    [InlineData("auto", null, true, ShellQuoteStyle.Cmd)]
    [InlineData("auto", null, false, ShellQuoteStyle.Posix)]
    [InlineData("bogus", "/usr/bin/fish", false, ShellQuoteStyle.Fish)]
    [InlineData("POSIX", "/usr/bin/fish", false, ShellQuoteStyle.Posix)]
    [InlineData("cmd", "/bin/bash", false, ShellQuoteStyle.Cmd)]
    public void ResolveStyle(string quote, string? shell, bool windows, ShellQuoteStyle expected) =>
        Assert.Equal(expected, DropPathEscaper.ResolveStyle(quote, shell, windows));

    private static void AssertEscaped(string path, ShellQuoteStyle style, string expected)
    {
        Assert.True(DropPathEscaper.TryEscape(path, style, out string? escaped));
        Assert.Equal(expected, escaped);
    }
}
