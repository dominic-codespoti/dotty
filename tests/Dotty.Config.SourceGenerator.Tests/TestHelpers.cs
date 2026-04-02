using Xunit;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Test helpers and utilities for source generator testing
/// </summary>
public static class TestHelpers
{
    #region Test Data Classes

    /// <summary>
    /// Holds all configuration values with defaults for testing.
    /// Mirrors the ConfigValues class in the generator.
    /// </summary>
    public class ConfigValues
    {
        // Font settings
        public string FontFamily { get; set; } = "JetBrains Mono";
        public double FontSize { get; set; } = 15.0;
        public double CellPadding { get; set; } = 1.5;
        public double ContentPaddingLeft { get; set; } = 0.0;
        public double ContentPaddingTop { get; set; } = 0.0;
        public double ContentPaddingRight { get; set; } = 0.0;
        public double ContentPaddingBottom { get; set; } = 0.0;

        // Color settings
        public uint Background { get; set; } = 0xFF1E1E1E;
        public uint Foreground { get; set; } = 0xFFD4D4D4;
        public uint SelectionColor { get; set; } = 0xA03385DB;
        public uint TabBarBackgroundColor { get; set; } = 0xFF1A1A1A;

        // Terminal settings
        public int ScrollbackLines { get; set; } = 10000;
        public int InactiveTabDestroyDelayMs { get; set; } = 5000;

        // Window settings
        public int InitialColumns { get; set; } = 80;
        public int InitialRows { get; set; } = 24;
        public string WindowTitle { get; set; } = "Dotty";
        public bool StartFullscreen { get; set; } = false;

        // Cursor settings
        public string CursorShape { get; set; } = "Block";
        public bool CursorBlink { get; set; } = true;
        public int CursorBlinkIntervalMs { get; set; } = 500;
        public uint? CursorColor { get; set; } = null;
        public bool CursorShowUnfocused { get; set; } = false;

        // Window opacity
        public byte Opacity { get; set; } = 100;
        public byte WindowOpacity { get; set; } = 100;
        public string Transparency { get; set; } = "None";

        // ANSI colors
        public uint[] AnsiColors { get; set; } = new uint[16];
    }

    /// <summary>
    /// Represents thickness/padding values (Left, Top, Right, Bottom).
    /// Mirrors the Thickness class in the generator.
    /// </summary>
    public readonly struct Thickness
    {
        public double Left { get; }
        public double Top { get; }
        public double Right { get; }
        public double Bottom { get; }

        public Thickness(double left, double top, double right, double bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
        public Thickness(double horizontal, double vertical) : this(horizontal, vertical, horizontal, vertical) { }
    }

    #endregion

    #region Theme Helpers

    /// <summary>
    /// Sets color scheme values based on theme name for testing.
    /// </summary>
    public static void SetColorSchemeByName(string themeName, ConfigValues values)
    {
        var normalizedName = themeName.Replace("Theme", "");
        
        switch (normalizedName)
        {
            case "DarkPlus":
                SetDarkPlusColors(values);
                break;
            case "Dracula":
                SetDraculaColors(values);
                break;
            case "OneDark":
                SetOneDarkColors(values);
                break;
            case "GruvboxDark":
                SetGruvboxDarkColors(values);
                break;
            case "CatppuccinMocha":
                SetCatppuccinMochaColors(values);
                break;
            case "TokyoNight":
            case "Tokyo Night":
                SetTokyoNightColors(values);
                break;
            case "LightPlus":
                SetLightPlusColors(values);
                break;
            case "OneLight":
                SetOneLightColors(values);
                break;
            case "GruvboxLight":
                SetGruvboxLightColors(values);
                break;
            case "CatppuccinLatte":
                SetCatppuccinLatteColors(values);
                break;
            case "SolarizedLight":
                SetSolarizedLightColors(values);
                break;
            default:
                SetDarkPlusColors(values);
                break;
        }
    }

    private static void SetDarkPlusColors(ConfigValues values)
    {
        values.Background = 0xFF1E1E1E;
        values.Foreground = 0xFFD4D4D4;
        values.AnsiColors[0] = 0xFF000000;
        values.AnsiColors[1] = 0xFFCD3131;
        values.AnsiColors[2] = 0xFF0DBC79;
        values.AnsiColors[3] = 0xFFE5E510;
        values.AnsiColors[4] = 0xFF2472C8;
        values.AnsiColors[5] = 0xFFBC3FBC;
        values.AnsiColors[6] = 0xFF11A8CD;
        values.AnsiColors[7] = 0xFFE5E5E5;
        values.AnsiColors[8] = 0xFF666666;
        values.AnsiColors[9] = 0xFFF14C4C;
        values.AnsiColors[10] = 0xFF23D18B;
        values.AnsiColors[11] = 0xFFF5F543;
        values.AnsiColors[12] = 0xFF3B8EEA;
        values.AnsiColors[13] = 0xFFD670D6;
        values.AnsiColors[14] = 0xFF29B8DB;
        values.AnsiColors[15] = 0xFFFFFFFF;
    }

    private static void SetDraculaColors(ConfigValues values)
    {
        values.Background = 0xFF282A36;
        values.Foreground = 0xFFF8F8F2;
        values.AnsiColors[0] = 0xFF21222C;
        values.AnsiColors[1] = 0xFFFF5555;
        values.AnsiColors[2] = 0xFF50FA7B;
        values.AnsiColors[3] = 0xFFF1FA8C;
        values.AnsiColors[4] = 0xFFBD93F9;
        values.AnsiColors[5] = 0xFFFF79C6;
        values.AnsiColors[6] = 0xFF8BE9FD;
        values.AnsiColors[7] = 0xFFF8F8F2;
        values.AnsiColors[8] = 0xFF6272A4;
        values.AnsiColors[9] = 0xFFFF6E6E;
        values.AnsiColors[10] = 0xFF69FF94;
        values.AnsiColors[11] = 0xFFFFFFA5;
        values.AnsiColors[12] = 0xFFD6ACFF;
        values.AnsiColors[13] = 0xFFFF92DF;
        values.AnsiColors[14] = 0xFFA4FFFF;
        values.AnsiColors[15] = 0xFFFFFFFF;
    }

    private static void SetOneDarkColors(ConfigValues values)
    {
        values.Background = 0xFF282C34;
        values.Foreground = 0xFFABB2BF;
        values.AnsiColors[0] = 0xFF282C34;
        values.AnsiColors[1] = 0xFFE06C75;
        values.AnsiColors[2] = 0xFF98C379;
        values.AnsiColors[3] = 0xFFE5C07B;
        values.AnsiColors[4] = 0xFF61AFEF;
        values.AnsiColors[5] = 0xFFC678DD;
        values.AnsiColors[6] = 0xFF56B6C2;
        values.AnsiColors[7] = 0xFFABB2BF;
        values.AnsiColors[8] = 0xFF5C6370;
        values.AnsiColors[9] = 0xFFE06C75;
        values.AnsiColors[10] = 0xFF98C379;
        values.AnsiColors[11] = 0xFFE5C07B;
        values.AnsiColors[12] = 0xFF61AFEF;
        values.AnsiColors[13] = 0xFFC678DD;
        values.AnsiColors[14] = 0xFF56B6C2;
        values.AnsiColors[15] = 0xFFFFFFFF;
    }

    private static void SetGruvboxDarkColors(ConfigValues values)
    {
        values.Background = 0xFF282828;
        values.Foreground = 0xFFEBDBB2;
        values.AnsiColors[0] = 0xFF282828;
        values.AnsiColors[1] = 0xFFCC241D;
        values.AnsiColors[2] = 0xFF98971A;
        values.AnsiColors[3] = 0xFFD79921;
        values.AnsiColors[4] = 0xFF458588;
        values.AnsiColors[5] = 0xFFB16286;
        values.AnsiColors[6] = 0xFF689D6A;
        values.AnsiColors[7] = 0xFFA89984;
        values.AnsiColors[8] = 0xFF928374;
        values.AnsiColors[9] = 0xFFFB4934;
        values.AnsiColors[10] = 0xFFB8BB26;
        values.AnsiColors[11] = 0xFFFABD2F;
        values.AnsiColors[12] = 0xFF83A598;
        values.AnsiColors[13] = 0xFFD3869B;
        values.AnsiColors[14] = 0xFF8EC07C;
        values.AnsiColors[15] = 0xFFEBDBB2;
    }

    private static void SetCatppuccinMochaColors(ConfigValues values)
    {
        values.Background = 0xFF1E1E2E;
        values.Foreground = 0xFFCDD6F4;
        values.AnsiColors[0] = 0xFF45475A;
        values.AnsiColors[1] = 0xFFF38BA8;
        values.AnsiColors[2] = 0xFFA6E3A1;
        values.AnsiColors[3] = 0xFFF9E2AF;
        values.AnsiColors[4] = 0xFF89B4FA;
        values.AnsiColors[5] = 0xFFF5C2E7;
        values.AnsiColors[6] = 0xFF94E2D5;
        values.AnsiColors[7] = 0xFFBAC2DE;
        values.AnsiColors[8] = 0xFF585B70;
        values.AnsiColors[9] = 0xFFF38BA8;
        values.AnsiColors[10] = 0xFFA6E3A1;
        values.AnsiColors[11] = 0xFFF9E2AF;
        values.AnsiColors[12] = 0xFF89B4FA;
        values.AnsiColors[13] = 0xFFF5C2E7;
        values.AnsiColors[14] = 0xFF94E2D5;
        values.AnsiColors[15] = 0xFFA6ADC8;
    }

    private static void SetTokyoNightColors(ConfigValues values)
    {
        values.Background = 0xFF1A1B26;
        values.Foreground = 0xFFA9B1D6;
        values.AnsiColors[0] = 0xFF15161E;
        values.AnsiColors[1] = 0xFFF7768E;
        values.AnsiColors[2] = 0xFF9ECE6A;
        values.AnsiColors[3] = 0xFFE0AF68;
        values.AnsiColors[4] = 0xFF7AA2F7;
        values.AnsiColors[5] = 0xFFBB9AF7;
        values.AnsiColors[6] = 0xFF7DCFFF;
        values.AnsiColors[7] = 0xFF787C99;
        values.AnsiColors[8] = 0xFF414868;
        values.AnsiColors[9] = 0xFFF7768E;
        values.AnsiColors[10] = 0xFF9ECE6A;
        values.AnsiColors[11] = 0xFFE0AF68;
        values.AnsiColors[12] = 0xFF7AA2F7;
        values.AnsiColors[13] = 0xFFBB9AF7;
        values.AnsiColors[14] = 0xFF7DCFFF;
        values.AnsiColors[15] = 0xFFC0CAF5;
    }

    private static void SetLightPlusColors(ConfigValues values)
    {
        values.Background = 0xFFFFFFFF;
        values.Foreground = 0xFF000000;
        values.AnsiColors[0] = 0xFF000000;
        values.AnsiColors[1] = 0xFFCD3131;
        values.AnsiColors[2] = 0xFF00BC00;
        values.AnsiColors[3] = 0xFFE5E510;
        values.AnsiColors[4] = 0xFF0000EE;
        values.AnsiColors[5] = 0xFFCD00CD;
        values.AnsiColors[6] = 0xFF00CDCD;
        values.AnsiColors[7] = 0xFFE5E5E5;
        values.AnsiColors[8] = 0xFF666666;
        values.AnsiColors[9] = 0xFFFF0000;
        values.AnsiColors[10] = 0xFF00FF00;
        values.AnsiColors[11] = 0xFFFFFF00;
        values.AnsiColors[12] = 0xFF5C5CFF;
        values.AnsiColors[13] = 0xFFFF00FF;
        values.AnsiColors[14] = 0xFF00FFFF;
        values.AnsiColors[15] = 0xFFFFFFFF;
    }

    private static void SetOneLightColors(ConfigValues values)
    {
        values.Background = 0xFFFAFAFA;
        values.Foreground = 0xFF383A42;
        values.AnsiColors[0] = 0xFF383A42;
        values.AnsiColors[1] = 0xFFE45649;
        values.AnsiColors[2] = 0xFF50A14F;
        values.AnsiColors[3] = 0xFF986801;
        values.AnsiColors[4] = 0xFF4078F2;
        values.AnsiColors[5] = 0xFFA626A4;
        values.AnsiColors[6] = 0xFF0184BC;
        values.AnsiColors[7] = 0xFFA0A1A7;
        values.AnsiColors[8] = 0xFF4F525D;
        values.AnsiColors[9] = 0xFFE45649;
        values.AnsiColors[10] = 0xFF50A14F;
        values.AnsiColors[11] = 0xFF986801;
        values.AnsiColors[12] = 0xFF4078F2;
        values.AnsiColors[13] = 0xFFA626A4;
        values.AnsiColors[14] = 0xFF0184BC;
        values.AnsiColors[15] = 0xFFFFFFFF;
    }

    private static void SetGruvboxLightColors(ConfigValues values)
    {
        values.Background = 0xFFFBF1C7;
        values.Foreground = 0xFF3C3836;
        values.AnsiColors[0] = 0xFFFBF1C7;
        values.AnsiColors[1] = 0xFFCC241D;
        values.AnsiColors[2] = 0xFF98971A;
        values.AnsiColors[3] = 0xFFD79921;
        values.AnsiColors[4] = 0xFF458588;
        values.AnsiColors[5] = 0xFFB16286;
        values.AnsiColors[6] = 0xFF689D6A;
        values.AnsiColors[7] = 0xFF7C6F64;
        values.AnsiColors[8] = 0xFF928374;
        values.AnsiColors[9] = 0xFF9D0006;
        values.AnsiColors[10] = 0xFF79740E;
        values.AnsiColors[11] = 0xFFB57614;
        values.AnsiColors[12] = 0xFF076678;
        values.AnsiColors[13] = 0xFF8F3F71;
        values.AnsiColors[14] = 0xFF427B58;
        values.AnsiColors[15] = 0xFF3C3836;
    }

    private static void SetCatppuccinLatteColors(ConfigValues values)
    {
        values.Background = 0xFFEff1F5;
        values.Foreground = 0xFF4C4F69;
        values.AnsiColors[0] = 0xFF5C5F77;
        values.AnsiColors[1] = 0xFFD20F39;
        values.AnsiColors[2] = 0xFF40A02B;
        values.AnsiColors[3] = 0xFFDF8E1D;
        values.AnsiColors[4] = 0xFF1E66F5;
        values.AnsiColors[5] = 0xFF8839EF;
        values.AnsiColors[6] = 0xFF179299;
        values.AnsiColors[7] = 0xFFACB0BE;
        values.AnsiColors[8] = 0xFF6C6F85;
        values.AnsiColors[9] = 0xFFD20F39;
        values.AnsiColors[10] = 0xFF56C150;
        values.AnsiColors[11] = 0xFFDF8E1D;
        values.AnsiColors[12] = 0xFF1E66F5;
        values.AnsiColors[13] = 0xFF8839EF;
        values.AnsiColors[14] = 0xFF179299;
        values.AnsiColors[15] = 0xFFBCC0CC;
    }

    private static void SetSolarizedLightColors(ConfigValues values)
    {
        values.Background = 0xFFFDF6E3;
        values.Foreground = 0xFF657B83;
        values.AnsiColors[0] = 0xFF073642;
        values.AnsiColors[1] = 0xFFDC322F;
        values.AnsiColors[2] = 0xFF859900;
        values.AnsiColors[3] = 0xFFB58900;
        values.AnsiColors[4] = 0xFF268BD2;
        values.AnsiColors[5] = 0xFFD33682;
        values.AnsiColors[6] = 0xFF2AA198;
        values.AnsiColors[7] = 0xFFEEE8D5;
        values.AnsiColors[8] = 0xFF002B36;
        values.AnsiColors[9] = 0xFFCB4B16;
        values.AnsiColors[10] = 0xFF586E75;
        values.AnsiColors[11] = 0xFF657B83;
        values.AnsiColors[12] = 0xFF839496;
        values.AnsiColors[13] = 0xFF6C71C4;
        values.AnsiColors[14] = 0xFF93A1A1;
        values.AnsiColors[15] = 0xFFFDF6E3;
    }

    #endregion

    /// <summary>
    /// Sample source code strings for testing
    /// </summary>
    public static class TestSourceCode
    {
        public const string SimpleConfig = @"
namespace Dotty.Config;

public static class DottyConfig
{
    public static string Theme => ""Dark+"";
    public static double Opacity => 1.0;
}
";

        public const string ComplexConfig = @"
namespace Dotty.Config;

public static class DottyConfig
{
    public static string Theme => ""Dracula"";
    public static double Opacity => 0.95;
    public static string FontFamily => ""JetBrains Mono"";
    public static int FontSize => 14;
}
";

        public const string EmptyThemeConfig = @"
namespace Dotty.Config;

public static class DottyConfig
{
    public static string Theme => """";
    public static double Opacity => 1.0;
}
";

        public const string CustomColorsConfig = @"
namespace Dotty.Config;

public static class DottyConfig
{
    public static string Theme => ""Custom"";
    public static string[] CustomColors => new[] { ""#ff0000"", ""#00ff00"", ""#0000ff"" };
}
";

        public const string TestConfigWithDarkPlus = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    public string? FontFamily => ""Test Font"";
    public double? FontSize => 14.0;
    public double? CellPadding => 2.0;
}
";

        public const string TestConfigWithDracula = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.Dracula;
    public string? FontFamily => ""JetBrains Mono"";
    public double? FontSize => 16.0;
}
";

        public const string TestConfigWithInvalidTheme = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => null;
    public string? FontFamily => ""Test"";
}
";
    }

    /// <summary>
    /// Assertion helpers for generated code validation
    /// </summary>
    public static class GeneratedCodeAssertions
    {
        public static void ContainsValidThemeClass(string generatedCode, string themeName)
        {
            generatedCode.Should().Contain($"class {themeName.Replace(" ", "")}Theme");
            generatedCode.Should().Contain("public static string Name");
            generatedCode.Should().Contain("public static string Background");
            generatedCode.Should().Contain("public static string[] AnsiColors");
        }

        public static void ContainsThemeRegistration(string generatedCode, string themeName)
        {
            generatedCode.Should().Contain($"\"{themeName}\"");
            generatedCode.Should().Contain("ThemeRegistry");
        }

        public static void ContainsNoDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            var errorDiagnostics = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            errorDiagnostics.Should().BeEmpty($"Expected no errors, but found: {string.Join(", ", errorDiagnostics.Select(d => d.GetMessage()))}");
        }

        public static void SyntaxTreeIsValid(SyntaxTree syntaxTree)
        {
            syntaxTree.Should().NotBeNull();
            syntaxTree.GetRoot().DescendantNodes().Should().NotBeEmpty();
            
            var diagnostics = syntaxTree.GetDiagnostics();
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            errors.Should().BeEmpty();
        }

        /// <summary>
        /// Asserts that generated code contains the specified text.
        /// </summary>
        public static void AssertGeneratedCodeContains(string generatedCode, string expectedText)
        {
            generatedCode.Should().Contain(expectedText);
        }
    }

    /// <summary>
    /// Common test data including theme information
    /// </summary>
    public static class TestData
    {
        public static readonly string[] AllThemeNames = new[]
        {
            "Dark+",
            "Light+",
            "Monokai",
            "Dracula",
            "Nord",
            "OneDark",
            "Solarized Dark",
            "Solarized Light",
            "Tokyo Night",
            "GitHub Dark",
            "GitHub Light"
        };

        public static readonly Dictionary<string, string> ThemeBackgrounds = new()
        {
            ["Dark+"] = "#1e1e1e",
            ["Light+"] = "#ffffff",
            ["Monokai"] = "#272822",
            ["Dracula"] = "#282a36",
            ["Nord"] = "#2e3440",
            ["OneDark"] = "#282c34",
            ["Solarized Dark"] = "#002b36",
            ["Solarized Light"] = "#fdf6e3",
            ["Tokyo Night"] = "#1a1b26",
            ["GitHub Dark"] = "#0d1117",
            ["GitHub Light"] = "#ffffff"
        };

        public static readonly Dictionary<string, uint> ThemeBackgroundsArgb = new()
        {
            ["Dark+"] = 0xFF1E1E1E,
            ["Light+"] = 0xFFFFFFFF,
            ["Monokai"] = 0xFF272822,
            ["Dracula"] = 0xFF282A36,
            ["Nord"] = 0xFF2E3440,
            ["OneDark"] = 0xFF282C34,
            ["Solarized Dark"] = 0xFF002B36,
            ["Solarized Light"] = 0xFFFDF6E3,
            ["Tokyo Night"] = 0xFF1A1B26,
            ["GitHub Dark"] = 0xFF0D1117,
            ["GitHub Light"] = 0xFFFFFFFF
        };

        public static readonly Dictionary<string, double> ThemeOpacities = new()
        {
            ["Dark+"] = 1.0,
            ["Light+"] = 1.0,
            ["Monokai"] = 1.0,
            ["Dracula"] = 1.0,
            ["Nord"] = 0.95,
            ["OneDark"] = 1.0,
            ["Solarized Dark"] = 1.0,
            ["Solarized Light"] = 1.0,
            ["Tokyo Night"] = 0.95,
            ["GitHub Dark"] = 1.0,
            ["GitHub Light"] = 1.0
        };

        public static readonly string[] StandardAnsiColors = new[]
        {
            "#000000",  // Black
            "#800000",  // Red
            "#008000",  // Green
            "#808000",  // Yellow
            "#000080",  // Blue
            "#800080",  // Magenta
            "#008080",  // Cyan
            "#c0c0c0",  // White
            "#808080",  // Bright Black
            "#ff0000",  // Bright Red
            "#00ff00",  // Bright Green
            "#ffff00",  // Bright Yellow
            "#0000ff",  // Bright Blue
            "#ff00ff",  // Bright Magenta
            "#00ffff",  // Bright Cyan
            "#ffffff"   // Bright White
        };
    }

    /// <summary>
    /// Creates a compilation from source code for testing
    /// </summary>
    public static CSharpCompilation CreateCompilation(string sourceCode, IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location)
        };

        if (additionalReferences != null)
        {
            references.AddRange(additionalReferences);
        }

        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(sourceCode) },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
    }

    /// <summary>
    /// Creates a test compilation for source generator testing with Dotty.Abstractions referenced.
    /// </summary>
    public static CSharpCompilation CreateTestCompilation(string sourceCode)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dotty.Abstractions.Config.IDottyConfig).Assembly.Location)
        };

        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(sourceCode) },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
    }
}
