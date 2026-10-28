using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class ShellIntegrationOsc7Tests
{
    private static string Uri(string unixPath, string windowsPath) =>
        OperatingSystem.IsWindows() ? windowsPath : unixPath;

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Changes) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var changes = new List<string>();
        adapter.CurrentWorkingDirectoryChanged += changes.Add;
        return (adapter, parser, changes);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    private static string UriA => Uri("file:///tmp/a", "file:///C:/a");
    private static string UriB => Uri("file:///tmp/b", "file:///C:/b");

    [Fact]
    public void BelAndStTerminatedPayloadsSetTheValueAndFireOnce()
    {
        var (adapter, parser, changes) = Setup();
        Feed(parser, "\u001b]7;" + UriA + "\a");
        Assert.Equal(Uri("/tmp/a", @"C:\a"), adapter.CurrentWorkingDirectory);
        Feed(parser, "\u001b]7;" + UriB + "\u001b\\");
        Assert.Equal(Uri("/tmp/b", @"C:\b"), adapter.CurrentWorkingDirectory);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void RepeatedValueDoesNotRefire()
    {
        var (_, parser, changes) = Setup();
        Feed(parser, "\u001b]7;" + UriA + "\a");
        Feed(parser, "\u001b]7;" + UriA + "\a");
        Assert.Single(changes);
        Feed(parser, "\u001b]7;" + UriB + "\a");
        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData("file:///tmp/x%00y")]
    [InlineData("file://remote.example/tmp/x")]
    [InlineData("http://localhost/tmp/x")]
    [InlineData("file:///tmp/x%1B%5B")]
    [InlineData("file:///tmp/x\u0001y")]
    public void InvalidPayloadKeepsPreviousValueAndFiresNothing(string payload)
    {
        var (adapter, parser, changes) = Setup();
        Feed(parser, "\u001b]7;" + UriA + "\a");
        string? before = adapter.CurrentWorkingDirectory;
        changes.Clear();
        Feed(parser, "\u001b]7;" + payload + "\a");
        Assert.Equal(before, adapter.CurrentWorkingDirectory);
        Assert.Empty(changes);
    }

    [Fact]
    public void OversizedPayloadsAreIgnored()
    {
        var (adapter, parser, changes) = Setup();
        // Above the parser's own OSC cap: discarded by the parser.
        Feed(parser, "\u001b]7;file:///" + new string('a', 70_000) + "\a");
        // Between Osc7WorkingDirectory.MaxPayloadLength and the parser cap: rejected by TryParse.
        Feed(parser, "\u001b]7;file:///" + new string('a', Osc7WorkingDirectory.MaxPayloadLength + 100) + "\a");
        Assert.Null(adapter.CurrentWorkingDirectory);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData("file:///home/u/My%20Dir%20%281%29")] // dotty.bash / dotty.zsh / dotty.fish url-escaped shape
    [InlineData("file:///home/u/My%20Dir%20(1)")]
    public void ShellIntegrationScriptShapesAreAccepted(string payload)
    {
        if (OperatingSystem.IsWindows()) return;
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b]7;" + payload + "\u001b\\");
        Assert.Equal("/home/u/My Dir (1)", adapter.CurrentWorkingDirectory);
    }
}
