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

    [Fact]
    public void Parser_RecognizesExplainConfigWithoutArguments()
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(["+explain-config"]);

        Assert.Equal("+explain-config", options.Action);
        Assert.Empty(options.ActionArguments);
        Assert.Null(options.Command);
        Assert.Null(options.Shell);
        Assert.False(options.ShowHelp);
    }

    [Fact]
    public void Parser_PassesTheOptionNameToExplainConfig()
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(["+explain-config", "font.size"]);

        Assert.Equal("+explain-config", options.Action);
        Assert.Equal(new[] { "font.size" }, options.ActionArguments);
    }

    [Fact]
    public void Parser_LaunchOptionsHaveNoAction()
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(["--shell", "/bin/sh"]);

        Assert.Null(options.Action);
        Assert.Empty(options.ActionArguments);
    }

    [Fact]
    public void Parser_RejectsUnknownActions()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DottyLaunchOptionsParser.Parse(["+bogus"]));

        Assert.Contains("Unknown action '+bogus'", error.Message);
    }

    [Fact]
    public void Parser_RejectsMoreThanOneAction()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DottyLaunchOptionsParser.Parse(["+explain-config", "+explain-config"]));

        Assert.Contains("Only one action", error.Message);
    }

    [Fact]
    public void Parser_RejectsActionArgumentWithoutAnAction()
    {
        Assert.Throws<ArgumentException>(() => DottyLaunchOptionsParser.Parse(["font.size"]));
    }

    [Fact]
    public void Parser_RejectsMoreThanOneExplainConfigOption()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DottyLaunchOptionsParser.Parse(["+explain-config", "font.size", "font.family"]));

        Assert.Contains("at most one", error.Message);
    }

    [Theory]
    [InlineData("+explain-config", "--shell", "x")]
    [InlineData("--shell", "x", "+explain-config")]
    [InlineData("+explain-config", "-d", "/tmp")]
    [InlineData("-d", "/tmp", "+explain-config", "font.size")]
    [InlineData("+explain-config", "--", "echo")]
    public void Parser_RejectsActionsCombinedWithLaunchOptions(params string[] args)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            DottyLaunchOptionsParser.Parse(args));

        Assert.Contains("cannot be combined", error.Message);
    }

    [Fact]
    public void Parser_RejectsUnknownOptionAfterAnAction()
    {
        Assert.Throws<ArgumentException>(() => DottyLaunchOptionsParser.Parse(["+explain-config", "--bogus"]));
    }

    [Fact]
    public void Parser_KeepsHelpAlongsideAnAction()
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(["+explain-config", "--help"]);

        Assert.True(options.ShowHelp);
        Assert.Equal("+explain-config", options.Action);
    }

    [Fact]
    public void Parser_DoesNotTreatCommandArgumentsAfterSeparatorAsActions()
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(["--", "tool", "+explain-config"]);

        Assert.Null(options.Action);
        Assert.Equal(new[] { "tool", "+explain-config" }, options.Command);
    }

    [Fact]
    public void HelpTextDocumentsTheExplainConfigAction()
    {
        Assert.Contains("dotty +explain-config [OPTION]", DottyLaunchOptionsParser.HelpText);
    }
}
