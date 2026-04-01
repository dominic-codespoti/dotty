using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

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
                transform: static (ctx, _) => GetConfigClass(ctx))
            .Where(static c => c is not null)
            .Collect();

        // Combine with compilation
        var compilationAndConfigs = context.CompilationProvider.Combine(configClasses);

        // Register the source output
        context.RegisterSourceOutput(compilationAndConfigs, static (ctx, source) =>
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

    private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<ClassDeclarationSyntax?> configClasses)
    {
        var configValues = new ConfigValues();
        var keyBindings = new List<KeyBinding>();

        // Filter out null entries and order to prefer user configs over DefaultConfig
        var validConfigs = configClasses
            .Where(c => c != null)
            .OrderBy(c => 
            {
                // Prefer user configs (not named DefaultConfig) over built-in defaults
                var name = c!.Identifier.ValueText;
                if (name == "DefaultConfig") return 1; // Put DefaultConfig last
                if (name.Contains("Config")) return 0; // User configs first
                return 0;
            })
            .ToList();

        // Use the first (highest priority) config class
        var configClass = validConfigs.FirstOrDefault();
        if (configClass != null)
        {
            var semanticModel = compilation.GetSemanticModel(configClass.SyntaxTree);
            var classSymbol = semanticModel.GetDeclaredSymbol(configClass);

            if (classSymbol != null)
            {
                ExtractConfigValues(classSymbol, configValues, keyBindings);
            }
        }

        // Generate the Config class
        var configSource = GenerateConfigClass(configValues, keyBindings);
        context.AddSource("Dotty.Generated.Config.g.cs", configSource);

        // Generate the ColorScheme record
        var colorSchemeSource = GenerateColorSchemeRecord(configValues);
        context.AddSource("Dotty.Generated.ColorScheme.g.cs", colorSchemeSource);

        // Generate the KeyBinding helpers
        var keyBindingSource = GenerateKeyBindingHelpers(configValues, keyBindings);
        context.AddSource("Dotty.Generated.KeyBindings.g.cs", keyBindingSource);
    }

    private static void ExtractConfigValues(INamedTypeSymbol classSymbol, ConfigValues values, List<KeyBinding> keyBindings)
    {
        // Extract property values from the config class
        foreach (var member in classSymbol.GetMembers())
        {
            if (member is IPropertySymbol property)
            {
                var propertyName = property.Name;
                var constantValue = GetConstantValue(property);

                switch (propertyName)
                {
                    case "FontFamily":
                        values.FontFamily = constantValue as string ?? values.FontFamily;
                        break;
                    case "FontSize":
                        if (constantValue is double d) values.FontSize = d;
                        else if (constantValue is int i) values.FontSize = i;
                        break;
                    case "CellPadding":
                        if (constantValue is double cp) values.CellPadding = cp;
                        else if (constantValue is int cpi) values.CellPadding = cpi;
                        break;
                    case "ContentPadding":
                        // Handle Thickness - simplified for now
                        break;
                    case "ScrollbackLines":
                        if (constantValue is int sl) values.ScrollbackLines = sl;
                        break;
                    case "SelectionColor":
                        if (constantValue is uint sc) values.SelectionColor = sc;
                        break;
                    case "TabBarBackgroundColor":
                        if (constantValue is uint tb) values.TabBarBackgroundColor = tb;
                        break;
                    case "InactiveTabDestroyDelayMs":
                        if (constantValue is int itd) values.InactiveTabDestroyDelayMs = itd;
                        break;
                }
            }
        }

        // Look for Colors property
        var colorsProperty = classSymbol.GetMembers("Colors").OfType<IPropertySymbol>().FirstOrDefault();
        if (colorsProperty != null)
        {
            ExtractColorScheme(colorsProperty.Type, values);
        }

        // Look for KeyBindings property
        var keyBindingsProperty = classSymbol.GetMembers("KeyBindings").OfType<IPropertySymbol>().FirstOrDefault();
        if (keyBindingsProperty != null)
        {
            ExtractKeyBindings(keyBindingsProperty.Type, keyBindings);
        }

        // Look for Cursor property
        var cursorProperty = classSymbol.GetMembers("Cursor").OfType<IPropertySymbol>().FirstOrDefault();
        if (cursorProperty != null)
        {
            ExtractCursorSettings(cursorProperty.Type, values);
        }

        // Look for InitialDimensions property
        var dimensionsProperty = classSymbol.GetMembers("InitialDimensions").OfType<IPropertySymbol>().FirstOrDefault();
        if (dimensionsProperty != null)
        {
            ExtractWindowDimensions(dimensionsProperty.Type, values);
        }
    }

    private static object? GetConstantValue(IPropertySymbol property)
    {
        // Try to get the constant value from the property
        if (property.IsReadOnly && property.GetMethod != null)
        {
            // For expression-bodied properties, we'd need more complex analysis
            // For now, return null and rely on defaults
        }
        return null;
    }

    private static void ExtractColorScheme(ITypeSymbol colorsType, ConfigValues values)
    {
        if (colorsType is not INamedTypeSymbol namedType) return;

        // Check if this is a built-in theme by name and use hardcoded values
        var themeName = namedType.Name;
        switch (themeName)
        {
            case "CatppuccinLatteTheme":
                SetCatppuccinLatteColors(values);
                return;
            case "DarkPlusTheme":
                SetDarkPlusColors(values);
                return;
            case "DraculaTheme":
                SetDraculaColors(values);
                return;
            case "OneDarkTheme":
                SetOneDarkColors(values);
                return;
            case "GruvboxDarkTheme":
                SetGruvboxDarkColors(values);
                return;
            case "GruvboxLightTheme":
                SetGruvboxLightColors(values);
                return;
            case "CatppuccinMochaTheme":
                SetCatppuccinMochaColors(values);
                return;
            case "TokyoNightTheme":
                SetTokyoNightColors(values);
                return;
            case "LightPlusTheme":
                SetLightPlusColors(values);
                return;
            case "OneLightTheme":
                SetOneLightColors(values);
                return;
            case "SolarizedLightTheme":
                SetSolarizedLightColors(values);
                return;
        }

        // For custom themes, try to extract from the type's properties
        foreach (var member in namedType.GetMembers())
        {
            if (member is IPropertySymbol property)
            {
                var val = GetConstantValue(property);
                if (val is uint u)
                {
                    switch (property.Name)
                    {
                        case "Background": values.Background = u; break;
                        case "Foreground": values.Foreground = u; break;
                        case "AnsiBlack": values.AnsiColors[0] = u; break;
                        case "AnsiRed": values.AnsiColors[1] = u; break;
                        case "AnsiGreen": values.AnsiColors[2] = u; break;
                        case "AnsiYellow": values.AnsiColors[3] = u; break;
                        case "AnsiBlue": values.AnsiColors[4] = u; break;
                        case "AnsiMagenta": values.AnsiColors[5] = u; break;
                        case "AnsiCyan": values.AnsiColors[6] = u; break;
                        case "AnsiWhite": values.AnsiColors[7] = u; break;
                        case "AnsiBrightBlack": values.AnsiColors[8] = u; break;
                        case "AnsiBrightRed": values.AnsiColors[9] = u; break;
                        case "AnsiBrightGreen": values.AnsiColors[10] = u; break;
                        case "AnsiBrightYellow": values.AnsiColors[11] = u; break;
                        case "AnsiBrightBlue": values.AnsiColors[12] = u; break;
                        case "AnsiBrightMagenta": values.AnsiColors[13] = u; break;
                        case "AnsiBrightCyan": values.AnsiColors[14] = u; break;
                        case "AnsiBrightWhite": values.AnsiColors[15] = u; break;
                    }
                }
                else if (val is int i && property.Name == "Opacity")
                {
                    values.Opacity = (byte)(i < 0 ? 0 : (i > 100 ? 100 : i));
                }
            }
        }
    }

    private static void SetCatppuccinLatteColors(ConfigValues values)
    {
        values.Background = 0xFFEff1F5;
        values.Foreground = 0xFF4C4F69;
        values.AnsiColors[0] = 0xFF5C5F77;   // Black
        values.AnsiColors[1] = 0xFFD20F39;   // Red
        values.AnsiColors[2] = 0xFF40A02B;   // Green
        values.AnsiColors[3] = 0xFFDF8E1D;   // Yellow
        values.AnsiColors[4] = 0xFF1E66F5;   // Blue
        values.AnsiColors[5] = 0xFF8839EF;   // Magenta
        values.AnsiColors[6] = 0xFF179299;   // Cyan
        values.AnsiColors[7] = 0xFFACB0BE;   // White
        values.AnsiColors[8] = 0xFF6C6F85;   // Bright Black
        values.AnsiColors[9] = 0xFFD20F39;   // Bright Red
        values.AnsiColors[10] = 0xFF40A02B;  // Bright Green
        values.AnsiColors[11] = 0xFFDF8E1D; // Bright Yellow
        values.AnsiColors[12] = 0xFF1E66F5;  // Bright Blue
        values.AnsiColors[13] = 0xFF8839EF;  // Bright Magenta
        values.AnsiColors[14] = 0xFF179299;   // Bright Cyan
        values.AnsiColors[15] = 0xFFBCC0CC;   // Bright White
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

    private static void ExtractKeyBindings(ITypeSymbol keyBindingsType, List<KeyBinding> bindings)
    {
        // Key bindings extraction would require analyzing the GetAction method
        // For now, we'll use default bindings
    }

    private static void ExtractCursorSettings(ITypeSymbol cursorType, ConfigValues values)
    {
        if (cursorType is not INamedTypeSymbol namedType) return;

        foreach (var member in namedType.GetMembers())
        {
            if (member is IPropertySymbol property)
            {
                var val = GetConstantValue(property);
                switch (property.Name)
                {
                    case "Shape":
                        if (val?.ToString() == "Beam") values.CursorShape = "Beam";
                        else if (val?.ToString() == "Underline") values.CursorShape = "Underline";
                        else values.CursorShape = "Block";
                        break;
                    case "Blink":
                        values.CursorBlink = val is true;
                        break;
                    case "BlinkIntervalMs":
                        if (val is int bi) values.CursorBlinkIntervalMs = bi;
                        break;
                    case "Color":
                        if (val is uint cc) values.CursorColor = cc;
                        break;
                    case "ShowUnfocused":
                        values.CursorShowUnfocused = val is true;
                        break;
                }
            }
        }
    }

    private static void ExtractWindowDimensions(ITypeSymbol dimensionsType, ConfigValues values)
    {
        if (dimensionsType is not INamedTypeSymbol namedType) return;

        foreach (var member in namedType.GetMembers())
        {
            if (member is IPropertySymbol property)
            {
                var val = GetConstantValue(property);
                switch (property.Name)
                {
                    case "Columns":
                        if (val is int c) values.InitialColumns = c;
                        break;
                    case "Rows":
                        if (val is int r) values.InitialRows = r;
                        break;
                    case "Title":
                        values.WindowTitle = val?.ToString() ?? values.WindowTitle;
                        break;
                    case "StartFullscreen":
                        values.StartFullscreen = val is true;
                        break;
                }
            }
        }
    }

    private static string GenerateConfigClass(ConfigValues values, List<KeyBinding> keyBindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by Dotty.Config.SourceGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Auto-generated configuration values for Dotty terminal emulator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class Config");
        sb.AppendLine("{");

        // Font settings
        sb.AppendLine("    #region Font Settings");
        sb.AppendLine($"    public static string FontFamily => \"{EscapeString(values.FontFamily)}\";");
        sb.AppendLine($"    public static double FontSize => {values.FontSize};");
        sb.AppendLine($"    public static double CellPadding => {values.CellPadding};");
        sb.AppendLine($"    public static double ContentPaddingLeft => {values.ContentPaddingLeft};");
        sb.AppendLine($"    public static double ContentPaddingTop => {values.ContentPaddingTop};");
        sb.AppendLine($"    public static double ContentPaddingRight => {values.ContentPaddingRight};");
        sb.AppendLine($"    public static double ContentPaddingBottom => {values.ContentPaddingBottom};");
        sb.AppendLine("    #endregion");
        sb.AppendLine();

        // Color settings
        sb.AppendLine("    #region Color Settings");
        sb.AppendLine($"    public static uint Background => 0x{values.Background:X8};");
        sb.AppendLine($"    public static uint Foreground => 0x{values.Foreground:X8};");
        sb.AppendLine($"    public static uint SelectionColor => 0x{values.SelectionColor:X8};");
        sb.AppendLine($"    public static uint TabBarBackgroundColor => 0x{values.TabBarBackgroundColor:X8};");
        sb.AppendLine("    #endregion");
        sb.AppendLine();

        // Terminal settings
        sb.AppendLine("    #region Terminal Settings");
        sb.AppendLine($"    public static int ScrollbackLines => {values.ScrollbackLines};");
        sb.AppendLine($"    public static int InactiveTabDestroyDelayMs => {values.InactiveTabDestroyDelayMs};");
        sb.AppendLine("    #endregion");
        sb.AppendLine();

        // Window settings
        sb.AppendLine("    #region Window Settings");
        sb.AppendLine($"    public static int InitialColumns => {values.InitialColumns};");
        sb.AppendLine($"    public static int InitialRows => {values.InitialRows};");
        sb.AppendLine($"    public static string WindowTitle => \"{EscapeString(values.WindowTitle)}\";");
        sb.AppendLine($"    public static bool StartFullscreen => {values.StartFullscreen.ToString().ToLowerInvariant()};");
        sb.AppendLine($"    public static byte Opacity => {values.Opacity};");
        sb.AppendLine("    #endregion");
        sb.AppendLine();

        // Cursor settings
        sb.AppendLine("    #region Cursor Settings");
        sb.AppendLine($"    public static string CursorShape => \"{values.CursorShape}\";");
        sb.AppendLine($"    public static bool CursorBlink => {values.CursorBlink.ToString().ToLowerInvariant()};");
        sb.AppendLine($"    public static int CursorBlinkIntervalMs => {values.CursorBlinkIntervalMs};");
        sb.AppendLine($"    public static uint? CursorColor => {(values.CursorColor.HasValue ? $"0x{values.CursorColor.Value:X8}U" : "null")};");
        sb.AppendLine($"    public static bool CursorShowUnfocused => {values.CursorShowUnfocused.ToString().ToLowerInvariant()};");
        sb.AppendLine("    #endregion");
        sb.AppendLine();

        // Colors accessor
        sb.AppendLine("    #region Color Scheme");
        sb.AppendLine("    public static ColorScheme Colors => ColorScheme.Default;");
        sb.AppendLine("    #endregion");
        sb.AppendLine();

        // Key bindings
        sb.AppendLine("    #region Key Bindings");
        sb.AppendLine("    public static TerminalAction? GetActionForKey(global::Avalonia.Input.Key key, global::Avalonia.Input.KeyModifiers modifiers)");
        sb.AppendLine("    {");
        sb.AppendLine("        return (key, modifiers) switch");
        sb.AppendLine("        {");
        sb.AppendLine("            // Tab Management");
        sb.AppendLine("            (global::Avalonia.Input.Key.T, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.NewTab,");
        sb.AppendLine("            (global::Avalonia.Input.Key.W, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.CloseTab,");
        sb.AppendLine("            (global::Avalonia.Input.Key.Tab, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.NextTab,");
        sb.AppendLine("            (global::Avalonia.Input.Key.Tab, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.PreviousTab,");
        sb.AppendLine();
        sb.AppendLine("            // Copy/Paste");
        sb.AppendLine("            (global::Avalonia.Input.Key.C, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.Copy,");
        sb.AppendLine("            (global::Avalonia.Input.Key.V, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.Paste,");
        sb.AppendLine();
        sb.AppendLine("            // Zoom");
        sb.AppendLine("            (global::Avalonia.Input.Key.OemPlus, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.ZoomIn,");
        sb.AppendLine("            (global::Avalonia.Input.Key.OemMinus, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.ZoomOut,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D0, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.ResetZoom,");
        sb.AppendLine();
        sb.AppendLine("            // Tab Switching (Ctrl+1-9)");
        sb.AppendLine("            (global::Avalonia.Input.Key.D1, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab1,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D2, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab2,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D3, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab3,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D4, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab4,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D5, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab5,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D6, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab6,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D7, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab7,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D8, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab8,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D9, global::Avalonia.Input.KeyModifiers.Control) => TerminalAction.SwitchTab9,");
        sb.AppendLine();
        sb.AppendLine("            // Other Actions");
        sb.AppendLine("            (global::Avalonia.Input.Key.K, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.Clear,");
        sb.AppendLine("            (global::Avalonia.Input.Key.F11, global::Avalonia.Input.KeyModifiers.None) => TerminalAction.ToggleFullscreen,");
        sb.AppendLine("            (global::Avalonia.Input.Key.F, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.Search,");
        sb.AppendLine("            (global::Avalonia.Input.Key.D, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.DuplicateTab,");
        sb.AppendLine("            (global::Avalonia.Input.Key.Q, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift) => TerminalAction.Quit,");
        sb.AppendLine();
        sb.AppendLine("            _ => null");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("    #endregion");

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateColorSchemeRecord(ConfigValues values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by Dotty.Config.SourceGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Auto-generated color scheme for Dotty terminal emulator.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public readonly record struct ColorScheme");
        sb.AppendLine("(");
        sb.AppendLine("    uint Background,");
        sb.AppendLine("    uint Foreground,");
        sb.AppendLine("    uint[] AnsiColors");
        sb.AppendLine(")");
        sb.AppendLine("{");
        sb.AppendLine("    public static ColorScheme Default => new(");
        sb.AppendLine($"        0x{values.Background:X8},");
        sb.AppendLine($"        0x{values.Foreground:X8},");
        sb.AppendLine("        new uint[]");
        sb.AppendLine("        {");
        for (int i = 0; i < 16; i++)
        {
            sb.AppendLine($"            0x{values.AnsiColors[i]:X8}, // {GetAnsiColorName(i)}");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    );");
        sb.AppendLine();
        sb.AppendLine("    public uint AnsiBlack => AnsiColors[0];");
        sb.AppendLine("    public uint AnsiRed => AnsiColors[1];");
        sb.AppendLine("    public uint AnsiGreen => AnsiColors[2];");
        sb.AppendLine("    public uint AnsiYellow => AnsiColors[3];");
        sb.AppendLine("    public uint AnsiBlue => AnsiColors[4];");
        sb.AppendLine("    public uint AnsiMagenta => AnsiColors[5];");
        sb.AppendLine("    public uint AnsiCyan => AnsiColors[6];");
        sb.AppendLine("    public uint AnsiWhite => AnsiColors[7];");
        sb.AppendLine("    public uint AnsiBrightBlack => AnsiColors[8];");
        sb.AppendLine("    public uint AnsiBrightRed => AnsiColors[9];");
        sb.AppendLine("    public uint AnsiBrightGreen => AnsiColors[10];");
        sb.AppendLine("    public uint AnsiBrightYellow => AnsiColors[11];");
        sb.AppendLine("    public uint AnsiBrightBlue => AnsiColors[12];");
        sb.AppendLine("    public uint AnsiBrightMagenta => AnsiColors[13];");
        sb.AppendLine("    public uint AnsiBrightCyan => AnsiColors[14];");
        sb.AppendLine("    public uint AnsiBrightWhite => AnsiColors[15];");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateKeyBindingHelpers(ConfigValues values, List<KeyBinding> keyBindings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Generated by Dotty.Config.SourceGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Dotty.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Terminal actions available for key binding.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public enum TerminalAction");
        sb.AppendLine("{");
        sb.AppendLine("    None,");
        sb.AppendLine("    NewTab,");
        sb.AppendLine("    CloseTab,");
        sb.AppendLine("    NextTab,");
        sb.AppendLine("    PreviousTab,");
        sb.AppendLine("    SwitchTab1,");
        sb.AppendLine("    SwitchTab2,");
        sb.AppendLine("    SwitchTab3,");
        sb.AppendLine("    SwitchTab4,");
        sb.AppendLine("    SwitchTab5,");
        sb.AppendLine("    SwitchTab6,");
        sb.AppendLine("    SwitchTab7,");
        sb.AppendLine("    SwitchTab8,");
        sb.AppendLine("    SwitchTab9,");
        sb.AppendLine("    Copy,");
        sb.AppendLine("    Paste,");
        sb.AppendLine("    Clear,");
        sb.AppendLine("    ToggleFullscreen,");
        sb.AppendLine("    ZoomIn,");
        sb.AppendLine("    ZoomOut,");
        sb.AppendLine("    ResetZoom,");
        sb.AppendLine("    Search,");
        sb.AppendLine("    DuplicateTab,");
        sb.AppendLine("    CloseOtherTabs,");
        sb.AppendLine("    RenameTab,");
        sb.AppendLine("    ToggleVisibility,");
        sb.AppendLine("    IncreaseScrollback,");
        sb.AppendLine("    DecreaseScrollback,");
        sb.AppendLine("    SendEscapeSequence,");
        sb.AppendLine("    Quit,");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string GetAnsiColorName(int index)
    {
        return index switch
        {
            0 => "Black",
            1 => "Red",
            2 => "Green",
            3 => "Yellow",
            4 => "Blue",
            5 => "Magenta",
            6 => "Cyan",
            7 => "White",
            8 => "Bright Black",
            9 => "Bright Red",
            10 => "Bright Green",
            11 => "Bright Yellow",
            12 => "Bright Blue",
            13 => "Bright Magenta",
            14 => "Bright Cyan",
            15 => "Bright White",
            _ => $"Color {index}"
        };
    }
}

/// <summary>
/// Holds all configuration values with defaults matching the current hardcoded values.
/// </summary>
internal class ConfigValues
{
    // Font settings - defaults from Services.Defaults
    public string FontFamily { get; set; } = "JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, JetBrains Mono, SpaceMono Nerd Font Mono, SpaceMono Nerd Font, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols, Cascadia Code, Liberation Mono, Noto Sans Mono, monospace";
    public double FontSize { get; set; } = 15.0;
    public double CellPadding { get; set; } = 1.5;
    public double ContentPaddingLeft { get; set; } = 0.0;
    public double ContentPaddingTop { get; set; } = 0.0;
    public double ContentPaddingRight { get; set; } = 0.0;
    public double ContentPaddingBottom { get; set; } = 0.0;

    // Color settings - defaults from Services.Defaults (#F2000000 = ARGB with transparency)
    public uint Background { get; set; } = 0xF2000000;  // Near-black with alpha
    public uint Foreground { get; set; } = 0xFFD4D4D4;  // Light gray #D4D4D4
    public uint SelectionColor { get; set; } = 0xA03385DB;  // From TerminalCanvas SelectionBrush
    public uint TabBarBackgroundColor { get; set; } = 0xFF1A1A1A;  // Dark gray

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
    public uint? CursorColor { get; set; } = null;  // null = use foreground
    public bool CursorShowUnfocused { get; set; } = false;

    // Window opacity (0-100, where 100 is fully opaque)
    public byte Opacity { get; set; } = 100;

    // ANSI colors (standard 16-color palette)
    public uint[] AnsiColors { get; set; } = new uint[]
    {
        0xFF000000,  // Black (30)
        0xFFAA0000,  // Red (31)
        0xFF00AA00,  // Green (32)
        0xFFAA5500,  // Yellow (33)
        0xFF0000AA,  // Blue (34)
        0xFFAA00AA,  // Magenta (35)
        0xFF00AAAA,  // Cyan (36)
        0xFFAAAAAA,  // White (37)
        0xFF555555,  // Bright Black (90)
        0xFFFF5555,  // Bright Red (91)
        0xFF55FF55,  // Bright Green (92)
        0xFFFFFF55,  // Bright Yellow (93)
        0xFF5555FF,  // Bright Blue (94)
        0xFFFF55FF,  // Bright Magenta (95)
        0xFF55FFFF,  // Bright Cyan (96)
        0xFFFFFFFF,  // Bright White (97)
    };
}

/// <summary>
/// Represents a key binding entry.
/// </summary>
internal readonly struct KeyBinding
{
    public string Key { get; }
    public string Modifiers { get; }
    public string Action { get; }
    
    public KeyBinding(string key, string modifiers, string action)
    {
        Key = key;
        Modifiers = modifiers;
        Action = action;
    }
}
