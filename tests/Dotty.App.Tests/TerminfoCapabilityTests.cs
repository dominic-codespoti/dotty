using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dotty.Runtime.Input;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Keeps <c>terminfo/xterm-dotty.src</c>, <see cref="TerminfoCapabilities"/> (which answers XTGETTCAP) and the
/// implementation in step: every capability the entry advertises is declared in the table with the same value, is
/// answered by XTGETTCAP, and does what its value claims when run through Dotty.
/// </summary>
public sealed class TerminfoCapabilityTests
{
    private const string SourceRelativePath = "terminfo/xterm-dotty.src";

    private sealed record TerminfoSource(string[] Names, List<(string Name, string? Value)> Capabilities, List<string> Uses);

    private static TerminfoSource ReadSource()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, SourceRelativePath)))
            directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        Assert.True(directory is not null, $"{SourceRelativePath} not found above {AppContext.BaseDirectory}");

        string[] names = [];
        var capabilities = new List<(string, string?)>();
        var uses = new List<string>();
        foreach (string rawLine in File.ReadAllLines(Path.Combine(directory!, SourceRelativePath)))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            Assert.EndsWith(",", line);
            if (!char.IsWhiteSpace(rawLine[0]))
            {
                names = line[..^1].Split('|');
                continue;
            }
            foreach (string field in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = field.IndexOf('=');
                if (equals < 0) { capabilities.Add((field, null)); continue; }
                string name = field[..equals];
                string value = Unescape(field[(equals + 1)..]);
                if (name == "use") uses.Add(value);
                else capabilities.Add((name, value));
            }
        }
        return new TerminfoSource(names, capabilities, uses);
    }

    // The entry uses only \E; anything else must be added here deliberately.
    private static string Unescape(string value)
    {
        var text = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { text.Append(value[i]); continue; }
            Assert.True(i + 1 < value.Length && value[i + 1] is 'E' or 'e', $"unsupported escape in '{value}'");
            text.Append('\u001b');
            i++;
        }
        return text.ToString();
    }

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 10);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    public static TheoryData<string> CapabilityNames()
    {
        var data = new TheoryData<string>();
        foreach (TerminfoCapabilities.Capability capability in TerminfoCapabilities.Implemented)
            data.Add(capability.Name);
        return data;
    }

    private static string? ValueOf(string name)
    {
        foreach (TerminfoCapabilities.Capability capability in TerminfoCapabilities.Implemented)
        {
            if (capability.Name == name) return capability.Value;
        }
        throw new ArgumentException(name);
    }

    [Fact]
    public void Source_IsXterm256colorUnderTheDottyNames()
    {
        TerminfoSource source = ReadSource();

        Assert.Equal(new[] { "xterm-dotty", "dotty", "Dotty terminal emulator" }, source.Names);
        Assert.Equal(new[] { "xterm-256color" }, source.Uses);
    }

    [Fact]
    public void Source_DeclaresExactlyTheImplementedCapabilities()
    {
        TerminfoSource source = ReadSource();
        var implemented = TerminfoCapabilities.Implemented.ToArray().Select(c => (c.Name, c.Value)).ToArray();

        Assert.Equal(source.Capabilities.Count, source.Capabilities.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            implemented.OrderBy(c => c.Name, StringComparer.Ordinal),
            source.Capabilities.OrderBy(c => c.Name, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public void Xtgettcap_AnswersStringCapabilitiesWithTheirTerminfoValue(string name)
    {
        var (_, parser, replies) = Setup();
        string nameHex = Convert.ToHexString(Encoding.ASCII.GetBytes(name));

        Feed(parser, "\u001bP+q" + nameHex + "\u001b\\");

        string? value = ValueOf(name);
        if (value is null)
            Assert.Empty(replies); // booleans are not answered
        else
            Assert.Equal(new[] { "\u001bP1+r" + nameHex + "=" + Convert.ToHexString(Encoding.Latin1.GetBytes(value)) + "\u001b\\" }, replies);
    }

    [Theory]
    [MemberData(nameof(CapabilityNames))]
    public void Capability_DoesWhatItsValueClaims(string name)
    {
        var (adapter, parser, _) = Setup();
        string? value = ValueOf(name);
        switch (name)
        {
            case "Tc":
                CellAttributes rgb = SgrParserArgb.Apply("38;2;1;2;3".AsSpan(), CellAttributes.Default, new TerminalPalette());
                Assert.Equal(ColorKind.Rgb, rgb.ForegroundKind);
                Assert.Equal(SgrColorArgb.FromRgb(1, 2, 3), rgb.Foreground);
                break;
            case "Ss":
                Assert.Equal("\u001b[%p1%d q", value);
                foreach (int style in new[] { 1, 2, 3, 4, 5, 6 })
                {
                    Feed(parser, value!.Replace("%p1%d", style.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    Assert.Equal(style, adapter.CursorShape);
                }
                break;
            case "Se":
                Feed(parser, "\u001b[6 q");
                Feed(parser, value!);
                Assert.Equal(2, adapter.CursorShape);
                break;
            case "Sync":
                // %p1 == 1 expands to "h" (begin), anything else to "l" (end).
                Assert.Equal("\u001b[?2026%?%p1%{1}%=%th%el%;", value);
                Feed(parser, "\u001b[?2026h");
                Assert.True(adapter.SynchronizedUpdateActive);
                Feed(parser, "\u001b[?2026l");
                Assert.False(adapter.SynchronizedUpdateActive);
                break;
            case "BE":
                Feed(parser, value!);
                Assert.True(adapter.Buffer.BracketedPasteMode);
                break;
            case "BD":
                Feed(parser, "\u001b[?2004h");
                Feed(parser, value!);
                Assert.False(adapter.Buffer.BracketedPasteMode);
                break;
            case "PS":
                Assert.StartsWith(value!, Encoding.UTF8.GetString(PasteEncoder.Encode("x", bracketed: true)), StringComparison.Ordinal);
                break;
            case "PE":
                Assert.EndsWith(value!, Encoding.UTF8.GetString(PasteEncoder.Encode("x", bracketed: true)), StringComparison.Ordinal);
                break;
            default:
                Assert.Fail($"Capability '{name}' is advertised without a behaviour check here.");
                break;
        }
    }
}
