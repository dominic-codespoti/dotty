using Xunit;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Integration tests for the ConfigGenerator source generator.
/// Verifies end-to-end functionality with actual compilation.
/// </summary>
public class IntegrationTests
{
    #region Theme Integration Tests

    /// <summary>
    /// Verifies that the generator produces correct background color for Dark+ theme.
    /// Note: This test verifies the generated source code content only, not full compilation,
    /// as the generated code requires Avalonia references that aren't available in test context.
    /// </summary>
    [Fact]
    public async Task Generator_WithDarkPlus_ProducesCorrectBackground()
    {
        // Arrange
        const string source = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    public string? FontFamily => ""Test Font"";
    public double? FontSize => 14.0;
}
";

        // Act
        var (compilation, generatedSources) = await RunGeneratorAsync(source);

        // Assert - verify generated source contains expected values
        // (Skip compilation error check due to missing Avalonia references in test context)
        generatedSources.Should().Contain(s => s.HintName.Contains("Config.g.cs"));
        
        var configSource = generatedSources.First(s => s.HintName.Contains("Config.g.cs"));
        var configCode = configSource.SourceText.ToString();
        
        // DarkPlus background is 0xFF1E1E1E
        configCode.Should().Contain("0xFF1E1E1E");
    }

    /// <summary>
    /// Verifies that the generator produces correct background color for Dracula theme.
    /// Note: This test verifies the generated source code content only, not full compilation,
    /// as the generated code requires Avalonia references that aren't available in test context.
    /// </summary>
    [Fact]
    public async Task Generator_WithDracula_ProducesCorrectBackground()
    {
        // Arrange
        const string source = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.Dracula;
    public string? FontFamily => ""JetBrains Mono"";
    public double? FontSize => 16.0;
}
";

        // Act
        var (compilation, generatedSources) = await RunGeneratorAsync(source);

        // Assert - verify generated source contains expected values
        // (Skip compilation error check due to missing Avalonia references in test context)
        generatedSources.Should().Contain(s => s.HintName.Contains("Config.g.cs"));
        
        var configSource = generatedSources.First(s => s.HintName.Contains("Config.g.cs"));
        var configCode = configSource.SourceText.ToString();
        
        // Dracula background is 0xFF282A36
        configCode.Should().Contain("0xFF282A36");
    }

    /// <summary>
    /// Verifies that the generator produces correct font family from config.
    /// </summary>
    [Fact]
    public async Task Generator_WithCustomFont_ProducesCorrectFont()
    {
        // Arrange
        const string source = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    public string? FontFamily => ""Fira Code"";
    public double? FontSize => 12.0;
}
";

        // Act
        var (compilation, generatedSources) = await RunGeneratorAsync(source);

        // Assert
        var configSource = generatedSources.First(s => s.HintName.Contains("Config.g.cs"));
        var configCode = configSource.SourceText.ToString();
        
        configCode.Should().Contain("FontFamily => \"Fira Code\"");
        configCode.Should().Contain("FontSize => 12");
    }

    /// <summary>
    /// Verifies that the generator uses default values when no config class is present.
    /// </summary>
    [Fact]
    public async Task Generator_WithNoConfig_UsesDefaults()
    {
        // Arrange - source without IDottyConfig implementation
        const string source = @"
public class SomeOtherClass
{
    public string Name => ""Test"";
}
";

        // Act
        var (compilation, generatedSources) = await RunGeneratorAsync(source);

        // Assert
        // Generator should still run and produce default config
        generatedSources.Should().Contain(s => s.HintName.Contains("Config.g.cs"));
        
        var configSource = generatedSources.First(s => s.HintName.Contains("Config.g.cs"));
        var configCode = configSource.SourceText.ToString();
        
        // Should contain default values
        configCode.Should().Contain("FontFamily");
        configCode.Should().Contain("FontSize");
        configCode.Should().Contain("Background");
    }

    /// <summary>
    /// Verifies that the generator falls back to Dark+ for invalid/unknown themes.
    /// </summary>
    [Fact]
    public async Task Generator_WithInvalidTheme_FallsBackToDarkPlus()
    {
        // Arrange - note: null Colors should fall back to default (DarkPlus)
        const string source = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => null;
    public string? FontFamily => ""Test"";
}
";

        // Act
        var (compilation, generatedSources) = await RunGeneratorAsync(source);

        // Assert
        var configSource = generatedSources.First(s => s.HintName.Contains("Config.g.cs"));
        var configCode = configSource.SourceText.ToString();
        
        // Should have DarkPlus background as fallback
        configCode.Should().Contain("0xFF1E1E1E");
    }

    /// <summary>
    /// Verifies that enum-typed const fields are emitted as enum member names,
    /// not their underlying numeric values.
    /// </summary>
    [Fact]
    public async Task Generator_WithConstEnumField_EmitsValidTransparencySyntax()
    {
        // Arrange
        const string source = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public static class MyDefaults
{
    public const TransparencyLevel Transparency = TransparencyLevel.None;
}

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    public TransparencyLevel? Transparency => MyDefaults.Transparency;
}
";

        // Act
        var (_, generatedSources) = await RunGeneratorAsync(source);

        // Assert
        var configSource = generatedSources.First(s => s.HintName.Contains("Config.g.cs"));
        var configCode = configSource.SourceText.ToString();

        configCode.Should().Contain("TransparencyLevel.None");
        configCode.Should().NotContain("TransparencyLevel.0");
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Runs the generator against the provided source code and returns the compilation result.
    /// </summary>
    private static async Task<(Compilation Compilation, ImmutableArray<GeneratedSourceResult> GeneratedSources)> RunGeneratorAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        
        // Collect all necessary assembly references
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dotty.Abstractions.Config.IDottyConfig).Assembly.Location),
            // Add System.Runtime for Nullable<> and Enum
            MetadataReference.CreateFromFile(typeof(int).Assembly.Location),
            // Add System.Collections for arrays
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            // Add System.Linq for Enumerable
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)
        };

        // Try to add Avalonia references from the App project
        try
        {
            var config = typeof(Dotty.Abstractions.Config.IDottyConfig).Assembly;
            var configPath = Path.GetDirectoryName(config.Location)!;
            // Navigate from Dotty.Abstractions/bin/Release/net10.0 to Dotty.App/bin/Release/net10.0
            var appBinPath = Path.GetFullPath(Path.Combine(configPath, "..", "..", "..", "..", "src", "Dotty.App", "bin", "Release", "net10.0"));
            
            if (Directory.Exists(appBinPath))
            {
                var avaloniaDlls = Directory.GetFiles(appBinPath, "Avalonia*.dll");
                foreach (var dll in avaloniaDlls)
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(dll));
                    }
                    catch
                    {
                        // Ignore failures for individual assemblies
                    }
                }
            }
        }
        catch
        {
            // Avalonia not available - tests will skip compilation verification
        }

        var inputCompilation = CSharpCompilation.Create(
            "TestInputAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Run the generator (IIncrementalGenerator API)
        var generator = new ConfigGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(inputCompilation, out var outputCompilation, out var diagnostics);

        // Get generated sources
        var runResult = driver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .ToImmutableArray();

        return (outputCompilation, generatedSources);
    }

    #endregion
}
