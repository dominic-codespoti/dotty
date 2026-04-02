using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dotty.Config.SourceGenerator.Pipeline;

/// <summary>
/// Discovers IDottyConfig implementations in the compilation.
/// </summary>
public static class ConfigDiscovery
{
    /// <summary>
    /// Finds all class declarations that implement IDottyConfig.
    /// </summary>
    /// <param name="compilation">The compilation to search</param>
    /// <returns>Array of class declarations implementing IDottyConfig</returns>
    public static ClassDeclarationSyntax[] FindConfigClasses(Compilation compilation)
    {
        var result = new List<ClassDeclarationSyntax>();

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (ImplementsIDottyConfig(classDecl, semanticModel))
                {
                    result.Add(classDecl);
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Checks if a class declaration implements IDottyConfig.
    /// </summary>
    private static bool ImplementsIDottyConfig(ClassDeclarationSyntax classDecl, SemanticModel semanticModel)
    {
        if (classDecl.BaseList == null)
            return false;

        foreach (var baseType in classDecl.BaseList.Types)
        {
            var symbol = semanticModel.GetSymbolInfo(baseType.Type).Symbol;
            if (symbol is INamedTypeSymbol namedType)
            {
                var fullName = namedType.ToDisplayString();
                if (fullName == "Dotty.Abstractions.Config.IDottyConfig" ||
                    fullName.EndsWith("IDottyConfig"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Selects the best config class from multiple candidates.
    /// Priority: user configs over default configs.
    /// </summary>
    /// <param name="configClasses">Array of candidate config classes</param>
    /// <returns>The selected config class, or null if none found</returns>
    public static ClassDeclarationSyntax? SelectConfigClass(ClassDeclarationSyntax[] configClasses)
    {
        if (configClasses.Length == 0)
            return null;

        if (configClasses.Length == 1)
            return configClasses[0];

        // Order by priority (user configs first, default configs last)
        var ordered = configClasses
            .OrderBy(c => GetPriorityScore(c))
            .ToArray();

        return ordered[0];
    }

    /// <summary>
    /// Gets a priority score for a config class (lower = higher priority).
    /// </summary>
    private static int GetPriorityScore(ClassDeclarationSyntax classDecl)
    {
        var name = classDecl.Identifier.ValueText;

        // Default configs have lowest priority
        if (name == "DefaultConfig" || name == "DefaultDottyConfig")
            return 100;

        // User configs have highest priority
        if (name.Contains("Config"))
            return 0;

        return 50; // Medium priority for others
    }
}
