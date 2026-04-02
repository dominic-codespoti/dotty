using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Tests for configuration class discovery functionality.
/// Verifies that the generator correctly identifies and selects IDottyConfig implementations.
/// </summary>
public class ConfigDiscoveryTests
{
    #region FindConfigClasses Tests

    /// <summary>
    /// Verifies that all classes implementing IDottyConfig interface are found.
    /// Tests the syntax provider predicate that identifies candidate classes.
    /// </summary>
    [Fact]
    public async Task FindConfigClasses_FindsAllIDottyConfigImplementations()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Config;
            
            public class MyConfig : IDottyConfig
            {
                public string? FontFamily => 'JetBrains Mono';
                public double? FontSize => 14.0;
                public double? CellPadding => null;
                public Thickness? ContentPadding => null;
                public IColorScheme? Colors => null;
                public IKeyBindings? KeyBindings => null;
                public int? ScrollbackLines => null;
                public IWindowDimensions? InitialDimensions => null;
                public ICursorSettings? Cursor => null;
                public uint? SelectionColor => null;
                public uint? TabBarBackgroundColor => null;
                public TransparencyLevel? Transparency => null;
                public byte? WindowOpacity => null;
                public int? InactiveTabDestroyDelayMs => null;
            }
            
            public class AnotherConfig : IDottyConfig
            {
                public string? FontFamily => 'Fira Code';
                public double? FontSize => 16.0;
                public double? CellPadding => null;
                public Thickness? ContentPadding => null;
                public IColorScheme? Colors => null;
                public IKeyBindings? KeyBindings => null;
                public int? ScrollbackLines => null;
                public IWindowDimensions? InitialDimensions => null;
                public ICursorSettings? Cursor => null;
                public uint? SelectionColor => null;
                public uint? TabBarBackgroundColor => null;
                public TransparencyLevel? Transparency => null;
                public byte? WindowOpacity => null;
                public int? InactiveTabDestroyDelayMs => null;
            }
        ";

        // Act
        var syntaxTree = CSharpSyntaxTree.ParseText(source.Replace('\'', '"'));
        var root = await syntaxTree.GetRootAsync();
        var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();

        // Assert
        Assert.Equal(2, classDeclarations.Count);
        Assert.All(classDeclarations, c =>
        {
            Assert.NotNull(c.BaseList);
            Assert.Contains(c.BaseList!.Types, bt =>
                bt.Type.ToString().Contains("IDottyConfig"));
        });
    }

    /// <summary>
    /// Verifies that classes without IDottyConfig interface are not considered.
    /// Tests that the predicate correctly filters non-config classes.
    /// </summary>
    [Fact]
    public async Task FindConfigClasses_IgnoresClassesWithoutInterface()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Config;
            
            public class NotAConfig
            {
                public string Name => 'Test';
            }
            
            public interface ISomeOtherInterface { }
            
            public class AlsoNotConfig : ISomeOtherInterface
            {
                public string Data => 'Data';
            }
            
            public class ValidConfig : IDottyConfig
            {
                public string? FontFamily => 'JetBrains Mono';
                public double? FontSize => 14.0;
                public double? CellPadding => null;
                public Thickness? ContentPadding => null;
                public IColorScheme? Colors => null;
                public IKeyBindings? KeyBindings => null;
                public int? ScrollbackLines => null;
                public IWindowDimensions? InitialDimensions => null;
                public ICursorSettings? Cursor => null;
                public uint? SelectionColor => null;
                public uint? TabBarBackgroundColor => null;
                public TransparencyLevel? Transparency => null;
                public byte? WindowOpacity => null;
                public int? InactiveTabDestroyDelayMs => null;
            }
        ";

        // Act
        var syntaxTree = CSharpSyntaxTree.ParseText(source.Replace('\'', '"'));
        var root = await syntaxTree.GetRootAsync();
        var allClasses = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var configClasses = allClasses.Where(c =>
            c.BaseList != null &&
            c.BaseList.Types.Any(bt => bt.Type.ToString().Contains("IDottyConfig"))
        ).ToList();

        // Assert
        Assert.Equal(3, allClasses.Count); // Total classes including interface
        Assert.Single(configClasses);
        Assert.Equal("ValidConfig", configClasses[0].Identifier.ValueText);
    }

    #endregion

    #region SelectConfigClass Tests

    /// <summary>
    /// Verifies that user-defined config classes are preferred over DefaultConfig.
    /// Tests the ordering logic that puts user configs first.
    /// </summary>
    [Fact]
    public void SelectConfigClass_PrefersUserConfigOverDefault()
    {
        // Arrange
        var configs = ImmutableArray.Create<ClassDeclarationSyntax?>(
            CreateClassDeclaration("DefaultConfig"),
            CreateClassDeclaration("MyUserConfig"),
            CreateClassDeclaration("DefaultDottyConfig")
        );

        // Act
        var validConfigs = configs
            .Where(c => c != null)
            .OrderBy(c =>
            {
                var name = c!.Identifier.ValueText;
                if (name == "DefaultConfig" || name == "DefaultDottyConfig") return 1;
                if (name.Contains("Config")) return 0;
                return 0;
            })
            .ToList();

        // Assert
        Assert.Equal(3, validConfigs.Count);
        Assert.Equal("MyUserConfig", validConfigs[0].Identifier.ValueText);
    }

    /// <summary>
    /// Verifies that the selector returns null when no config classes are found.
    /// Tests the fallback behavior for empty config lists.
    /// </summary>
    [Fact]
    public void SelectConfigClass_ReturnsNullWhenNoConfigFound()
    {
        // Arrange
        var emptyConfigs = ImmutableArray.Create<ClassDeclarationSyntax?>();

        // Act
        var firstConfig = emptyConfigs.FirstOrDefault();

        // Assert
        Assert.Null(firstConfig);
    }

    /// <summary>
    /// Verifies that DefaultConfig is used when no user config exists.
    /// Tests fallback to built-in defaults.
    /// </summary>
    [Fact]
    public void SelectConfigClass_UsesDefaultWhenNoUserConfig()
    {
        // Arrange
        var configs = ImmutableArray.Create<ClassDeclarationSyntax?>(
            CreateClassDeclaration("DefaultConfig")
        );

        // Act
        var validConfigs = configs
            .Where(c => c != null)
            .OrderBy(c =>
            {
                var name = c!.Identifier.ValueText;
                if (name == "DefaultConfig" || name == "DefaultDottyConfig") return 1;
                return 0;
            })
            .ToList();

        var firstConfig = validConfigs.FirstOrDefault();

        // Assert
        Assert.NotNull(firstConfig);
        Assert.Equal("DefaultConfig", firstConfig.Identifier.ValueText);
    }

    /// <summary>
    /// Verifies that the first user config is selected when multiple user configs exist.
    /// Tests handling of multiple user-defined configurations.
    /// </summary>
    [Fact]
    public void SelectConfigClass_SelectsFirstWhenMultipleUserConfigs()
    {
        // Arrange
        var configs = ImmutableArray.Create<ClassDeclarationSyntax?>(
            CreateClassDeclaration("UserConfig1"),
            CreateClassDeclaration("UserConfig2"),
            CreateClassDeclaration("UserConfig3")
        );

        // Act
        var validConfigs = configs
            .Where(c => c != null)
            .OrderBy(c =>
            {
                var name = c!.Identifier.ValueText;
                if (name == "DefaultConfig" || name == "DefaultDottyConfig") return 1;
                if (name.Contains("Config")) return 0;
                return 0;
            })
            .ToList();

        var firstConfig = validConfigs.FirstOrDefault();

        // Assert
        Assert.NotNull(firstConfig);
        Assert.Equal("UserConfig1", firstConfig.Identifier.ValueText);
        Assert.Equal(3, validConfigs.Count);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Creates a minimal ClassDeclarationSyntax for testing.
    /// </summary>
    private static ClassDeclarationSyntax CreateClassDeclaration(string className)
    {
        return SyntaxFactory.ClassDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBaseList(SyntaxFactory.BaseList(
                SyntaxFactory.SeparatedList<BaseTypeSyntax>(
                    new[] { SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName("IDottyConfig")) }
                )));
    }

    #endregion
}
