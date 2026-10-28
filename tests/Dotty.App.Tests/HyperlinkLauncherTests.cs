using System;
using Dotty.Runtime.Hyperlinks;
using Xunit;

namespace Dotty.App.Tests;

public sealed class HyperlinkLauncherTests
{
    private static HyperlinkTarget Web(string canonical = "https://example.com/a%20b?x=1&y=2") =>
        new(canonical, "https", "example.com", null, HyperlinkSource.Detected);

    [Fact]
    public void Linux_UsesXdgOpenWithSingleArgumentAndNoShell()
    {
        var psi = HyperlinkLauncher.CreateStartInfo(Web(), HyperlinkPlatform.Linux);

        Assert.Equal("xdg-open", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "https://example.com/a%20b?x=1&y=2" }, psi.ArgumentList);
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void MacOS_UsesOpenWithSingleArgumentAndNoShell()
    {
        var psi = HyperlinkLauncher.CreateStartInfo(Web(), HyperlinkPlatform.MacOS);

        Assert.Equal("open", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "https://example.com/a%20b?x=1&y=2" }, psi.ArgumentList);
    }

    [Fact]
    public void Windows_ShellExecutesCanonicalUri()
    {
        var psi = HyperlinkLauncher.CreateStartInfo(Web(), HyperlinkPlatform.Windows);

        Assert.True(psi.UseShellExecute);
        Assert.Equal("https://example.com/a%20b?x=1&y=2", psi.FileName);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal(string.Empty, psi.Verb);
    }

    [Fact]
    public void Windows_FileTargetUsesValidatedLocalPath()
    {
        var target = new HyperlinkTarget("file:///C:/dir/ok.txt", "file", "", "C:\\dir\\ok.txt", HyperlinkSource.Detected);

        var psi = HyperlinkLauncher.CreateStartInfo(target, HyperlinkPlatform.Windows);

        Assert.Equal("C:\\dir\\ok.txt", psi.FileName);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void Unix_FileTargetPassesFileUri()
    {
        var target = new HyperlinkTarget("file:///tmp/a.txt", "file", "", "/tmp/a.txt", HyperlinkSource.Detected);

        var psi = HyperlinkLauncher.CreateStartInfo(target, HyperlinkPlatform.Linux);

        Assert.Equal(new[] { "file:///tmp/a.txt" }, psi.ArgumentList);
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("--help")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void NonSchemeCanonical_IsRejected(string canonical)
    {
        var target = new HyperlinkTarget(canonical, "https", "", null, HyperlinkSource.Detected);

        foreach (HyperlinkPlatform platform in Enum.GetValues<HyperlinkPlatform>())
            Assert.Throws<ArgumentException>(() => HyperlinkLauncher.CreateStartInfo(target, platform));
    }
}
