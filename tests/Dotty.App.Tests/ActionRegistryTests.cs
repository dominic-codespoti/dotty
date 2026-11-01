using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dotty.Abstractions.Config;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ActionRegistryTests
{
    [Fact]
    public void EveryActionHasExactlyOneRow_WithAUniqueSnakeCaseName()
    {
        var actions = Enum.GetValues<TerminalAction>();
        Assert.Equal(actions.Length, ActionRegistry.All.Count);
        Assert.Equal(actions, ActionRegistry.All.Select(i => i.Action));
        Assert.Equal(ActionRegistry.All.Count, ActionRegistry.All.Select(i => i.Name).Distinct().Count());
        foreach (var info in ActionRegistry.All)
        {
            Assert.Matches("^[a-z]+(_[a-z0-9]+)*$", info.Name);
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
            Assert.Equal(info.Action == TerminalAction.SendText, info.TakesArgument);
        }
    }

    [Theory]
    [InlineData("copy", TerminalAction.Copy)]
    [InlineData("Copy", TerminalAction.Copy)]
    [InlineData(" COPY ", TerminalAction.Copy)]
    [InlineData("new_tab", TerminalAction.NewTab)]
    [InlineData("NewTab", TerminalAction.NewTab)]
    [InlineData("switch_tab_3", TerminalAction.SwitchTab3)]
    [InlineData("none", TerminalAction.None)]
    [InlineData("copy_or_send", TerminalAction.CopyOrSend)]
    public void TryParse_AcceptsSnakeAndEnumNames(string text, TerminalAction expected)
    {
        Assert.True(ActionRegistry.TryParse(text, out var action, out string? error), error);
        Assert.Equal(new BoundAction(expected), action);
    }

    [Theory]
    [InlineData("send_text:\\x01", "\x01")]
    [InlineData("send_text:\\e[A", "\x1b[A")]
    [InlineData("send_text:hi\\r\\n", "hi\r\n")]
    [InlineData("send_text:a\\\\b", "a\\b")]
    [InlineData("send_text:\\u{1F600}", "😀")]
    [InlineData("send_text:\\u{e9}", "é")]
    [InlineData("send_text:\\t\\a\\b\\f\\v\\0", "\t\a\b\f\v\0")]
    [InlineData("send_text:a:b", "a:b")]
    [InlineData("SendText:x", "x")]
    public void TryParse_SendTextUnescapes(string text, string expected)
    {
        Assert.True(ActionRegistry.TryParse(text, out var action, out string? error), error);
        Assert.Equal(new BoundAction(TerminalAction.SendText, expected), action);
    }

    [Theory]
    [InlineData(null, "empty action")]
    [InlineData("", "empty action")]
    [InlineData("frobnicate", "unknown action 'frobnicate'")]
    [InlineData("copy:x", "takes no argument")]
    [InlineData("send_text", "needs an argument")]
    [InlineData("send_text:", "needs an argument")]
    [InlineData("send_text:\\q", "unknown escape")]
    [InlineData("send_text:\\x1", "two hex digits")]
    [InlineData("send_text:\\xzz", "two hex digits")]
    [InlineData("send_text:abc\\", "lone backslash")]
    [InlineData("send_text:\\u1234", "needs braces")]
    [InlineData("send_text:\\u{}", "1-6 hex digits")]
    [InlineData("send_text:\\u{D800}", "Unicode scalar")]
    [InlineData("send_text:\\u{110000}", "Unicode scalar")]
    [InlineData("send_text:\\u{1234567}", "1-6 hex digits")]
    [InlineData("send_text:\\u{41", "1-6 hex digits")]
    public void TryParse_RejectsWithReason(string? text, string reason)
    {
        Assert.False(ActionRegistry.TryParse(text, out _, out string? error));
        Assert.Contains(reason, error);
    }

    // A lone surrogate cannot round-trip through InlineData (xunit replaces it with U+FFFD), so it is built here.
    [Fact]
    public void TryParse_RejectsLoneSurrogate()
    {
        Assert.False(ActionRegistry.TryParse("send_text:a" + '\uD800', out _, out string? error));
        Assert.Contains("lone surrogate", error);
    }

    [Theory]
    [InlineData("\x01")]
    [InlineData("\x1b[1;5A")]
    [InlineData("back\\slash")]
    [InlineData("tab\there\x7f")]
    [InlineData("c1\u0085\u009b")]
    [InlineData("emoji 😀 é")]
    public void Format_RoundTripsThroughTryParse(string text)
    {
        var bound = new BoundAction(TerminalAction.SendText, text);
        string formatted = ActionRegistry.Format(bound);
        Assert.DoesNotContain(formatted, c => char.IsControl(c));
        Assert.True(ActionRegistry.TryParse(formatted, out var parsed, out _));
        Assert.Equal(bound, parsed);
    }

    /// <summary>docs/Configuration.md lists every action exactly once, by its registry name.</summary>
    [Fact]
    public void ConfigurationDocs_ListEveryAction()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Dotty.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        string docs = File.ReadAllText(Path.Combine(root, "docs", "Configuration.md"));
        int start = docs.IndexOf("### `keybindings`", StringComparison.Ordinal);
        int end = docs.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        string section = docs[start..end];
        var documented = Regex.Matches(section, @"^\| `([a-z0-9_]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(ActionRegistry.All.Select(i => i.Name).Order(), documented.Order());
    }
}
