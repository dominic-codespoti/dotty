using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DottyLaunchOptionsTests
{
    [Fact]
    public void Parser_PreservesCommandArgumentsAfterSeparatorExactly()
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(
            ["-d", "relative cwd", "--", "program name", "", "--version", "a b"]);

        Assert.Equal("relative cwd", options.WorkingDirectory);
        Assert.Equal(new[] { "program name", "", "--version", "a b" }, options.Command);
        Assert.False(options.ShowVersion);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parser_RecognizesHelpWithoutPreflight(string flag)
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse([flag]);

        Assert.True(options.ShowHelp);
        Assert.Null(options.WorkingDirectory);
        Assert.Null(options.Command);
    }

    [Fact]
    public void Parser_RejectsShellAndCommandTogether()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DottyLaunchOptionsParser.Parse(["--shell", "/bin/sh", "--", "/bin/echo"]));

        Assert.Contains("cannot be combined", error.Message);
    }

    [Fact]
    public void Parser_RejectsCommandWithoutSeparator()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DottyLaunchOptionsParser.Parse(["--version", "program"]));

        Assert.Contains("Use '--'", error.Message);
    }
}
