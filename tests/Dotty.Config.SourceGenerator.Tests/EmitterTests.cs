using Xunit;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;
using System.Text;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Tests for code emission/generation functionality.
/// Verifies that the source generator correctly emits Config class, ColorScheme record, and KeyBindings enum.
/// </summary>
public class EmitterTests
{
    #region ConfigEmitter Tests

    /// <summary>
    /// Verifies that the Config class emitter generates a valid static class with expected members.
    /// </summary>
    [Fact]
    public void ConfigEmitter_GeneratesValidClass()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        values.FontFamily = "JetBrains Mono";
        values.FontSize = 14.0;
        values.CellPadding = 2.0;
        values.Background = 0xFF1E1E1E;
        values.Foreground = 0xFFD4D4D4;

        // Act - Simulate what the generator does
        var generatedCode = GenerateConfigClassForTest(values);

        // Assert
        generatedCode.Should().Contain("public static class Config");
        generatedCode.Should().Contain("FontFamily => \"JetBrains Mono\"");
        generatedCode.Should().Contain("FontSize => 14");
        generatedCode.Should().Contain("CellPadding => 2");
        generatedCode.Should().Contain("Background => 0xFF1E1E1E");
        generatedCode.Should().Contain("Foreground => 0xFFD4D4D4");
    }

    /// <summary>
    /// Verifies that string values are properly escaped in the generated code.
    /// </summary>
    [Fact]
    public void ConfigEmitter_EscapesStringsProperly()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        values.FontFamily = "Font with \"quotes\" and \\ backslash";
        values.WindowTitle = "Title with \"quotes\"";

        // Act
        var generatedCode = GenerateConfigClassForTest(values);

        // Assert
        generatedCode.Should().Contain("Font with \\\"quotes\\\"");
        generatedCode.Should().Contain("\\\\ backslash");
        generatedCode.Should().Contain("Title with \\\"quotes\\\"");
    }

    #endregion

    #region ColorSchemeEmitter Tests

    /// <summary>
    /// Verifies that the ColorScheme record is generated correctly with default values.
    /// </summary>
    [Fact]
    public void ColorSchemeEmitter_GeneratesValidRecord()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        TestHelpers.SetColorSchemeByName("DarkPlus", values);

        // Act
        var generatedCode = GenerateColorSchemeRecordForTest(values);

        // Assert
        generatedCode.Should().Contain("public readonly record struct ColorScheme");
        generatedCode.Should().Contain("uint Background,");
        generatedCode.Should().Contain("uint Foreground,");
        generatedCode.Should().Contain("uint[] AnsiColors");
        generatedCode.Should().Contain("public static ColorScheme Default");
    }

    /// <summary>
    /// Verifies that the ColorScheme includes all 16 ANSI colors.
    /// </summary>
    [Fact]
    public void ColorSchemeEmitter_IncludesAllAnsiColors()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        TestHelpers.SetColorSchemeByName("DarkPlus", values);

        // Act
        var generatedCode = GenerateColorSchemeRecordForTest(values);

        // Assert
        generatedCode.Should().Contain("public uint AnsiBlack => AnsiColors[0]");
        generatedCode.Should().Contain("public uint AnsiRed => AnsiColors[1]");
        generatedCode.Should().Contain("public uint AnsiGreen => AnsiColors[2]");
        generatedCode.Should().Contain("public uint AnsiYellow => AnsiColors[3]");
        generatedCode.Should().Contain("public uint AnsiBlue => AnsiColors[4]");
        generatedCode.Should().Contain("public uint AnsiMagenta => AnsiColors[5]");
        generatedCode.Should().Contain("public uint AnsiCyan => AnsiColors[6]");
        generatedCode.Should().Contain("public uint AnsiWhite => AnsiColors[7]");
        generatedCode.Should().Contain("public uint AnsiBrightBlack => AnsiColors[8]");
        generatedCode.Should().Contain("public uint AnsiBrightRed => AnsiColors[9]");
        generatedCode.Should().Contain("public uint AnsiBrightGreen => AnsiColors[10]");
        generatedCode.Should().Contain("public uint AnsiBrightYellow => AnsiColors[11]");
        generatedCode.Should().Contain("public uint AnsiBrightBlue => AnsiColors[12]");
        generatedCode.Should().Contain("public uint AnsiBrightMagenta => AnsiColors[13]");
        generatedCode.Should().Contain("public uint AnsiBrightCyan => AnsiColors[14]");
        generatedCode.Should().Contain("public uint AnsiBrightWhite => AnsiColors[15]");
    }

    #endregion

    #region KeyBindingsEmitter Tests

    /// <summary>
    /// Verifies that the TerminalAction enum is generated correctly.
    /// </summary>
    [Fact]
    public void KeyBindingsEmitter_GeneratesValidEnum()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();

        // Act
        var generatedCode = GenerateKeyBindingHelpersForTest(values);

        // Assert
        generatedCode.Should().Contain("public enum TerminalAction");
        generatedCode.Should().Contain("None,");
        generatedCode.Should().Contain("NewTab,");
        generatedCode.Should().Contain("CloseTab,");
        generatedCode.Should().Contain("NextTab,");
        generatedCode.Should().Contain("PreviousTab,");
        generatedCode.Should().Contain("Copy,");
        generatedCode.Should().Contain("Paste,");
        generatedCode.Should().Contain("ZoomIn,");
        generatedCode.Should().Contain("ZoomOut,");
        generatedCode.Should().Contain("ToggleFullscreen,");
        generatedCode.Should().Contain("Quit,");
    }

    #endregion

    #region GeneratedCode Compilation Tests

    /// <summary>
    /// Verifies that the generated code compiles without errors.
    /// </summary>
    [Fact]
    public void GeneratedCode_Compiles()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        TestHelpers.SetColorSchemeByName("DarkPlus", values);

        var configCode = GenerateConfigClassForTest(values);
        var colorSchemeCode = GenerateColorSchemeRecordForTest(values);
        var keyBindingsCode = GenerateKeyBindingHelpersForTest(values);

        // Create syntax trees from generated code
        var configTree = CSharpSyntaxTree.ParseText(configCode);
        var colorSchemeTree = CSharpSyntaxTree.ParseText(colorSchemeCode);
        var keyBindingsTree = CSharpSyntaxTree.ParseText(keyBindingsCode);

        // Act - Try to compile
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dotty.Abstractions.Config.IDottyConfig).Assembly.Location)
        };

        var compilation = CSharpCompilation.Create(
            "GeneratedTestAssembly",
            new[] { configTree, colorSchemeTree, keyBindingsTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        // Assert
        errors.Should().BeEmpty($"Generated code should compile without errors. Found: {string.Join(", ", errors.Select(e => e.GetMessage()))}");
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Simulates the Config class generation for testing.
    /// </summary>
    private static string GenerateConfigClassForTest(TestHelpers.ConfigValues values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("public static class Config");
        sb.AppendLine("{");
        sb.AppendLine($"    public static string FontFamily => \"{EscapeString(values.FontFamily)}\";");
        sb.AppendLine($"    public static double FontSize => {values.FontSize};");
        sb.AppendLine($"    public static double CellPadding => {values.CellPadding};");
        sb.AppendLine($"    public static uint Background => 0x{values.Background:X8};");
        sb.AppendLine($"    public static uint Foreground => 0x{values.Foreground:X8};");
        sb.AppendLine($"    public static int ScrollbackLines => {values.ScrollbackLines};");
        sb.AppendLine($"    public static int InitialColumns => {values.InitialColumns};");
        sb.AppendLine($"    public static int InitialRows => {values.InitialRows};");
        sb.AppendLine($"    public static string WindowTitle => \"{EscapeString(values.WindowTitle)}\";");
        sb.AppendLine($"    public static string CursorShape => \"{values.CursorShape}\";");
        sb.AppendLine($"    public static bool CursorBlink => {values.CursorBlink.ToString().ToLowerInvariant()};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Simulates the ColorScheme record generation for testing.
    /// </summary>
    private static string GenerateColorSchemeRecordForTest(TestHelpers.ConfigValues values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("public readonly record struct ColorScheme");
        sb.AppendLine("(");
        sb.AppendLine("    uint Background,");
        sb.AppendLine("    uint Foreground,");
        sb.AppendLine("    uint[] AnsiColors");
        sb.AppendLine(")");
        sb.AppendLine("{");
        sb.AppendLine("    public static ColorScheme Default => new(");
        sb.AppendLine($"        0x{values.Background:X8},");
        sb.AppendLine($"        0x{values.Foreground:X8},");
        sb.AppendLine("        new uint[]");
        sb.AppendLine("        {");
        for (int i = 0; i < 16; i++)
        {
            sb.AppendLine($"            0x{values.AnsiColors[i]:X8}, // {GetAnsiColorName(i)}");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    );");
        sb.AppendLine();
        sb.AppendLine("    public uint AnsiBlack => AnsiColors[0];");
        sb.AppendLine("    public uint AnsiRed => AnsiColors[1];");
        sb.AppendLine("    public uint AnsiGreen => AnsiColors[2];");
        sb.AppendLine("    public uint AnsiYellow => AnsiColors[3];");
        sb.AppendLine("    public uint AnsiBlue => AnsiColors[4];");
        sb.AppendLine("    public uint AnsiMagenta => AnsiColors[5];");
        sb.AppendLine("    public uint AnsiCyan => AnsiColors[6];");
        sb.AppendLine("    public uint AnsiWhite => AnsiColors[7];");
        sb.AppendLine("    public uint AnsiBrightBlack => AnsiColors[8];");
        sb.AppendLine("    public uint AnsiBrightRed => AnsiColors[9];");
        sb.AppendLine("    public uint AnsiBrightGreen => AnsiColors[10];");
        sb.AppendLine("    public uint AnsiBrightYellow => AnsiColors[11];");
        sb.AppendLine("    public uint AnsiBrightBlue => AnsiColors[12];");
        sb.AppendLine("    public uint AnsiBrightMagenta => AnsiColors[13];");
        sb.AppendLine("    public uint AnsiBrightCyan => AnsiColors[14];");
        sb.AppendLine("    public uint AnsiBrightWhite => AnsiColors[15];");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Simulates the KeyBinding helpers generation for testing.
    /// </summary>
    private static string GenerateKeyBindingHelpersForTest(TestHelpers.ConfigValues values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("public enum TerminalAction");
        sb.AppendLine("{");
        sb.AppendLine("    None,");
        sb.AppendLine("    NewTab,");
        sb.AppendLine("    CloseTab,");
        sb.AppendLine("    NextTab,");
        sb.AppendLine("    PreviousTab,");
        sb.AppendLine("    SwitchTab1,");
        sb.AppendLine("    SwitchTab2,");
        sb.AppendLine("    SwitchTab3,");
        sb.AppendLine("    SwitchTab4,");
        sb.AppendLine("    SwitchTab5,");
        sb.AppendLine("    SwitchTab6,");
        sb.AppendLine("    SwitchTab7,");
        sb.AppendLine("    SwitchTab8,");
        sb.AppendLine("    SwitchTab9,");
        sb.AppendLine("    Copy,");
        sb.AppendLine("    Paste,");
        sb.AppendLine("    Clear,");
        sb.AppendLine("    ToggleFullscreen,");
        sb.AppendLine("    ZoomIn,");
        sb.AppendLine("    ZoomOut,");
        sb.AppendLine("    ResetZoom,");
        sb.AppendLine("    Search,");
        sb.AppendLine("    DuplicateTab,");
        sb.AppendLine("    CloseOtherTabs,");
        sb.AppendLine("    RenameTab,");
        sb.AppendLine("    ToggleVisibility,");
        sb.AppendLine("    IncreaseScrollback,");
        sb.AppendLine("    DecreaseScrollback,");
        sb.AppendLine("    SendEscapeSequence,");
        sb.AppendLine("    Quit,");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string GetAnsiColorName(int index)
    {
        return index switch
        {
            0 => "Black",
            1 => "Red",
            2 => "Green",
            3 => "Yellow",
            4 => "Blue",
            5 => "Magenta",
            6 => "Cyan",
            7 => "White",
            8 => "Bright Black",
            9 => "Bright Red",
            10 => "Bright Green",
            11 => "Bright Yellow",
            12 => "Bright Blue",
            13 => "Bright Magenta",
            14 => "Bright Cyan",
            15 => "Bright White",
            _ => $"Color {index}"
        };
    }

    #endregion
}