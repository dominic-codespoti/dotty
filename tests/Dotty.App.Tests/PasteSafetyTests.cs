using System;
using System.Linq;
using Dotty.Runtime.Config;
using Dotty.Runtime.Input;
using Xunit;

namespace Dotty.App.Tests;

public sealed class PasteSafetyTests
{
    public static TheoryData<string, bool, bool, PasteRisk, bool> Matrix() => new()
    {
        { "ls", false, true, PasteRisk.None, false },
        { "ls", true, false, PasteRisk.None, false },
        { "ls\n", false, true, PasteRisk.Newlines, true },
        { "ls\n", true, true, PasteRisk.Newlines, false },
        { "ls\n", true, false, PasteRisk.Newlines, true },
        { "a\rb", false, true, PasteRisk.Newlines, true },
        { "a\r\nb", false, true, PasteRisk.Newlines, true },
        { "a\tb", false, true, PasteRisk.None, false },
        { "x\x1b[201~y", true, true, PasteRisk.ControlChars | PasteRisk.EndMarker, true },
        { "x\u009b201~y", true, true, PasteRisk.ControlChars | PasteRisk.EndMarker, true },
        { "\x1b[200~", true, true, PasteRisk.ControlChars, true },
        { "\0", true, true, PasteRisk.ControlChars, true },
        { "\x03", true, true, PasteRisk.ControlChars, true },
        { "\x7f", true, true, PasteRisk.ControlChars, true },
        { "\u0085", true, true, PasteRisk.ControlChars, true },
        { "héllo 你好 😀\u00a0\u2028", false, true, PasteRisk.None, false },
        { "", false, true, PasteRisk.None, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void RiskAndConfirmation(string text, bool bracketed, bool safe, PasteRisk risk, bool confirm)
    {
        Assert.Equal(risk, PasteSafety.Evaluate(text));
        Assert.Equal(confirm, PasteSafety.NeedsConfirmation(risk, bracketed, safe));
        var config = new ClipboardUserConfig { ConfirmPaste = true, BracketedPasteIsSafe = safe };
        Assert.Equal(confirm, PasteSafety.NeedsConfirmation(text, bracketed, config));
    }

    [Fact]
    public void ConfirmPasteOff_NeverNeedsConfirmation()
    {
        var config = new ClipboardUserConfig { ConfirmPaste = false };
        Assert.False(PasteSafety.NeedsConfirmation("a\nb\x1b", false, config));
        Assert.False(PasteSafety.NeedsConfirmation(null, false, config));
    }

    [Fact]
    public void Reasons_ShrinkWhenBracketedAndSafe()
    {
        Assert.Equal(PasteRisk.Newlines, PasteSafety.Reasons(PasteRisk.Newlines, false, true));
        Assert.Equal(PasteRisk.None, PasteSafety.Reasons(PasteRisk.Newlines, true, true));
        Assert.Equal(PasteRisk.Newlines, PasteSafety.Reasons(PasteRisk.Newlines, true, false));
    }

    [Fact]
    public void Helpers_ClassifyText()
    {
        Assert.True(PasteSafety.IsMultiline("a\nb"));
        Assert.False(PasteSafety.IsMultiline("ab"));
        Assert.True(PasteSafety.HasControl("a\x1b"));
        Assert.False(PasteSafety.HasControl("a\tb\n"));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\r\nb", 2)]
    [InlineData("\n", 1)]
    [InlineData("a\n\nb", 3)]
    public void CountLines(string text, int expected)
    {
        Assert.Equal(expected, PasteSafety.CountLines(text));
    }

    [Fact]
    public void BuildPreview_ShowsControlsAsGlyphsAndBoundsOutput()
    {
        string[] preview = PasteSafety.BuildPreview("a\x1b[0m\x7f\u0085\r\nb", 6, 60);
        Assert.Equal(new[] { "a\u241b[0m\u2421·", "b" }, preview);
    }

    [Fact]
    public void BuildPreview_TruncatesLinesAndCount()
    {
        string text = string.Join("\n", Enumerable.Range(0, 20).Select(i => new string('x', 100)));
        string[] preview = PasteSafety.BuildPreview(text, 6, 60);
        Assert.Equal(7, preview.Length);
        Assert.All(preview, l => Assert.True(l.Length <= 60));
        Assert.EndsWith("…", preview[0]);
        Assert.Equal("… 14 more lines", preview[6]);
    }

    [Fact]
    public void BuildPreview_HugeSingleLineStaysBounded()
    {
        string[] preview = PasteSafety.BuildPreview(new string('y', 100_000), 6, 60);
        Assert.Single(preview);
        Assert.Equal(60, preview[0].Length);
    }

    [Fact]
    public void BuildPreview_DoesNotSplitSurrogatePair()
    {
        string[] preview = PasteSafety.BuildPreview(new string('a', 58) + "😀😀", 6, 60);
        Assert.Equal(new string('a', 58) + "…", preview[0]);
    }
}
