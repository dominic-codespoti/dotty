# Extending the Dotty Config Source Generator System

This guide is for developers who want to extend Dotty's configuration source generator system. It covers adding new configuration properties, expression evaluation, theme types, and emission outputs.

## Table of Contents

1. [Getting Started](#1-getting-started)
2. [Adding New Config Properties](#2-adding-new-config-properties)
3. [Adding New Expression Types](#3-adding-new-expression-types)
4. [Adding New Emission Outputs](#4-adding-new-emission-outputs)
5. [Theme System Extensions](#5-theme-system-extensions)
6. [Testing Your Changes](#6-testing-your-changes)
7. [Build and Debugging](#7-build-and-debugging)

---

## 1. Getting Started

### Prerequisites

Before extending the config system, ensure you have:

- **.NET 8.0+ SDK** - The source generator targets .NET Standard 2.0 but tests use .NET 8
- **Understanding of Roslyn APIs** - Familiarity with `Microsoft.CodeAnalysis`
- **Basic knowledge of Source Generators** - Understanding of `IIncrementalGenerator`

### Project Structure

The config source generator is located in:

```
src/Dotty.Config.SourceGenerator/
├── ConfigGenerator.cs              # Entry point - IIncrementalGenerator implementation
├── Diagnostics/
│   └── GeneratorDiagnostics.cs    # Diagnostic descriptors (DOTTY001-DOTTY004)
├── Emission/
│   ├── ConfigEmitter.cs           # Generates Config.g.cs
│   ├── ColorSchemeEmitter.cs      # Generates ColorScheme.g.cs
│   └── KeyBindingsEmitter.cs      # Generates KeyBindings.g.cs
├── Models/
│   ├── ConfigModel.cs             # Central configuration record
│   ├── ThemeModel.cs              # Theme representation
│   ├── CursorModel.cs             # Cursor settings
│   ├── ThicknessModel.cs          # Padding values
│   ├── WindowDimensionsModel.cs   # Window size
│   └── KeyBindingModel.cs         # Key binding definitions
└── Pipeline/
    ├── ConfigDiscovery.cs         # Selects config class from candidates
    ├── ConfigExtractor.cs         # Extracts values from syntax tree
    ├── ExpressionEvaluator.cs     # Evaluates C# expressions
    └── ThemeResolver.cs           # Resolves theme names to color schemes
```

### Key Concepts

1. **Pipeline Architecture**: The generator follows a pipeline pattern:
   - **Discovery** → Find IDottyConfig implementations
   - **Extraction** → Parse syntax trees and extract values
   - **Evaluation** → Evaluate C# expressions to constants
   - **Resolution** → Resolve themes, apply defaults
   - **Emission** → Generate C# source code

2. **Immutable Models**: All data flows through immutable record types (`ConfigModel`, `ThemeModel`, etc.)

3. **Expression Evaluation**: The `ExpressionEvaluator` handles compile-time constant folding

4. **Theme Resolution**: Themes are defined in `themes.json` and resolved by name

---

## 2. Adding New Config Properties

This section walks through adding a new configuration property from start to finish.

### Example: Adding `BellVolume` Property

Let's add a `BellVolume` property that controls the terminal bell volume (0-100).

#### Step 1: Add to IDottyConfig Interface

**File**: `src/Dotty.Abstractions/Config/IDottyConfig.cs`

**Before:**
```csharp
public interface IDottyConfig
{
    string? FontFamily { get; }
    double? FontSize { get; }
    // ... existing properties
    int? InactiveTabDestroyDelayMs { get; }
}
```

**After:**
```csharp
public interface IDottyConfig
{
    string? FontFamily { get; }
    double? FontSize { get; }
    // ... existing properties
    int? InactiveTabDestroyDelayMs { get; }
    
    /// <summary>
    /// Terminal bell volume (0-100, where 0 is silent).
    /// </summary>
    byte? BellVolume { get; }
}
```

#### Step 2: Add to ConfigModel

**File**: `src/Dotty.Config.SourceGenerator/Models/ConfigModel.cs`

**Before:**
```csharp
public record ConfigModel
{
    // Font Settings
    public string FontFamily { get; init; } = DottyDefaults.FontFamily;
    // ... existing properties
    
    // Theme
    public ThemeModel Theme { get; init; } = ThemeModel.DarkPlus;
}

internal static class DottyDefaults
{
    // ... existing defaults
    public const string DefaultThemeName = "DarkPlus";
}
```

**After:**
```csharp
public record ConfigModel
{
    // Font Settings
    public string FontFamily { get; init; } = DottyDefaults.FontFamily;
    // ... existing properties
    
    // Theme
    public ThemeModel Theme { get; init; } = ThemeModel.DarkPlus;
    
    // Audio Settings
    public byte BellVolume { get; init; } = DottyDefaults.BellVolume;
}

internal static class DottyDefaults
{
    // ... existing defaults
    public const string DefaultThemeName = "DarkPlus";
    public const byte BellVolume = 50;  // Default to 50% volume
}
```

#### Step 3: Update ConfigExtractor

**File**: `src/Dotty.Config.SourceGenerator/Pipeline/ConfigExtractor.cs`

Add extraction logic in the `Extract` method's property switch:

**Before:**
```csharp
model = propertyName switch
{
    "FontFamily" => evaluatedValue is string s ? model with { FontFamily = s } : model,
    "FontSize" => evaluatedValue switch
    {
        double d => model with { FontSize = d },
        int i => model with { FontSize = i },
        _ => model
    },
    // ... existing cases
    "InactiveTabDestroyDelayMs" => evaluatedValue is int itd
        ? model with { InactiveTabDestroyDelayMs = itd }
        : model,
    _ => model
};
```

**After:**
```csharp
model = propertyName switch
{
    "FontFamily" => evaluatedValue is string s ? model with { FontFamily = s } : model,
    "FontSize" => evaluatedValue switch
    {
        double d => model with { FontSize = d },
        int i => model with { FontSize = i },
        _ => model
    },
    // ... existing cases
    "InactiveTabDestroyDelayMs" => evaluatedValue is int itd
        ? model with { InactiveTabDestroyDelayMs = itd }
        : model,
    "BellVolume" => evaluatedValue is int bv
        ? model with { BellVolume = (byte)Clamp(bv, 0, 100) }
        : model,
    _ => model
};
```

#### Step 4: Update ConfigEmitter

**File**: `src/Dotty.Config.SourceGenerator/Emission/ConfigEmitter.cs`

Add the property to the generated Config class:

**Before:**
```csharp
// Window settings
sb.AppendLine("    #region Window Settings");
sb.AppendLine($"    public static int InitialColumns => {model.InitialDimensions.Columns};");
sb.AppendLine($"    public static int InitialRows => {model.InitialDimensions.Rows};");
// ... window properties
sb.AppendLine("    #endregion");
```

**After:**
```csharp
// Window settings
sb.AppendLine("    #region Window Settings");
sb.AppendLine($"    public static int InitialColumns => {model.InitialDimensions.Columns};");
sb.AppendLine($"    public static int InitialRows => {model.InitialDimensions.Rows};");
// ... window properties
sb.AppendLine("    #endregion");
sb.AppendLine();

// Audio settings
sb.AppendLine("    #region Audio Settings");
sb.AppendLine($"    public static byte BellVolume => {model.BellVolume};");
sb.AppendLine("    #endregion");
```

#### Step 5: Testing the Changes

**File**: `tests/Dotty.Config.SourceGenerator.Tests/ConfigExtractorTests.cs`

Add a test for the new property:

```csharp
/// <summary>
/// Verifies that BellVolume property is correctly extracted and clamped.
/// </summary>
[Theory]
[InlineData(0, 0)]     // Silent
[InlineData(50, 50)]   // Normal
[InlineData(100, 100)] // Maximum
[InlineData(-10, 0)]   // Clamped to minimum
[InlineData(150, 100)] // Clamped to maximum
public void Extract_ClampsBellVolumeToRange(int input, int expected)
{
    // Arrange
    var values = new TestHelpers.ConfigValues();

    // Act
    values.BellVolume = (byte)(input < 0 ? 0 : (input > 100 ? 100 : input));

    // Assert
    Assert.Equal((byte)expected, values.BellVolume);
}
```

Also update `TestHelpers.ConfigValues` to include the new property.

---

## 3. Adding New Expression Types

The `ExpressionEvaluator` handles various C# expression types. To add support for new syntax (e.g., ternary operators, conditional access), follow this pattern.

### How ExpressionEvaluator Works

The `Evaluate` method in `ExpressionEvaluator.cs` handles different expression types:

1. **Literals**: Direct values like `42`, `"string"`, `true`
2. **Member Access**: `BuiltInThemes.DarkPlus`, `TransparencyLevel.Blur`
3. **Object Creation**: `new Thickness(10)`, `new CursorSettings { ... }`
4. **Binary Expressions**: `5 + 3`, `10 * 2`
5. **Cast Expressions**: `(uint)42`, `(byte)255`
6. **Parenthesized**: `(1 + 2)`
7. **Prefix Unary**: `-5`, `!true`
8. **Identifiers**: References to const fields

### Example: Adding Ternary Operator Support (?:)

#### Step 1: Add Handler in ExpressionEvaluator

**File**: `src/Dotty.Config.SourceGenerator/Pipeline/ExpressionEvaluator.cs`

Add the new expression type handler in the `Evaluate` method:

**Before:**
```csharp
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
```

**After:**
```csharp
// Handle conditional/ternary expressions: condition ? trueValue : falseValue
if (expression is ConditionalExpressionSyntax conditional)
{
    return EvaluateConditional(conditional, semanticModel);
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
```

Add the evaluation method:

```csharp
/// <summary>
/// Evaluates a conditional/ternary expression.
/// </summary>
private static object? EvaluateConditional(ConditionalExpressionSyntax conditional, SemanticModel semanticModel)
{
    var condition = Evaluate(conditional.Condition, semanticModel);
    
    // Evaluate condition to boolean
    bool conditionValue = condition switch
    {
        bool b => b,
        int i => i != 0,
        _ => false
    };
    
    // Return appropriate branch
    if (conditionValue)
    {
        return Evaluate(conditional.WhenTrue, semanticModel);
    }
    else
    {
        return Evaluate(conditional.WhenFalse, semanticModel);
    }
}
```

#### Step 2: Add Tests

**File**: `tests/Dotty.Config.SourceGenerator.Tests/ExpressionEvaluatorTests.cs`

```csharp
#region Conditional Expression Tests

/// <summary>
/// Verifies that ternary expressions are evaluated correctly.
/// Tests condition ? trueValue : falseValue syntax.
/// </summary>
[Theory]
[InlineData("true ? 1 : 2", 1)]
[InlineData("false ? 1 : 2", 2)]
[InlineData("5 > 3 ? 100 : 200", 100)]
public async Task EvaluateConditional_ReturnsCorrectBranch(string expression, int expected)
{
    // Arrange
    var source = $"class Test {{ public int Value => {expression}; }}";
    var (propertyDecl, semanticModel) = await GetPropertyDeclarationAsync(source);

    // Act
    var result = EvaluateExpressionForTest(propertyDecl.ExpressionBody!.Expression, semanticModel);

    // Assert
    Assert.Equal(expected, result);
}

#endregion
```

Add the test helper method to mirror the main evaluator:

```csharp
// Handle conditional expressions in test evaluator
if (expression is ConditionalExpressionSyntax conditional)
{
    var condition = EvaluateExpressionForTest(conditional.Condition, semanticModel);
    bool conditionValue = condition switch
    {
        bool b => b,
        int i => i != 0,
        _ => false
    };
    
    if (conditionValue)
        return EvaluateExpressionForTest(conditional.WhenTrue, semanticModel);
    else
        return EvaluateExpressionForTest(conditional.WhenFalse, semanticModel);
}
```

#### Usage in User Config

Once implemented, users can write:

```csharp
public class MyConfig : IDottyConfig
{
    // Time-based theme switching
    public IColorScheme? Colors => DateTime.Now.Hour >= 6 && DateTime.Now.Hour < 18 
        ? BuiltInThemes.LightPlus 
        : BuiltInThemes.DarkPlus;
    
    // Conditional opacity
    public byte? WindowOpacity => IsHighContrastMode ? 100 : 85;
    
    private bool IsHighContrastMode => false; // Would come from environment
}
```

---

## 4. Adding New Emission Outputs

Sometimes you need to generate additional source files beyond the three default ones (Config, ColorScheme, KeyBindings).

### When to Create a New Emitter

Create a new emitter when:
- You need a separate generated class for a distinct feature area
- The generated code is logically independent from existing files
- You want to allow users to optionally include/exclude certain generated code
- The output is large enough to warrant separation

### Pattern for Creating Emitters

#### Step 1: Create the Emitter Class

**File**: `src/Dotty.Config.SourceGenerator/Emission/SettingsEmitter.cs`

```csharp
using System.Text;
using Dotty.Config.SourceGenerator.Models;

namespace Dotty.Config.SourceGenerator.Emission;

/// <summary>
/// Generates the Settings.g.cs source file for advanced terminal settings.
/// </summary>
public static class SettingsEmitter
{
    /// <summary>
    /// Generates the Settings class source code.
    /// </summary>
    public static string Generate(ConfigModel model)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by Dotty.Config.SourceGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Auto-generated advanced settings for Dotty terminal.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class Settings");
        sb.AppendLine("{");
        
        // Audio settings
        sb.AppendLine("    #region Audio Settings");
        sb.AppendLine($"    public static byte BellVolume => {model.BellVolume};");
        sb.AppendLine($"    public static bool BellEnabled => {model.BellVolume > 0};");
        sb.AppendLine("    #endregion");
        sb.AppendLine();
        
        // Performance settings
        sb.AppendLine("    #region Performance Settings");
        sb.AppendLine($"    public static int MaxFrameRate => 60;");
        sb.AppendLine($"    public static bool EnableGpuAcceleration => true;");
        sb.AppendLine("    #endregion");
        
        sb.AppendLine("}");

        return sb.ToString();
    }
}
```

#### Step 2: Register in ConfigGenerator

**File**: `src/Dotty.Config.SourceGenerator/ConfigGenerator.cs`

**Before:**
```csharp
// Generate the three source files
var configSource = ConfigEmitter.Generate(configModel);
var colorSchemeSource = ColorSchemeEmitter.Generate(configModel.Theme);
var keyBindingsSource = KeyBindingsEmitter.Generate();

// Add the generated sources
context.AddSource("Dotty.Generated.Config.g.cs", configSource);
context.AddSource("Dotty.Generated.ColorScheme.g.cs", colorSchemeSource);
context.AddSource("Dotty.Generated.KeyBindings.g.cs", keyBindingsSource);
```

**After:**
```csharp
// Generate the four source files
var configSource = ConfigEmitter.Generate(configModel);
var colorSchemeSource = ColorSchemeEmitter.Generate(configModel.Theme);
var keyBindingsSource = KeyBindingsEmitter.Generate();
var settingsSource = SettingsEmitter.Generate(configModel);

// Add the generated sources
context.AddSource("Dotty.Generated.Config.g.cs", configSource);
context.AddSource("Dotty.Generated.ColorScheme.g.cs", colorSchemeSource);
context.AddSource("Dotty.Generated.KeyBindings.g.cs", keyBindingsSource);
context.AddSource("Dotty.Generated.Settings.g.cs", settingsSource);
```

#### Step 3: Add Integration Tests

**File**: `tests/Dotty.Config.SourceGenerator.Tests/EmitterTests.cs` (create if doesn't exist)

```csharp
using Xunit;
using FluentAssertions;
using Dotty.Config.SourceGenerator.Models;
using Dotty.Config.SourceGenerator.Emission;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Tests for emitter classes.
/// </summary>
public class EmitterTests
{
    [Fact]
    public void SettingsEmitter_GeneratesValidSettings()
    {
        // Arrange
        var model = new ConfigModel { BellVolume = 75 };

        // Act
        var generated = SettingsEmitter.Generate(model);

        // Assert
        generated.Should().Contain("class Settings");
        generated.Should().Contain("BellVolume => 75");
        generated.Should().Contain("BellEnabled => True");
        generated.Should().Contain("#nullable enable");
    }
}
```

---

## 5. Theme System Extensions

### How Theme Resolution Works

The theme system uses a multi-layer resolution strategy:

1. **Embedded JSON Resource**: `themes.json` is embedded as a manifest resource
2. **Lazy Loading**: Themes are loaded once and cached in a static dictionary
3. **Multiple Lookup Strategies**:
   - Direct name match (case-insensitive)
   - Normalized name (lowercase, no spaces, no hyphens)
   - Alias lookup
   - Theme name without "Theme" suffix
4. **Fallback**: Unknown themes fall back to DarkPlus

### Adding New Built-in Themes

#### Step 1: Add Theme to themes.json

**File**: `src/Dotty.Abstractions/Themes/themes.json`

Add a new theme entry to the `themes` array:

```json
{
  "version": 1,
  "themes": [
    // ... existing themes
    {
      "canonicalName": "Nord",
      "displayName": "Nord",
      "description": "Nord theme - arctic-inspired color palette with cool blue tones.",
      "isDark": true,
      "aliases": ["nord-dark", "nord-theme", "arctic"],
      "colors": {
        "background": "#2E3440",
        "foreground": "#D8DEE9",
        "opacity": 1.0,
        "ansi": [
          "#3B4252",
          "#BF616A",
          "#A3BE8C",
          "#EBCB8B",
          "#81A1C1",
          "#B48EAD",
          "#88C0D0",
          "#E5E9F0",
          "#4C566A",
          "#BF616A",
          "#A3BE8C",
          "#EBCB8B",
          "#81A1C1",
          "#B48EAD",
          "#8FBCBB",
          "#ECEFF4"
        ]
      }
    }
  ]
}
```

#### Step 2: Add Fallback Theme (Optional but Recommended)

**File**: `src/Dotty.Config.SourceGenerator/Pipeline/ThemeResolver.cs`

In case JSON loading fails, add a fallback:

```csharp
private static void AddFallbackThemes(Dictionary<string, ThemeModel> themes)
{
    // ... existing fallback themes
    
    // Nord fallback
    var nord = new ThemeModel
    {
        CanonicalName = "Nord",
        Background = 0xFF2E3440,
        Foreground = 0xFFD8DEE9,
        Opacity = 100,
        AnsiColors = new uint[]
        {
            0xFF3B4252, 0xFFBF616A, 0xFFA3BE8C, 0xFFEBCB8B,
            0xFF81A1C1, 0xFFB48EAD, 0xFF88C0D0, 0xFFE5E9F0,
            0xFF4C566A, 0xFFBF616A, 0xFFA3BE8C, 0xFFEBCB8B,
            0xFF81A1C1, 0xFFB48EAD, 0xFF8FBCBB, 0xFFECEFF4
        }
    };
    themes["Nord"] = nord;
    themes["nord"] = nord;
    themes["nord-dark"] = nord;
}
```

#### Step 3: Add Tests

**File**: `tests/Dotty.Config.SourceGenerator.Tests/ThemeResolverTests.cs` (or TestHelpers.cs)

```csharp
[Theory]
[InlineData("Nord", 0xFF2E3440)]
[InlineData("nord", 0xFF2E3440)]
[InlineData("nord-dark", 0xFF2E3440)]
public void Resolve_NordTheme_ReturnsCorrectColors(string themeName, uint expectedBackground)
{
    // Act
    var theme = ThemeResolver.Resolve(themeName);

    // Assert
    Assert.Equal(expectedBackground, theme.Background);
    Assert.Equal("Nord", theme.CanonicalName);
}
```

Update `TestHelpers.cs` to add Nord colors:

```csharp
private static void SetNordColors(ConfigValues values)
{
    values.Background = 0xFF2E3440;
    values.Foreground = 0xFFD8DEE9;
    values.AnsiColors[0] = 0xFF3B4252;
    values.AnsiColors[1] = 0xFFBF616A;
    // ... rest of ANSI colors
}
```

### Adding Theme Aliases

Aliases allow users to reference themes by alternative names. Add aliases to the `aliases` array in `themes.json`:

```json
{
  "canonicalName": "CatppuccinMocha",
  "displayName": "Catppuccin Mocha",
  "aliases": [
    "catppuccin-mocha",
    "catppuccinmocha",
    "mocha",
    "cat-mocha",        // New alias
    "ctp-mocha"         // New alias
  ],
  // ...
}
```

The `ThemeResolver` automatically picks up aliases when loading from JSON.

---

## 6. Testing Your Changes

### Unit Test Patterns

Unit tests focus on individual components in isolation.

#### Testing Expression Evaluation

```csharp
[Fact]
public async Task Evaluate_AdditionExpression_ReturnsSum()
{
    // Arrange
    var source = "class Test { public int Value => 5 + 3; }";
    var (prop, model) = await GetPropertyDeclarationAsync(source);

    // Act
    var result = ExpressionEvaluator.Evaluate(prop.ExpressionBody.Expression, model);

    // Assert
    Assert.Equal(8, result);
}
```

#### Testing Model Creation

```csharp
[Fact]
public void ConfigModel_WithBellVolume_CreatesCorrectly()
{
    // Arrange & Act
    var model = new ConfigModel { BellVolume = 75 };

    // Assert
    Assert.Equal(75, model.BellVolume);
}
```

### Integration Test Patterns

Integration tests verify the full generator pipeline with actual compilation.

#### Basic Integration Test

```csharp
[Fact]
public async Task Generator_WithBellVolume_GeneratesCorrectCode()
{
    // Arrange
    const string source = @"
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;

public class TestConfig : IDottyConfig
{
    public IColorScheme? Colors => BuiltInThemes.DarkPlus;
    public byte? BellVolume => 75;
}
";

    // Act
    var (compilation, generatedSources) = await RunGeneratorAsync(source);

    // Assert
    var settingsSource = generatedSources.FirstOrDefault(s => s.HintName.Contains("Settings.g.cs"));
    Assert.NotNull(settingsSource);
    
    var code = settingsSource.SourceText.ToString();
    Assert.Contains("BellVolume => 75", code);
}
```

#### Test Helpers

The test project provides several helpers in `TestHelpers.cs`:

- `CreateTestCompilation(source)` - Creates a compilation with Dotty.Abstractions referenced
- `TestHelpers.SetColorSchemeByName(name, values)` - Sets theme colors for testing
- `TestHelpers.ConfigValues` - Holds all config values with defaults
- `TestHelpers.Thickness` - Mirrors the Thickness struct

### Testing Checklist

Before submitting changes:

- [ ] Unit tests for new expression evaluation logic
- [ ] Unit tests for new model properties
- [ ] Integration tests for full pipeline
- [ ] Tests for default values
- [ ] Tests for boundary conditions (clamping, nulls)
- [ ] Tests for error cases (invalid themes, unsupported expressions)

---

## 7. Build and Debugging

### How to Debug Source Generators

Debugging source generators requires attaching to the compiler process. Here's the recommended approach:

#### Method 1: Using Debug.Launch()

Add this at the start of your generator:

```csharp
#if DEBUG
if (!System.Diagnostics.Debugger.IsAttached)
{
    System.Diagnostics.Debugger.Launch();
}
#endif
```

#### Method 2: Using launchSettings.json

**File**: `src/Dotty.Config.SourceGenerator/.vscode/launchSettings.json`

```json
{
  "profiles": {
    "Debug Generator": {
      "commandName": "DebugRoslynComponent",
      "targetProject": "../Dotty.App/Dotty.App.csproj"
    }
  }
}
```

In VS Code, press F5 and select "DebugRoslynComponent" profile.

#### Method 3: Unit Test Debugging

The easiest method is debugging through unit tests:

1. Add a breakpoint in your generator code
2. Run the test in debug mode: `dotnet test --filter "FullyQualifiedName~MyTest" -v n`
3. Or use VS Code's test explorer

### Viewing Generated Code

Generated code is written to the obj folder during build:

```bash
# After building, look in the obj folder
find /home/dom/projects/dotnet-term/obj -name "*.g.cs" -o -name "*Generated*"

# Specific pattern for Dotty
ls -la /home/dom/projects/dotnet-term/obj/Debug/net8.0/generated/
```

For source generator tests, use the `GeneratorDriver` to inspect output:

```csharp
var runResult = driver.GetRunResult();
foreach (var result in runResult.Results)
{
    foreach (var source in result.GeneratedSources)
    {
        Console.WriteLine($"=== {source.HintName} ===");
        Console.WriteLine(source.SourceText);
    }
}
```

### Common Development Pitfalls

#### Pitfall 1: Stale Generated Code

**Problem**: After making changes, the generator doesn't seem to update.

**Solution**: 
```bash
dotnet clean
rm -rf obj/
dotnet build
```

#### Pitfall 2: Embedded Resource Not Found

**Problem**: `themes.json` can't be loaded.

**Solution**: Check the `.csproj` file:

```xml
<ItemGroup>
  <EmbeddedResource Include="../Dotty.Abstractions/Themes/themes.json">
    <LogicalName>Dotty.Config.SourceGenerator.themes.json</LogicalName>
  </EmbeddedResource>
</ItemGroup>
```

#### Pitfall 3: Null Reference in Symbol Analysis

**Problem**: `GetSymbolInfo()` returns null.

**Solution**: Always check for null and handle gracefully:

```csharp
var symbolInfo = semanticModel.GetSymbolInfo(expression);
if (symbolInfo.Symbol is IPropertySymbol property)
{
    // Use property
}
// Don't crash on null - return null or default
```

#### Pitfall 4: Non-Deterministic Output

**Problem**: Generated code differs between builds.

**Solution**: 
- Use `OrderBy()` when iterating collections
- Avoid `HashSet` iteration order dependencies
- Use deterministic random seeds if needed

#### Pitfall 5: Memory Leaks in Tests

**Problem**: Tests slow down over time or run out of memory.

**Solution**: 
- Use `Compilation` instances sparingly
- Dispose of `Stream` objects properly
- Clear static caches between test runs if applicable

### Performance Tips

1. **Use Incremental Generation**: The generator already implements `IIncrementalGenerator` - keep it that way for better IDE performance

2. **Cache Heavy Computations**: Theme resolution uses `Lazy<T>`:
   ```csharp
   private static readonly Lazy<Dictionary<string, ThemeModel>> _themes = new(LoadThemes, true);
   ```

3. **Avoid String Interpolation in Hot Paths**: Use `StringBuilder` for generating large code files

4. **Minimize Semantic Model Queries**: Batch symbol lookups when possible

---

## Summary

This guide covered:

1. **Architecture**: Pipeline pattern with discovery → extraction → evaluation → emission
2. **Adding Properties**: Interface → Model → Extractor → Emitter → Tests
3. **Expression Types**: Add handler → Add evaluation method → Add tests
4. **New Emitters**: Create emitter class → Register in ConfigGenerator → Add tests
5. **Themes**: Add to JSON → Add fallback → Add tests
6. **Testing**: Unit and integration test patterns with test helpers
7. **Debugging**: Multiple debugging strategies and common pitfalls

### Quick Reference: Files to Modify

| Task | Files to Modify |
|------|----------------|
| Add config property | `IDottyConfig.cs`, `ConfigModel.cs`, `ConfigExtractor.cs`, `ConfigEmitter.cs`, `TestHelpers.cs` |
| Add expression support | `ExpressionEvaluator.cs`, `ExpressionEvaluatorTests.cs` |
| Add new emitter | `NewEmitter.cs`, `ConfigGenerator.cs`, `EmitterTests.cs` |
| Add theme | `themes.json`, `ThemeResolver.cs` (fallback), `TestHelpers.cs` |
| Add diagnostic | `GeneratorDiagnostics.cs`, usage in appropriate pipeline file |

### Getting Help

- Check existing tests for patterns: `tests/Dotty.Config.SourceGenerator.Tests/`
- Look at samples: `samples/Config.cs`
- Review the theme definitions: `src/Dotty.Abstractions/Themes/themes.json`
