using Microsoft.CodeAnalysis;

namespace Dotty.Config.SourceGenerator.Diagnostics;

/// <summary>
/// Diagnostic descriptors for the configuration source generator.
/// </summary>
public static class GeneratorDiagnostics
{
    /// <summary>
    /// Category for diagnostics.
    /// </summary>
    private const string Category = "DottyConfig";

    /// <summary>
    /// DOTTY001: Multiple IDottyConfig implementations found.
    /// </summary>
    public static readonly DiagnosticDescriptor MultipleConfigsFound = new(
        id: "DOTTY001",
        title: "Multiple configuration classes found",
        messageFormat: "Found {0} IDottyConfig implementations. Using '{1}'. Consider removing unused configurations.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Multiple classes implementing IDottyConfig were found. The generator will use the first one with highest priority."
    );

    /// <summary>
    /// DOTTY002: No IDottyConfig implementation found.
    /// </summary>
    public static readonly DiagnosticDescriptor NoConfigFound = new(
        id: "DOTTY002",
        title: "No configuration class found",
        messageFormat: "No IDottyConfig implementation found. Using default configuration.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "No class implementing IDottyConfig was found. The generator will use default values."
    );

    /// <summary>
    /// DOTTY003: Unsupported expression in configuration.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedExpression = new(
        id: "DOTTY003",
        title: "Unsupported expression",
        messageFormat: "Could not evaluate expression for property '{0}'. Using default value.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator could not evaluate a C# expression in the configuration. The default value will be used."
    );

    /// <summary>
    /// DOTTY004: Invalid theme name specified.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidThemeName = new(
        id: "DOTTY004",
        title: "Invalid theme name",
        messageFormat: "Theme '{0}' not found. Falling back to DarkPlus.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The specified theme name could not be resolved. The default DarkPlus theme will be used."
    );

    /// <summary>
    /// Creates a diagnostic for multiple configs found.
    /// </summary>
    public static Diagnostic CreateMultipleConfigsFound(Location location, int count, string selectedName)
    {
        return Diagnostic.Create(MultipleConfigsFound, location, count, selectedName);
    }

    /// <summary>
    /// Creates a diagnostic for no config found.
    /// </summary>
    public static Diagnostic CreateNoConfigFound(Location? location = null)
    {
        return Diagnostic.Create(NoConfigFound, location);
    }

    /// <summary>
    /// Creates a diagnostic for an unsupported expression.
    /// </summary>
    public static Diagnostic CreateUnsupportedExpression(Location location, string propertyName)
    {
        return Diagnostic.Create(UnsupportedExpression, location, propertyName);
    }

    /// <summary>
    /// Creates a diagnostic for an invalid theme name.
    /// </summary>
    public static Diagnostic CreateInvalidThemeName(Location location, string themeName)
    {
        return Diagnostic.Create(InvalidThemeName, location, themeName);
    }
}
