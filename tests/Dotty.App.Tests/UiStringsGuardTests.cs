using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dotty.Runtime.Localization;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Menu and prompt builders take their user-visible text from <see cref="UiStrings"/>, never from literals.</summary>
public class UiStringsGuardTests
{
    private static readonly string[] s_guardedFiles =
    [
        "src/Dotty.Runtime/ContextMenu/DefaultContextMenus.cs",
        "src/Dotty.Runtime/Prompt/HostPrompt.cs",
        "src/Dotty/Host/PastePromptBuilder.cs",
        "src/Dotty/Host/ClipboardReadPromptBuilder.cs",
        "src/Dotty/Host/HyperlinkPromptBuilder.cs",
        "src/Dotty/Host/DottyWindowHost.Recovery.cs",
    ];

    // A string literal that reads like prose: a capital letter followed by a lowercase letter, or a parenthesised phrase.
    private static readonly Regex s_prose = new("\"(?:[A-Z][a-z][^\"]*|\\([a-z][^\"]*)\"", RegexOptions.Compiled);

    [Fact]
    public void GuardedFiles_HaveNoProseLiterals()
    {
        string root = RepoRoot();
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string file in s_guardedFiles)
        {
            string[] lines = File.ReadAllLines(Path.Combine(root, file));
            bool inDiagnostic = false; // inside a multi-line stderr/exception/crash-log statement (not UI text)
            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i];
                string trimmed = text.TrimStart();
                if (text.Contains("Console.Error", StringComparison.Ordinal) || text.Contains("Exception(", StringComparison.Ordinal)
                    || text.Contains("CrashReporter.", StringComparison.Ordinal))
                    inDiagnostic = true;
                bool skip = inDiagnostic || trimmed.StartsWith("//", StringComparison.Ordinal);
                if (inDiagnostic && trimmed.EndsWith(';'))
                    inDiagnostic = false;
                if (!skip && s_prose.IsMatch(text))
                    offenders.Add($"{file}:{i + 1}: {trimmed}");
            }
        }
        Assert.True(offenders.Count == 0, "Move these literals to UiStrings:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Constants_AreNonEmptyTrimmedAndDistinct()
    {
        var values = typeof(UiStrings).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.True(v.Length > 0 && v == v.Trim()));
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(1, 5, "Paste 1 line, 5 characters?")]
    [InlineData(1200, 34567, "Paste 1,200 lines, 34,567 characters?")]
    public void PasteTitle_IsCultureInvariant(int lines, int chars, string expected) =>
        Assert.Equal(expected, UiStrings.PasteTitle(lines, chars));

    private static string RepoRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Dotty.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        return root;
    }
}
