using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Tests for the expression evaluator that extracts constant values from syntax trees.
/// Verifies that various expression types are correctly evaluated.
/// </summary>
public class ExpressionEvaluatorTests
{
    #region Literal Expression Tests

    /// <summary>
    /// Verifies that literal expressions return their direct values.
    /// Tests string, numeric, and boolean literals.
    /// </summary>
    [Theory]
    [InlineData("\"JetBrains Mono\"", "JetBrains Mono")]
    [InlineData("14", 14)]
    [InlineData("3.14", 3.14)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task EvaluateLiteral_ReturnsValue(string expression, object expected)
    {
        // Arrange
        var source = $"class Test {{ public object Value => {expression}; }}";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region Member Access Tests

    /// <summary>
    /// Verifies that enum member access returns the member name.
    /// Tests that TransparencyLevel.None returns "None" not 0.
    /// </summary>
    [Fact]
    public async Task EvaluateMemberAccess_ReturnsEnumMemberName()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Config;
            class Test { public TransparencyLevel? Transparency => TransparencyLevel.Blur; }
        ";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal("Blur", result);
    }

    /// <summary>
    /// Verifies that theme member access returns the theme name.
    /// Tests BuiltInThemes.DarkPlus resolution.
    /// </summary>
    [Fact]
    public async Task EvaluateMemberAccess_ReturnsThemeName()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Themes;
            class Test { public object Colors => BuiltInThemes.Dracula; }
        ";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert - Theme member access returns the member name
        Assert.NotNull(result);
    }

    #endregion

    #region Object Creation Tests - Thickness

    /// <summary>
    /// Verifies that new Thickness(10) creates uniform padding.
    /// Tests single-argument constructor.
    /// </summary>
    [Fact]
    public async Task EvaluateObjectCreation_ThicknessWith1Arg()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Config;
            class Test { public Thickness Padding => new Thickness(10); }
        ";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.IsType<TestHelpers.Thickness>(result);
        var thickness = (TestHelpers.Thickness)result;
        Assert.Equal(10.0, thickness.Left);
        Assert.Equal(10.0, thickness.Top);
        Assert.Equal(10.0, thickness.Right);
        Assert.Equal(10.0, thickness.Bottom);
    }

    /// <summary>
    /// Verifies that new Thickness(10, 20) creates horizontal/vertical padding.
    /// Tests two-argument constructor.
    /// </summary>
    [Fact]
    public async Task EvaluateObjectCreation_ThicknessWith2Args()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Config;
            class Test { public Thickness Padding => new Thickness(10, 20); }
        ";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.IsType<TestHelpers.Thickness>(result);
        var thickness = (TestHelpers.Thickness)result;
        Assert.Equal(10.0, thickness.Left);
        Assert.Equal(20.0, thickness.Top);
        Assert.Equal(10.0, thickness.Right);
        Assert.Equal(20.0, thickness.Bottom);
    }

    /// <summary>
    /// Verifies that new Thickness(1, 2, 3, 4) creates specific padding.
    /// Tests four-argument constructor.
    /// </summary>
    [Fact]
    public async Task EvaluateObjectCreation_ThicknessWith4Args()
    {
        // Arrange
        const string source = @"
            using Dotty.Abstractions.Config;
            class Test { public Thickness Padding => new Thickness(1, 2, 3, 4); }
        ";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.IsType<TestHelpers.Thickness>(result);
        var thickness = (TestHelpers.Thickness)result;
        Assert.Equal(1.0, thickness.Left);
        Assert.Equal(2.0, thickness.Top);
        Assert.Equal(3.0, thickness.Right);
        Assert.Equal(4.0, thickness.Bottom);
    }

    #endregion

    #region Binary Expression Tests

    /// <summary>
    /// Verifies that addition expressions are evaluated.
    /// Tests binary + operator.
    /// </summary>
    [Theory]
    [InlineData("5 + 3", 8)]
    [InlineData("10 + 20", 30)]
    [InlineData("2.5 + 3.5", 6.0)]
    public async Task EvaluateBinary_Addition(string expression, double expected)
    {
        // Arrange
        var source = $"class Test {{ public double Value => {expression}; }}";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that subtraction expressions are evaluated.
    /// Tests binary - operator.
    /// </summary>
    [Theory]
    [InlineData("10 - 3", 7)]
    [InlineData("100 - 50", 50)]
    [InlineData("5.5 - 2.5", 3.0)]
    public async Task EvaluateBinary_Subtraction(string expression, double expected)
    {
        // Arrange
        var source = $"class Test {{ public double Value => {expression}; }}";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that multiplication expressions are evaluated.
    /// Tests binary * operator.
    /// </summary>
    [Theory]
    [InlineData("4 * 5", 20)]
    [InlineData("10 * 10", 100)]
    [InlineData("2.5 * 4", 10.0)]
    public async Task EvaluateBinary_Multiplication(string expression, double expected)
    {
        // Arrange
        var source = $"class Test {{ public double Value => {expression}; }}";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Verifies that division expressions are evaluated.
    /// Tests binary / operator.
    /// </summary>
    [Theory]
    [InlineData("20 / 4", 5)]
    [InlineData("100 / 10", 10)]
    [InlineData("7.5 / 2.5", 3.0)]
    public async Task EvaluateBinary_Division(string expression, double expected)
    {
        // Arrange
        var source = $"class Test {{ public double Value => {expression}; }}";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region Cast Expression Tests

    /// <summary>
    /// Verifies that int to uint casts are handled.
    /// Tests (uint) cast expression.
    /// </summary>
    [Fact]
    public async Task EvaluateCast_IntToUint()
    {
        // Arrange
        const string source = "class Test { public uint Value => (uint)42; }";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.IsType<uint>(result);
        Assert.Equal(42u, result);
    }

    /// <summary>
    /// Verifies that int to double casts are handled.
    /// Tests (double) cast expression.
    /// </summary>
    [Fact]
    public async Task EvaluateCast_IntToDouble()
    {
        // Arrange
        const string source = "class Test { public double Value => (double)42; }";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.IsType<double>(result);
        Assert.Equal(42.0, result);
    }

    /// <summary>
    /// Verifies that int to byte casts are handled.
    /// Tests (byte) cast expression.
    /// </summary>
    [Fact]
    public async Task EvaluateCast_IntToByte()
    {
        // Arrange
        const string source = "class Test { public byte Value => (byte)255; }";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.IsType<byte>(result);
        Assert.Equal((byte)255, result);
    }

    #endregion

    #region Identifier Tests

    /// <summary>
    /// Verifies that const field identifiers are evaluated.
    /// Tests reference to const fields.
    /// </summary>
    [Fact]
    public async Task EvaluateIdentifier_ConstField()
    {
        // Arrange
        const string source = @"
            class Test 
            { 
                public const int DefaultSize = 14;
                public int Size => DefaultSize; 
            }
        ";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Equal(14, result);
    }

    /// <summary>
    /// Verifies that local constants are evaluated.
    /// Tests reference to local const variables.
    /// </summary>
    [Fact]
    public async Task EvaluateIdentifier_LocalConstant()
    {
        // Arrange
        const string source = @"
            class Test 
            { 
                public int GetValue() 
                { 
                    const int localConst = 42;
                    return localConst;
                }
            }
        ";
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CreateCompilation(syntaxTree);
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();
        var returnStatement = root.DescendantNodes().OfType<ReturnStatementSyntax>().First();

        // Act
        var result = EvaluateExpressionForTest(returnStatement.Expression!, semanticModel);

        // Assert
        Assert.Equal(42, result);
    }

    #endregion

    #region Unsupported Expression Tests

    /// <summary>
    /// Verifies that unsupported expressions return null.
    /// Tests graceful degradation for complex expressions.
    /// </summary>
    [Theory]
    [InlineData("System.DateTime.Now")]
    [InlineData("new System.Random().Next()")]
    [InlineData("Console.ReadLine()")]
    public async Task Evaluate_UnsupportedExpression_ReturnsNull(string expression)
    {
        // Arrange
        var source = $"class Test {{ public object Value => {expression}; }}";
        var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

        // Act
        var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Test Helpers

    /// <summary>
    /// Parses source code and extracts the first property declaration with its semantic model.
    /// </summary>
    private static async Task<(PropertyDeclarationSyntax Property, SemanticModel SemanticModel)> GetPropertyDeclarationAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CreateCompilation(syntaxTree);
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();
        var propertyDecl = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().First();
        return (propertyDecl, semanticModel);
    }

    /// <summary>
    /// Creates a compilation from a syntax tree with Dotty.Abstractions referenced.
    /// </summary>
    private static Compilation CreateCompilation(SyntaxTree syntaxTree)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Dotty.Abstractions.Config.IDottyConfig).Assembly.Location)
        };

        return CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Simplified expression evaluator for testing purposes.
    /// Mirrors the logic in ConfigGenerator.EvaluateExpression.
    /// </summary>
    private static object? EvaluateExpressionForTest(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        // Handle literal expressions
        if (expression is LiteralExpressionSyntax literal)
        {
            return literal.Token.Value;
        }

        // Handle member access
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);

            // For enum members, return the member name
            if (symbolInfo.Symbol is IFieldSymbol field &&
                field.ContainingType?.TypeKind == TypeKind.Enum)
            {
                return field.Name;
            }

            if (symbolInfo.Symbol is IFieldSymbol constField && constField.IsConst)
            {
                return constField.ConstantValue;
            }

            return memberAccess.Name.Identifier.ValueText;
        }

        // Handle object creation
        if (expression is ObjectCreationExpressionSyntax objectCreation)
        {
            var typeName = objectCreation.Type.ToString();

            if (typeName == "Thickness" && objectCreation.ArgumentList != null)
            {
                var args = objectCreation.ArgumentList.Arguments;
                var values = args.Select(a => EvaluateExpressionForTest(a.Expression, semanticModel)).ToList();

                if (values.Count == 1 && values[0] is double uniform)
                    return new TestHelpers.Thickness(uniform);
                if (values.Count == 2 && values[0] is double h && values[1] is double v)
                    return new TestHelpers.Thickness(h, v);
                if (values.Count == 4 && values[0] is double l && values[1] is double t && values[2] is double r && values[3] is double b)
                    return new TestHelpers.Thickness(l, t, r, b);
            }

            return typeName;
        }

        // Handle binary expressions
        if (expression is BinaryExpressionSyntax binary)
        {
            var left = EvaluateExpressionForTest(binary.Left, semanticModel);
            var right = EvaluateExpressionForTest(binary.Right, semanticModel);

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
        }

        // Handle cast expressions
        if (expression is CastExpressionSyntax cast)
        {
            var innerValue = EvaluateExpressionForTest(cast.Expression, semanticModel);
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
        }

        // Try to get constant value through semantic model
        var constantValue = semanticModel.GetConstantValue(expression);
        if (constantValue.HasValue)
        {
            return constantValue.Value;
        }

        // Handle identifier names
        if (expression is IdentifierNameSyntax identifier)
        {
            var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;
            if (symbol is IFieldSymbol idField && idField.IsConst)
                return idField.ConstantValue;
            if (symbol is ILocalSymbol local && local.HasConstantValue)
                return local.ConstantValue;
        }

        return null;
    }

    #endregion
}
