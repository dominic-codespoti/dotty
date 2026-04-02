using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Dotty.Config.SourceGenerator.Models;
using Dotty.Config.SourceGenerator.Pipeline;

namespace Dotty.Config.SourceGenerator.Pipeline;

/// <summary>
/// Extracts configuration values from IDottyConfig implementations.
/// </summary>
public static class ConfigExtractor
{
    /// <summary>
    /// Extracts all configuration values from a config class.
    /// </summary>
    /// <param name="classSymbol">The symbol for the config class</param>
    /// <param name="compilation">The compilation context</param>
    /// <returns>ConfigModel with all extracted values</returns>
    public static ConfigModel Extract(INamedTypeSymbol classSymbol, Compilation compilation)
    {
        var model = ConfigModel.Default;
        var semanticModel = compilation.GetSemanticModel(classSymbol.DeclaringSyntaxReferences[0].SyntaxTree);

        // Extract each property
        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol property) continue;

            var propertyName = property.Name;
            var syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

            if (syntax is not PropertyDeclarationSyntax propertyDecl) continue;

            object? evaluatedValue = null;

            // Get the value from expression body or initializer
            if (propertyDecl.ExpressionBody?.Expression != null)
            {
                evaluatedValue = ExpressionEvaluator.Evaluate(propertyDecl.ExpressionBody.Expression, semanticModel);
            }
            else if (propertyDecl.Initializer?.Value != null)
            {
                evaluatedValue = ExpressionEvaluator.Evaluate(propertyDecl.Initializer.Value, semanticModel);
            }

            // Apply the value based on property name
            model = propertyName switch
            {
                "FontFamily" => evaluatedValue is string s ? model with { FontFamily = s } : model,
                "FontSize" => evaluatedValue switch
                {
                    double d => model with { FontSize = d },
                    int i => model with { FontSize = i },
                    _ => model
                },
                "CellPadding" => evaluatedValue switch
                {
                    double d => model with { CellPadding = d },
                    int i => model with { CellPadding = i },
                    _ => model
                },
                "ContentPadding" => evaluatedValue is ThicknessModel t
                    ? model with { ContentPadding = t }
                    : model,
                "ScrollbackLines" => evaluatedValue is int sl
                    ? model with { ScrollbackLines = sl }
                    : model,
                "SelectionColor" => evaluatedValue is uint sc
                    ? model with { SelectionColor = sc }
                    : model,
                "TabBarBackgroundColor" => evaluatedValue is uint tb
                    ? model with { TabBarBackgroundColor = tb }
                    : model,
                "InactiveTabDestroyDelayMs" => evaluatedValue is int itd
                    ? model with { InactiveTabDestroyDelayMs = itd }
                    : model,
                "WindowOpacity" => evaluatedValue is int wo
                    ? model with { WindowOpacity = (byte)Clamp(wo, 0, 100) }
                    : model,
                "Transparency" => evaluatedValue != null
                    ? model with { Transparency = evaluatedValue.ToString() ?? DottyDefaults.Transparency }
                    : model,
                _ => model
            };
        }

        // Extract Colors (theme)
        model = ExtractTheme(classSymbol, compilation, model);

        // Extract Cursor settings
        model = ExtractCursor(classSymbol, compilation, model);

        // Extract Window Dimensions
        model = ExtractWindowDimensions(classSymbol, compilation, model);

        return model;
    }

    /// <summary>
    /// Extracts the theme from the Colors property.
    /// </summary>
    private static ConfigModel ExtractTheme(INamedTypeSymbol classSymbol, Compilation compilation, ConfigModel model)
    {
        var colorsProperty = classSymbol.GetMembers("Colors").OfType<IPropertySymbol>().FirstOrDefault();
        if (colorsProperty == null) return model;

        var semanticModel = compilation.GetSemanticModel(colorsProperty.DeclaringSyntaxReferences[0].SyntaxTree);
        var syntax = colorsProperty.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

        if (syntax is PropertyDeclarationSyntax propertyDecl)
        {
            string themeName = "";

            if (propertyDecl.ExpressionBody?.Expression != null)
            {
                themeName = ExtractThemeNameFromExpression(propertyDecl.ExpressionBody.Expression, semanticModel);
            }
            else if (propertyDecl.Initializer?.Value != null)
            {
                themeName = ExtractThemeNameFromExpression(propertyDecl.Initializer.Value, semanticModel);
            }

            if (!string.IsNullOrEmpty(themeName))
            {
                var theme = ThemeResolver.Resolve(themeName);
                return model with { Theme = theme };
            }
        }

        return model;
    }

    /// <summary>
    /// Extracts theme name from an expression.
    /// </summary>
    private static string ExtractThemeNameFromExpression(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        // Handle member access: BuiltInThemes.DarkPlus
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Name.Identifier.ValueText;
        }

        // Handle object creation: new DarkPlusTheme()
        if (expression is ObjectCreationExpressionSyntax objectCreation)
        {
            if (objectCreation.Type is IdentifierNameSyntax identifier)
            {
                return identifier.Identifier.ValueText;
            }
        }

        // Try to resolve the symbol
        var symbolInfo = semanticModel.GetSymbolInfo(expression);
        if (symbolInfo.Symbol is IPropertySymbol prop && prop.Type is INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.Name;
        }
        else if (symbolInfo.Symbol is IMethodSymbol method && method.MethodKind == MethodKind.Constructor)
        {
            return method.ContainingType.Name;
        }

        return "";
    }

    /// <summary>
    /// Extracts cursor settings from the Cursor property.
    /// </summary>
    private static ConfigModel ExtractCursor(INamedTypeSymbol classSymbol, Compilation compilation, ConfigModel model)
    {
        var cursorProperty = classSymbol.GetMembers("Cursor").OfType<IPropertySymbol>().FirstOrDefault();
        if (cursorProperty == null) return model;

        var semanticModel = compilation.GetSemanticModel(cursorProperty.DeclaringSyntaxReferences[0].SyntaxTree);
        var syntax = cursorProperty.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

        if (syntax is PropertyDeclarationSyntax propertyDecl)
        {
            object? cursorValue = null;

            if (propertyDecl.ExpressionBody?.Expression != null)
            {
                cursorValue = ExpressionEvaluator.Evaluate(propertyDecl.ExpressionBody.Expression, semanticModel);
            }
            else if (propertyDecl.Initializer?.Value != null)
            {
                cursorValue = ExpressionEvaluator.Evaluate(propertyDecl.Initializer.Value, semanticModel);
            }

            if (cursorValue is CursorModel cs)
            {
                return model with { Cursor = cs };
            }
        }

        return model;
    }

    /// <summary>
    /// Extracts window dimensions from the InitialDimensions property.
    /// </summary>
    private static ConfigModel ExtractWindowDimensions(INamedTypeSymbol classSymbol, Compilation compilation, ConfigModel model)
    {
        var dimsProperty = classSymbol.GetMembers("InitialDimensions").OfType<IPropertySymbol>().FirstOrDefault();
        if (dimsProperty == null) return model;

        var semanticModel = compilation.GetSemanticModel(dimsProperty.DeclaringSyntaxReferences[0].SyntaxTree);
        var syntax = dimsProperty.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

        if (syntax is PropertyDeclarationSyntax propertyDecl)
        {
            object? dimsValue = null;

            if (propertyDecl.ExpressionBody?.Expression != null)
            {
                dimsValue = ExpressionEvaluator.Evaluate(propertyDecl.ExpressionBody.Expression, semanticModel);
            }
            else if (propertyDecl.Initializer?.Value != null)
            {
                dimsValue = ExpressionEvaluator.Evaluate(propertyDecl.Initializer.Value, semanticModel);
            }

            if (dimsValue is WindowDimensionsModel wd)
            {
                return model with { InitialDimensions = wd };
            }
        }

        return model;
    }

    /// <summary>
    /// Clamps a value to the specified range.
    /// </summary>
    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
