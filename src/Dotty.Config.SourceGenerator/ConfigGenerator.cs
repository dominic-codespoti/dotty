using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Dotty.Config.SourceGenerator.Diagnostics;
using Dotty.Config.SourceGenerator.Emission;
using Dotty.Config.SourceGenerator.Models;
using Dotty.Config.SourceGenerator.Pipeline;

namespace Dotty.Config.SourceGenerator;

/// <summary>
/// Source generator that scans for IDottyConfig implementations and generates
/// a static Config class with strongly-typed configuration values.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class ConfigGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the syntax provider to find IDottyConfig implementations
        var configClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax c && c.BaseList != null,
                transform: (ctx, _) => GetConfigClass(ctx))
            .Where(c => c is not null)
            .Collect();

        // Combine with compilation
        var compilationAndConfigs = context.CompilationProvider.Combine(configClasses);

        // Register the source output
        context.RegisterSourceOutput(compilationAndConfigs, (ctx, source) =>
        {
            var (compilation, configs) = source;
            Execute(ctx, compilation, configs);
        });
    }

    private static ClassDeclarationSyntax? GetConfigClass(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        foreach (var baseType in classDecl.BaseList!.Types)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(baseType.Type).Symbol;
            if (symbol is INamedTypeSymbol namedType)
            {
                var fullName = namedType.ToDisplayString();
                if (fullName == "Dotty.Abstractions.Config.IDottyConfig" ||
                    fullName.EndsWith("IDottyConfig"))
                {
                    return classDecl;
                }
            }
        }

        return null;
    }

    private static void Execute(SourceProductionContext context, Compilation compilation, System.Collections.Immutable.ImmutableArray<ClassDeclarationSyntax?> configClasses)
    {
        // Filter out null entries
        var validConfigs = configClasses.Where(c => c != null).Cast<ClassDeclarationSyntax>().ToArray();

        // Select the best config class (user configs take priority over defaults)
        var selectedConfig = ConfigDiscovery.SelectConfigClass(validConfigs);

        // Report diagnostics
        if (ConfigDiscovery.CountUserConfigs(validConfigs) > 1 && selectedConfig != null)
        {
            var location = selectedConfig.GetLocation();
            context.ReportDiagnostic(GeneratorDiagnostics.CreateMultipleConfigsFound(location, validConfigs.Length, selectedConfig.Identifier.ValueText));
        }
        else if (validConfigs.Length == 0)
        {
            context.ReportDiagnostic(GeneratorDiagnostics.CreateNoConfigFound());
        }

        // Extract configuration values
        ConfigModel configModel;
        if (selectedConfig != null)
        {
            var semanticModel = compilation.GetSemanticModel(selectedConfig.SyntaxTree);
            var classSymbol = semanticModel.GetDeclaredSymbol(selectedConfig);

            if (classSymbol != null)
            {
                configModel = ConfigExtractor.Extract(classSymbol, compilation);
            }
            else
            {
                configModel = ConfigModel.Default;
            }
        }
        else
        {
            configModel = ConfigModel.Default;
        }

        // Generate the three source files
        var configSource = ConfigEmitter.Generate(configModel);
        var colorSchemeSource = ColorSchemeEmitter.Generate(configModel.Theme);
        var keyBindingsSource = KeyBindingsEmitter.Generate();

        // Add the generated sources
        context.AddSource("Dotty.Generated.Config.g.cs", configSource);
        context.AddSource("Dotty.Generated.ColorScheme.g.cs", colorSchemeSource);
        context.AddSource("Dotty.Generated.KeyBindings.g.cs", keyBindingsSource);
    }
}
