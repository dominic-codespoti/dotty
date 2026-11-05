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

    [Theory]
    [InlineData("+explain-config [OPTION]")]
    [InlineData("+init-config [--force]")]
    [InlineData("+show-config [--default]")]
    [InlineData("+validate-config")]
    [InlineData("+list-themes")]
    [InlineData("+diagnose [--gl]")]
    public void HelpTextDocumentsEveryAction(string synopsis)
    {
        Assert.Contains("  " + synopsis, DottyLaunchOptionsParser.HelpText);
    }

    [Theory]
    [InlineData(new[] { "+init-config" }, "+init-config", "", "")]
    [InlineData(new[] { "+init-config", "--force" }, "+init-config", "", "--force")]
    [InlineData(new[] { "+show-config" }, "+show-config", "", "")]
    [InlineData(new[] { "+show-config", "--default" }, "+show-config", "", "--default")]
    [InlineData(new[] { "+validate-config" }, "+validate-config", "", "")]
    [InlineData(new[] { "+list-themes" }, "+list-themes", "", "")]
    [InlineData(new[] { "+diagnose" }, "+diagnose", "", "")]
    [InlineData(new[] { "+diagnose", "--gl" }, "+diagnose", "", "--gl")]
    [InlineData(new[] { "+explain-config", "vt" }, "+explain-config", "vt", "")]
    public void Parser_AcceptsEachActionWithItsOwnFlags(string[] args, string action, string argument, string flag)
    {
        DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(args);

        Assert.Equal(action, options.Action);
        Assert.Equal(argument.Length == 0 ? Array.Empty<string>() : new[] { argument }, options.ActionArguments);
        Assert.Equal(flag.Length == 0 ? Array.Empty<string>() : new[] { flag }, options.ActionFlags);
        if (flag.Length > 0)
            Assert.True(options.HasActionFlag(flag));
        Assert.False(options.HasActionFlag("--other"));
    }

    [Theory]
    [InlineData(new[] { "+show-config", "--force" }, "Unknown option")]
    [InlineData(new[] { "+init-config", "--default" }, "Unknown option")]
    [InlineData(new[] { "+validate-config", "--gl" }, "Unknown option")]
    [InlineData(new[] { "+explain-config", "--default" }, "Unknown option")]
    [InlineData(new[] { "--force", "+init-config" }, "Unknown option")]
    [InlineData(new[] { "+diagnose", "--gl", "--gl" }, "only once")]
    [InlineData(new[] { "+show-config", "font.size" }, "takes no arguments")]
    [InlineData(new[] { "+list-themes", "Dracula" }, "takes no arguments")]
    [InlineData(new[] { "+init-config", "--force", "-d", "/tmp" }, "cannot be combined")]
    [InlineData(new[] { "+validate-config", "+diagnose" }, "Only one action")]
    public void Parser_RejectsFlagsAndArgumentsAnActionDoesNotTake(string[] args, string message)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => DottyLaunchOptionsParser.Parse(args));

        Assert.Contains(message, error.Message);
    }
}
