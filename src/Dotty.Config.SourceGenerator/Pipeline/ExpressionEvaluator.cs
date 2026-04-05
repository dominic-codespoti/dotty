using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Dotty.Config.SourceGenerator.Models;

namespace Dotty.Config.SourceGenerator.Pipeline;

/// <summary>
/// Evaluates C# expressions to extract constant values.
/// </summary>
public static class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates a C# expression and returns its value.
    /// Supports: literals, member access, object creation, casts, binary operations.
    /// </summary>
    /// <param name="expression">The expression to evaluate</param>
    /// <param name="semanticModel">The semantic model for symbol resolution</param>
    /// <returns>The evaluated value, or null if cannot be evaluated</returns>
    public static object? Evaluate(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        // Handle literal expressions
        if (expression is LiteralExpressionSyntax literal)
        {
            return literal.Token.Value;
        }

        // Handle member access: BuiltInThemes.DarkPlus or SomeClass.Property
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            return EvaluateMemberAccess(memberAccess, semanticModel);
        }

        // Handle object creation: new Thickness(10) or new CursorSettings { ... }
        if (expression is ObjectCreationExpressionSyntax objectCreation)
        {
            return EvaluateObjectCreation(objectCreation, semanticModel);
        }

        // Handle binary expressions (simple arithmetic)
        if (expression is BinaryExpressionSyntax binary)
        {
            return EvaluateBinaryExpression(binary, semanticModel);
        }

        // Handle cast expressions
        if (expression is CastExpressionSyntax cast)
        {
            return EvaluateCastExpression(cast, semanticModel);
        }

        // Handle parenthesized expressions
        if (expression is ParenthesizedExpressionSyntax paren)
        {
            return Evaluate(paren.Expression, semanticModel);
        }

        // Handle prefix unary expressions (e.g., -5, !true)
        if (expression is PrefixUnaryExpressionSyntax prefixUnary)
        {
            return EvaluatePrefixUnary(prefixUnary, semanticModel);
        }

        // Handle identifier names
        if (expression is IdentifierNameSyntax identifier)
        {
            return EvaluateIdentifier(identifier, semanticModel);
        }

        // Try to get constant value through semantic model
        var constantValue = semanticModel.GetConstantValue(expression);
        if (constantValue.HasValue)
        {
            return constantValue.Value;
        }

        return null;
    }

    /// <summary>
    /// Evaluates a member access expression.
    /// </summary>
    private static object? EvaluateMemberAccess(MemberAccessExpressionSyntax memberAccess, SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);

        if (symbolInfo.Symbol is IFieldSymbol field)
        {
            // For enum members, return the member name.
            if (field.ContainingType?.TypeKind == TypeKind.Enum)
            {
                return field.Name;
            }

            // For const fields typed as enums (e.g. DottyDefaults.Transparency),
            // return the enum member name instead of the underlying numeric value.
            if (field.IsConst && field.Type.TypeKind == TypeKind.Enum)
            {
                var enumMember = field.Type
                    .GetMembers()
                    .OfType<IFieldSymbol>()
                    .FirstOrDefault(member =>
                        member.HasConstantValue &&
                        !member.IsImplicitlyDeclared &&
                        Equals(member.ConstantValue, field.ConstantValue));

                if (enumMember != null)
                {
                    return enumMember.Name;
                }
            }

            // For other const fields, return the constant value.
            if (field.IsConst)
            {
                return field.ConstantValue;
            }
        }

        // For properties returning built-in types, check the type
        if (symbolInfo.Symbol is IPropertySymbol property)
        {
            var typeName = property.Type.Name;
            if (typeName.EndsWith("Theme"))
            {
                return typeName; // Return theme name for special handling
            }
        }

        // Return the member name for downstream processing
        return memberAccess.Name.Identifier.ValueText;
    }

    /// <summary>
    /// Evaluates an object creation expression.
    /// </summary>
    private static object? EvaluateObjectCreation(ObjectCreationExpressionSyntax objectCreation, SemanticModel semanticModel)
    {
        var typeName = objectCreation.Type.ToString();

        // Handle Thickness
        if (typeName == "Thickness")
        {
            return EvaluateThickness(objectCreation, semanticModel);
        }

        // Handle CursorSettings
        if (typeName == "CursorSettings")
        {
            return EvaluateCursorSettings(objectCreation, semanticModel);
        }

        // Handle WindowDimensions
        if (typeName == "WindowDimensions")
        {
            return EvaluateWindowDimensions(objectCreation, semanticModel);
        }

        return typeName;
    }

    /// <summary>
    /// Evaluates a Thickness object creation.
    /// </summary>
    private static object? EvaluateThickness(ObjectCreationExpressionSyntax objectCreation, SemanticModel semanticModel)
    {
        var argsList = objectCreation.ArgumentList;
        if (argsList == null || argsList.Arguments.Count == 0)
        {
            // Try initializer syntax
            if (objectCreation.Initializer != null)
            {
                return EvaluateThicknessFromInitializer(objectCreation.Initializer, semanticModel);
            }
            return new ThicknessModel(0, 0, 0, 0);
        }

        var args = argsList.Arguments;
        var argValues = new List<double?>();
        foreach (var arg in args)
        {
            var val = Evaluate(arg.Expression, semanticModel);
            argValues.Add(val is double d ? d : (val is int i ? (double)i : null));
        }

        if (argValues.Count == 1 && argValues[0] is double v0)
            return new ThicknessModel(v0);
        if (argValues.Count == 2 && argValues[0] is double v1 && argValues[1] is double v2)
            return new ThicknessModel(v1, v2);
        if (argValues.Count == 4 && argValues[0] is double v3 && argValues[1] is double v4 && argValues[2] is double v5 && argValues[3] is double v6)
            return new ThicknessModel(v3, v4, v5, v6);

        return new ThicknessModel(0, 0, 0, 0);
    }

    /// <summary>
    /// Evaluates Thickness from object initializer.
    /// </summary>
    private static ThicknessModel EvaluateThicknessFromInitializer(InitializerExpressionSyntax initializer, SemanticModel semanticModel)
    {
        double left = 0, top = 0, right = 0, bottom = 0;

        foreach (var init in initializer.Expressions)
        {
            if (init is AssignmentExpressionSyntax assignment)
            {
                var propName = assignment.Left.ToString();
                var propValue = Evaluate(assignment.Right, semanticModel);
                var doubleValue = propValue is double d ? d : (propValue is int i ? (double)i : 0);

                switch (propName)
                {
                    case "Left": left = doubleValue; break;
                    case "Top": top = doubleValue; break;
                    case "Right": right = doubleValue; break;
                    case "Bottom": bottom = doubleValue; break;
                }
            }
        }

        return new ThicknessModel(left, top, right, bottom);
    }

    /// <summary>
    /// Evaluates a CursorSettings object creation.
    /// </summary>
    private static object? EvaluateCursorSettings(ObjectCreationExpressionSyntax objectCreation, SemanticModel semanticModel)
    {
        var cursor = new CursorModel();

        // Handle arguments
        if (objectCreation.ArgumentList?.Arguments.Count > 0)
        {
            var args = objectCreation.ArgumentList.Arguments;
            if (args.Count >= 1)
            {
                var shapeValue = Evaluate(args[0].Expression, semanticModel);
                cursor = cursor with { Shape = shapeValue?.ToString() ?? "Block" };
            }
        }

        // Handle initializer if present
        if (objectCreation.Initializer != null)
        {
            foreach (var init in objectCreation.Initializer.Expressions)
            {
                if (init is AssignmentExpressionSyntax assignment)
                {
                    var propName = assignment.Left.ToString();
                    var propValue = Evaluate(assignment.Right, semanticModel);

                    cursor = propName switch
                    {
                        "Shape" => cursor with { Shape = propValue?.ToString() ?? "Block" },
                        "Blink" => cursor with { Blink = propValue is true },
                        "BlinkIntervalMs" => propValue is int bi ? cursor with { BlinkIntervalMs = bi } : cursor,
                        "Color" => propValue is uint cc ? cursor with { Color = cc } : cursor,
                        "ShowUnfocused" => cursor with { ShowUnfocused = propValue is true },
                        _ => cursor
                    };
                }
            }
        }

        return cursor;
    }

    /// <summary>
    /// Evaluates a WindowDimensions object creation.
    /// </summary>
    private static object? EvaluateWindowDimensions(ObjectCreationExpressionSyntax objectCreation, SemanticModel semanticModel)
    {
        var dims = new WindowDimensionsModel();

        // Handle initializer if present
        if (objectCreation.Initializer != null)
        {
            foreach (var init in objectCreation.Initializer.Expressions)
            {
                if (init is AssignmentExpressionSyntax assignment)
                {
                    var propName = assignment.Left.ToString();
                    var propValue = Evaluate(assignment.Right, semanticModel);

                    dims = propName switch
                    {
                        "Columns" => propValue is int c ? dims with { Columns = c } : dims,
                        "Rows" => propValue is int r ? dims with { Rows = r } : dims,
                        "Title" => propValue is string t ? dims with { Title = t } : dims,
                        "StartFullscreen" => dims with { StartFullscreen = propValue is true },
                        _ => dims
                    };
                }
            }
        }

        return dims;
    }

    /// <summary>
    /// Evaluates a binary expression.
    /// </summary>
    private static object? EvaluateBinaryExpression(BinaryExpressionSyntax binary, SemanticModel semanticModel)
    {
        var left = Evaluate(binary.Left, semanticModel);
        var right = Evaluate(binary.Right, semanticModel);

        if (left is double ld && right is double rd)
        {
            return binary.OperatorToken.ValueText switch
            {
                "+" => ld + rd,
                "-" => ld - rd,
                "*" => ld * rd,
                "/" => rd != 0 ? ld / rd : 0,
                _ => null
            };
        }

        if (left is int li && right is int ri)
        {
            return binary.OperatorToken.ValueText switch
            {
                "+" => li + ri,
                "-" => li - ri,
                "*" => li * ri,
                "/" => ri != 0 ? li / ri : 0,
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Evaluates a cast expression.
    /// </summary>
    private static object? EvaluateCastExpression(CastExpressionSyntax cast, SemanticModel semanticModel)
    {
        var innerValue = Evaluate(cast.Expression, semanticModel);
        var targetType = cast.Type.ToString();

        if (innerValue is int i)
        {
            return targetType switch
            {
                "uint" => (uint)i,
                "double" => (double)i,
                "byte" => (byte)i,
                _ => innerValue
            };
        }

        return innerValue;
    }

    /// <summary>
    /// Evaluates a prefix unary expression.
    /// </summary>
    private static object? EvaluatePrefixUnary(PrefixUnaryExpressionSyntax unary, SemanticModel semanticModel)
    {
        var operand = Evaluate(unary.Operand, semanticModel);

        return unary.OperatorToken.ValueText switch
        {
            "-" => operand is int i ? -i : (operand is double d ? -d : null),
            "!" => operand is bool b ? !b : null,
            "~" => operand is int ii ? ~ii : null,
            _ => null
        };
    }

    /// <summary>
    /// Evaluates an identifier name.
    /// </summary>
    private static object? EvaluateIdentifier(IdentifierNameSyntax identifier, SemanticModel semanticModel)
    {
        var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;
        if (symbol is IFieldSymbol field && field.IsConst)
            return field.ConstantValue;
        if (symbol is ILocalSymbol local && local.HasConstantValue)
            return local.ConstantValue;

        return null;
    }
}
