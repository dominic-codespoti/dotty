using System;
using System.IO;

namespace Dotty.App.Services;

/// <summary>
/// Service responsible for generating a default user configuration project
/// on first run of Dotty terminal. Creates a full .csproj with NuGet reference
/// for full LSP support and IntelliSense.
/// </summary>
public static class ConfigGeneratorService
{
    private static readonly string BaseConfigDir = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dotty");
    
    /// <summary>
    /// The project directory where user config lives.
    /// </summary>
    public static readonly string ProjectDir = Path.Combine(BaseConfigDir, "Dotty.UserConfig");
    
    /// <summary>
    /// The main config file path.
    /// </summary>
    public static readonly string ConfigPath = Path.Combine(ProjectDir, "Config.cs");
    
    /// <summary>
    /// The project file path.
    /// </summary>
    public static readonly string ProjectPath = Path.Combine(ProjectDir, "Dotty.UserConfig.csproj");

    /// <summary>
    /// Checks common configuration file locations and returns the path if found.
    /// </summary>
    public static string? GetExistingConfigPath()
    {
        // Check new project structure first
        if (File.Exists(ConfigPath))
            return ConfigPath;
        
        // Check legacy locations
        var legacyPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dotty", "Config.cs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "dotty", "Config.cs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dotty", "Config.cs"),
        };
        
        foreach (var path in legacyPaths)
        {
            if (File.Exists(path))
                return path;
        }
        
        return null;
    }

    /// <summary>
    /// Ensures a configuration project exists for the user.
    /// If no config exists, creates one with sensible defaults.
    /// </summary>
    /// <param name="force">If true, overwrites existing config (use with caution)</param>
    /// <returns>True if a config was created, false if one already exists</returns>
    public static bool EnsureConfigExists(bool force = false)
    {
        if (!force && GetExistingConfigPath() != null)
            return false; // Config already exists
        
        try
        {
            // Create project directory
            Directory.CreateDirectory(ProjectDir);
            
            // Write Config.cs
            File.WriteAllText(ConfigPath, GenerateDefaultConfig());
            
            // Write .csproj with NuGet reference
            File.WriteAllText(ProjectPath, GenerateProjectFile());
            
            return true;
        }
        catch (Exception ex)
        {
            // Log error but don't crash the app
            Console.Error.WriteLine($"Failed to create config: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Generates the .csproj file with NuGet package reference.
    /// </summary>
    private static string GenerateProjectFile()
    {
        return "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
               "\n" +
               "  <PropertyGroup>\n" +
               "    <TargetFramework>net10.0</TargetFramework>\n" +
               "    <Nullable>enable</Nullable>\n" +
               "    <ImplicitUsings>enable</ImplicitUsings>\n" +
               "    <LangVersion>latest</LangVersion>\n" +
               "    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>\n" +
               "  </PropertyGroup>\n" +
               "\n" +
               "  <ItemGroup>\n" +
               "    <PackageReference Include=\"Dotty.Abstractions\" Version=\"0.1.0\" />\n" +
               "  </ItemGroup>\n" +
               "\n" +
               "</Project>";
    }

    /// <summary>
    /// Generates the default configuration file content with current defaults.
    /// </summary>
    private static string GenerateDefaultConfig()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var defaultFontFamily = DefaultConstants.FontFamily;
        var defaultFontSize = DefaultConstants.FontSize;
        var defaultCellPadding = DefaultConstants.CellPadding;
        var defaultScrollbackLines = DefaultConstants.ScrollbackLines;
        var defaultInactiveTabDelay = DefaultConstants.InactiveTabDestroyDelayMs;
        var defaultSelectionColor = DefaultConstants.SelectionColor;
        var defaultTabBarBgColor = DefaultConstants.TabBarBackgroundColor;
        
        return $"// Dotty Terminal Configuration\n" +
               $"// ===========================\n" +
               $"// This file was auto-generated on first run ({date}).\n" +
               $"// Edit these values and restart Dotty to see changes.\n" +
               $"//\n" +
               $"// Full IntelliSense available when you open this folder in VS Code or Rider!\n" +
               $"// The Dotty.Abstractions package provides all types and themes.\n" +
               $"//\n" +
               $"// Project: {ProjectPath}\n" +
               $"// Documentation: https://github.com/dominic-codespoti/dotty/blob/main/docs/CONFIGURATION.md\n" +
               $"\n" +
               $"using Dotty.Abstractions.Config;\n" +
               $"using Dotty.Abstractions.Themes;\n" +
               $"\n" +
               $"namespace Dotty.UserConfig;\n" +
               $"\n" +
               $"/// <summary>\n" +
               $"/// Your custom Dotty terminal configuration.\n" +
               $"/// All properties are optional - uncomment to override defaults.\n" +
               $"/// </summary>\n" +
               $"public partial class MyDottyConfig : IDottyConfig\n" +
               $"{{\n" +
               $"    // =========================================================================\n" +
               $"    // THEME\n" +
               $"    // =========================================================================\n" +
               $"    // Choose from: DarkPlus, Dracula, OneDark, GruvboxDark, CatppuccinMocha,\n" +
               $"    //              TokyoNight, LightPlus, OneLight, GruvboxLight,\n" +
               $"    //              CatppuccinLatte, SolarizedLight\n" +
               $"    //\n" +
               $"    public IColorScheme? Colors => BuiltInThemes.DarkPlus;\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // FONT SETTINGS\n" +
               $"    // =========================================================================\n" +
               $"    // Font family stack - comma-separated list with fallbacks.\n" +
               $"    // First available font is used.\n" +
               $"    // public string? FontFamily => \"{defaultFontFamily}\";\n" +
               $"    \n" +
               $"    // Font size in points (default: {defaultFontSize})\n" +
               $"    // public double? FontSize => {defaultFontSize};\n" +
               $"    \n" +
               $"    // Cell padding in pixels (default: {defaultCellPadding})\n" +
               $"    // public double? CellPadding => {defaultCellPadding};\n" +
               $"    \n" +
               $"    // Content padding around terminal area (default: 0,0,0,0)\n" +
               $"    // public Thickness? ContentPadding => new Thickness(0.0);\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // TERMINAL SETTINGS\n" +
               $"    // =========================================================================\n" +
               $"    // Scrollback buffer size - number of lines to keep in memory\n" +
               $"    // (default: {defaultScrollbackLines})\n" +
               $"    // public int? ScrollbackLines => {defaultScrollbackLines};\n" +
               $"    \n" +
               $"    // Time before inactive tab visuals are destroyed (default: {defaultInactiveTabDelay}ms)\n" +
               $"    // public int? InactiveTabDestroyDelayMs => {defaultInactiveTabDelay};\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // UI COLORS (ARGB format: 0xAARRGGBB)\n" +
               $"    // =========================================================================\n" +
               $"    // Selection highlight color (default: 0x{defaultSelectionColor:X8})\n" +
               $"    // public uint? SelectionColor => 0x{defaultSelectionColor:X8};\n" +
               $"    \n" +
               $"    // Tab bar background color (default: 0x{defaultTabBarBgColor:X8})\n" +
               $"    // public uint? TabBarBackgroundColor => 0x{defaultTabBarBgColor:X8};\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // WINDOW SETTINGS\n" +
               $"    // =========================================================================\n" +
               $"    // Initial window dimensions\n" +
               $"    // public IWindowDimensions? InitialDimensions => new WindowDimensions\n" +
               $"    // {{\n" +
               $"    //     Columns = 80,\n" +
               $"    //     Rows = 24,\n" +
               $"    //     Title = \"Dotty\"\n" +
               $"    // }};\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // CURSOR SETTINGS\n" +
               $"    // =========================================================================\n" +
               $"    // public ICursorSettings? Cursor => new CursorSettings\n" +
               $"    // {{\n" +
               $"    //     Shape = CursorShape.Block,\n" +
               $"    //     Blink = true,\n" +
               $"    //     BlinkIntervalMs = 500,\n" +
               $"    //     Color = null,  // null = use foreground color\n" +
               $"    //     ShowUnfocused = false\n" +
               $"    // }};\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // KEY BINDINGS\n" +
               $"    // =========================================================================\n" +
               $"    // Uncomment to use custom key bindings instead of defaults\n" +
               $"    // public IKeyBindings? KeyBindings => new CustomKeyBindings();\n" +
               $"}}\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Window Dimensions\n" +
               $"// =========================================================================\n" +
               $"// public class WindowDimensions : IWindowDimensions\n" +
               $"// {{\n" +
               $"//     public int Columns {{ get; init; }} = 120;\n" +
               $"//     public int Rows {{ get; init; }} = 40;\n" +
               $"//     public int? WidthPixels {{ get; init; }} = null;\n" +
               $"//     public int? HeightPixels {{ get; init; }} = null;\n" +
               $"//     public bool StartFullscreen {{ get; init; }} = false;\n" +
               $"//     public string? Title {{ get; init; }} = \"Dotty\";\n" +
               $"// }}\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Cursor Settings\n" +
               $"// =========================================================================\n" +
               $"// public class CursorSettings : ICursorSettings\n" +
               $"// {{\n" +
               $"//     public CursorShape Shape {{ get; init; }} = CursorShape.Block;\n" +
               $"//     public bool Blink {{ get; init; }} = true;\n" +
               $"//     public int BlinkIntervalMs {{ get; init; }} = 500;\n" +
               $"//     public uint? Color {{ get; init; }} = null;\n" +
               $"//     public bool ShowUnfocused {{ get; init; }} = false;\n" +
               $"// }}\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Key Bindings\n" +
               $"// =========================================================================\n" +
               $"// public class CustomKeyBindings : IKeyBindings\n" +
               $"// {{\n" +
               $"//     public TerminalAction? GetAction(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)\n" +
               $"//     {{\n" +
               $"//         // Add your custom key bindings here\n" +
               $"//         // Return null to use default bindings for unhandled keys\n" +
               $"//         return null;\n" +
               $"//     }}\n" +
               $"// }}\n";
    }
}
