using System;
using System.IO;
using System.Text.RegularExpressions;

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
    /// The current/latest version of Dotty.Abstractions package.
    /// This should match the version in Dotty.Abstractions.csproj
    /// </summary>
    public const string LatestPackageVersion = "0.2.0";

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
    /// Checks if the user's config project is using an outdated NuGet package version
    /// and updates it if necessary.
    /// </summary>
    /// <returns>True if an update was performed, false otherwise</returns>
    public static bool UpdatePackageVersionIfNeeded()
    {
        try
        {
            // Check if project file exists
            if (!File.Exists(ProjectPath))
                return false;
            
            var csprojContent = File.ReadAllText(ProjectPath);
            
            // Check if Dotty.Abstractions is referenced
            if (!csprojContent.Contains("Dotty.Abstractions"))
                return false;
            
            // Extract current version using regex
            var versionMatch = Regex.Match(
                csprojContent, 
                @"<PackageReference Include=""Dotty.Abstractions"" Version=""(\d+\.\d+\.\d+)""");
            
            if (!versionMatch.Success)
            {
                // Package reference exists but we can't parse version
                // Might be an older format, suggest manual update
                Console.WriteLine("⚠ Your config project uses an outdated format.");
                Console.WriteLine("  Consider running with --update-config to regenerate.");
                return false;
            }
            
            var currentVersion = versionMatch.Groups[1].Value;
            
            // Compare versions
            if (Version.TryParse(currentVersion, out var current) && 
                Version.TryParse(LatestPackageVersion, out var latest))
            {
                if (current < latest)
                {
                    // Update the version in the csproj
                    var updatedContent = Regex.Replace(
                        csprojContent,
                        @"(<PackageReference Include=""Dotty.Abstractions"" Version="")(\d+\.\d+\.\d+)("")",
                        "${1}" + LatestPackageVersion + "${3}");
                    
                    File.WriteAllText(ProjectPath, updatedContent);
                    
                    Console.WriteLine($"✓ Updated Dotty.Abstractions from {currentVersion} to {LatestPackageVersion}");
                    Console.WriteLine("  Run 'dotnet restore' in your config folder to apply changes.");
                    return true;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ Could not check for package updates: {ex.Message}");
            return false;
        }
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
        return $"<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
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
               $"    <PackageReference Include=\"Dotty.Abstractions\" Version=\"{LatestPackageVersion}\" />\n" +
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
               $"/// All properties implement IDottyConfig interface.\n" +
               $"/// Return null to use Dotty's built-in defaults.\n" +
               $"/// </summary>\n" +
               $"public partial class MyDottyConfig : IDottyConfig\n" +
               $"{{\n" +
               $"    // =========================================================================\n" +
               $"    // THEME (Required - must specify a theme)\n" +
               $"    // =========================================================================\n" +
               $"    // Choose from: DarkPlus, Dracula, OneDark, GruvboxDark, CatppuccinMocha,\n" +
               $"    //              TokyoNight, LightPlus, OneLight, GruvboxLight,\n" +
               $"    //              CatppuccinLatte, SolarizedLight\n" +
               $"    //\n" +
               $"    public IColorScheme? Colors => BuiltInThemes.DarkPlus;\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // FONT SETTINGS (Optional - null uses defaults)\n" +
               $"    // =========================================================================\n" +
               $"    // Font family stack - comma-separated list with fallbacks.\n" +
               $"    // First available font is used.\n" +
               $"    // Example: \"Fira Code, JetBrains Mono, Cascadia Code, monospace\"\n" +
               $"    public string? FontFamily => null;  // Default: {defaultFontFamily}\n" +
               $"    \n" +
               $"    // Font size in points\n" +
               $"    public double? FontSize => null;  // Default: {defaultFontSize}\n" +
               $"    \n" +
               $"    // Cell padding in pixels\n" +
               $"    public double? CellPadding => null;  // Default: {defaultCellPadding}\n" +
               $"    \n" +
               $"    // Content padding around terminal area (Left, Top, Right, Bottom)\n" +
               $"    public Thickness? ContentPadding => null;  // Default: 0,0,0,0\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // TERMINAL SETTINGS (Optional - null uses defaults)\n" +
               $"    // =========================================================================\n" +
               $"    // Scrollback buffer size - number of lines to keep in memory\n" +
               $"    public int? ScrollbackLines => null;  // Default: {defaultScrollbackLines}\n" +
               $"    \n" +
               $"    // Time before inactive tab visuals are destroyed (milliseconds)\n" +
               $"    public int? InactiveTabDestroyDelayMs => null;  // Default: {defaultInactiveTabDelay}\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // UI COLORS (Optional - null uses defaults)\n" +
               $"    // ARGB format: 0xAARRGGBB\n" +
               $"    // =========================================================================\n" +
               $"    // Selection highlight color\n" +
               $"    public uint? SelectionColor => null;  // Default: 0x{defaultSelectionColor:X8}\n" +
               $"    \n" +
               $"    // Tab bar background color\n" +
               $"    public uint? TabBarBackgroundColor => null;  // Default: 0x{{defaultTabBarBgColor:X8}}\n" +
               $"    \n" +
               $"    // Window transparency level\n" +
               $"    // Options: None (solid), Transparent (simple transparency),\n" +
               $"    //          Blur (blurred background), Acrylic (full acrylic with noise)\n" +
               $"    public TransparencyLevel? Transparency => null;  // Default: None\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // WINDOW SETTINGS (Optional - null uses defaults)\n" +
               $"    // =========================================================================\n" +
               $"    // Initial window dimensions\n" +
               $"    public IWindowDimensions? InitialDimensions => null;\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // CURSOR SETTINGS (Optional - null uses defaults)\n" +
               $"    // =========================================================================\n" +
               $"    public ICursorSettings? Cursor => null;\n" +
               $"    \n" +
               $"    // =========================================================================\n" +
               $"    // KEY BINDINGS (Optional - null uses defaults)\n" +
               $"    // =========================================================================\n" +
               $"    // Uncomment and implement CustomKeyBindings class below to customize\n" +
               $"    public IKeyBindings? KeyBindings => null;\n" +
               $"}}\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Key Bindings\n" +
               $"// =========================================================================\n" +
               $"// Uncomment and customize this class, then set:\n" +
               $"//   public IKeyBindings? KeyBindings => new CustomKeyBindings();\n" +
               $"/*\n" +
               $"public class CustomKeyBindings : IKeyBindings\n" +
               $"{{\n" +
               $"    public TerminalAction? GetAction(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers)\n" +
               $"    {{\n" +
               $"        // Example: Map F12 to toggle fullscreen\n" +
               $"        // if (key == Avalonia.Input.Key.F12)\n" +
               $"        //     return TerminalAction.ToggleFullscreen;\n" +
               $"        \n" +
               $"        return null;  // Use default bindings\n" +
               $"    }}\n" +
               $"}}\n" +
               $"*/\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Window Dimensions\n" +
               $"// =========================================================================\n" +
               $"/*\n" +
               $"public class WindowDimensions : IWindowDimensions\n" +
               $"{{\n" +
               $"    public int Columns {{ get; init; }} = 120;\n" +
               $"    public int Rows {{ get; init; }} = 40;\n" +
               $"    public int? WidthPixels {{ get; init; }} = null;\n" +
               $"    public int? HeightPixels {{ get; init; }} = null;\n" +
               $"    public bool StartFullscreen {{ get; init; }} = false;\n" +
               $"    public string? Title {{ get; init; }} = \"Dotty\";\n" +
               $"}}\n" +
               $"*/\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Cursor Settings\n" +
               $"// =========================================================================\n" +
               $"/*\n" +
               $"public class CursorSettings : ICursorSettings\n" +
               $"{{\n" +
               $"    public CursorShape Shape {{ get; init; }} = CursorShape.Block;\n" +
               $"    public bool Blink {{ get; init; }} = true;\n" +
               $"    public int BlinkIntervalMs {{ get; init; }} = 500;\n" +
               $"    public uint? Color {{ get; init; }} = null;  // null = use foreground\n" +
               $"    public bool ShowUnfocused {{ get; init; }} = false;\n" +
               $"}}\n" +
               $"*/\n" +
               $"\n" +
               $"// =========================================================================\n" +
               $"// EXAMPLE: Custom Theme with Opacity\n" +
               $"// =========================================================================\n" +
               $"/*\n" +
               $"public class TranslucentDracula : DraculaTheme\n" +
               $"{{\n" +
               $"    // 85 = 85% opaque, 15% transparent\n" +
               $"    public override byte Opacity => 85;\n" +
               $"}}\n" +
               $"\n" +
               $"// Then use it:\n" +
               $"// public IColorScheme? Colors => new TranslucentDracula();\n" +
               $"*/\n";
    }
}
