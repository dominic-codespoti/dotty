using System;
using System.IO;

namespace Dotty.App.Services;

/// <summary>
/// Service responsible for generating the user's standalone C# configuration file.
/// </summary>
public static class ConfigGeneratorService
{
    private static readonly string ApplicationDataConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dotty");

    /// <summary>
    /// The directory where the user configuration file lives.
    /// </summary>
    public static readonly string ConfigDir = ApplicationDataConfigDir;

    /// <summary>
    /// The canonical configuration file path.
    /// </summary>
    public static readonly string ConfigPath = Path.Combine(ConfigDir, "Config.cs");
    
    /// <summary>
    /// Optional editor project path. Dotty never uses this project at runtime.
    /// </summary>
    public static readonly string EditorProjectPath = Path.Combine(ConfigDir, "Dotty.UserConfig.csproj");

    /// <summary>
    /// True when the editor project was freshly created (not just version-updated)
    /// during the most recent EnsureConfigExists call.
    /// </summary>
    internal static bool EditorProjectWasCreated { get; private set; }

    /// <summary>
    /// Reset the editor-created flag at the start of EnsureConfigExists.
    /// </summary>
    private static void ResetEditorCreatedFlag() => EditorProjectWasCreated = false;

    private static bool EditorProjectIsCurrent(string editorProjectPath)
    {
        if (!File.Exists(editorProjectPath))
            return false;
        var content = File.ReadAllText(editorProjectPath);
        return content.Contains($"Version=\"{Dotty.VersionInfo.NuGetPackageVersion}\"");
    }

    private static string GenerateEditorProject() =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        $"  <PropertyGroup>\n" +
        $"    <TargetFramework>net10.0</TargetFramework>\n" +
        $"    <Nullable>enable</Nullable>\n" +
        $"    <ImplicitUsings>enable</ImplicitUsings>\n" +
        $"    <LangVersion>latest</LangVersion>\n" +
        $"  </PropertyGroup>\n" +
        $"  <ItemGroup>\n" +
        $"    <Compile Include=\"Config.cs\" />\n" +
        $"    <PackageReference Include=\"Dotty.Abstractions\" Version=\"{Dotty.VersionInfo.NuGetPackageVersion}\" />\n" +
        $"  </ItemGroup>\n" +
        $"</Project>\n";

    private static string GetLegacyNestedConfigPath(string configDir) =>
        Path.Combine(configDir, "Dotty.UserConfig", "Config.cs");

    /// <summary>
    /// Checks common configuration file locations and returns the path if found.
    /// A nested legacy configuration is migrated to the canonical flat path when possible.
    /// </summary>
    public static string? GetExistingConfigPath()
    {
        return GetExistingConfigPath(ConfigDir) ?? GetExternalLegacyConfigPath();
    }

    private static string? GetExternalLegacyConfigPath()
    {
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

    internal static string? GetExistingConfigPath(string configDir)
    {
        var configPath = Path.Combine(configDir, "Config.cs");
        if (File.Exists(configPath))
            return configPath;

        var nestedLegacyPath = GetLegacyNestedConfigPath(configDir);
        if (File.Exists(nestedLegacyPath))
        {
            try
            {
                Directory.CreateDirectory(configDir);
                File.Copy(nestedLegacyPath, configPath);
                return configPath;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not migrate legacy config: {ex.Message}");
                return nestedLegacyPath;
            }
        }

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

    public static bool EnsureConfigExists(bool force = false) =>
        EnsureConfigExists(ConfigDir, force);

    internal static bool EnsureConfigExists(string configDir, bool force = false)
    {
        var configPath = Path.Combine(configDir, "Config.cs");

        try
        {
            Directory.CreateDirectory(configDir);
            var configCreated = false;

            // Check only the canonical flat path — don't call GetExistingConfigPath
            // (it can do a side-effect migration copy that might fail).
            if (force || !File.Exists(configPath))
            {
                if (force)
                {
                    // Force overwrite — always regenerate the default config.
                    File.WriteAllText(configPath, GenerateDefaultConfig(configPath));
                    configCreated = true;
                }
                else
                {
                    // Creating fresh: migrate from legacy nested config if present.
                    var nestedLegacyPath = GetLegacyNestedConfigPath(configDir);
                    if (!File.Exists(configPath) && File.Exists(nestedLegacyPath))
                    {
                        try { File.Copy(nestedLegacyPath, configPath, overwrite: false); }
                        catch { /* migration failed — generate default below */ }
                    }

                    if (!File.Exists(configPath))
                    {
                        File.WriteAllText(configPath, GenerateDefaultConfig(configPath));
                        configCreated = true;
                    }
                }
            }

            ResetEditorCreatedFlag();
            var editorProjectPath = Path.Combine(configDir, "Dotty.UserConfig.csproj");
            if (force || !EditorProjectIsCurrent(editorProjectPath))
            {
                var newlyCreated = !File.Exists(editorProjectPath);
                File.WriteAllText(editorProjectPath, GenerateEditorProject());
                if (newlyCreated) EditorProjectWasCreated = true;
            }

            return configCreated;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to create config: {ex.Message}");
            return false;
        }
    }


    /// <summary>
    /// Generates the default configuration file content with current defaults.
    /// </summary>
    private static string GenerateDefaultConfig(string configPath)
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
               $"// Dotty compiles this ordinary C# file in memory for runtime configuration.\n" +
               $"// Dotty.UserConfig.csproj is generated beside this file for editor IntelliSense only.\n" +
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
                $"    // Enable shell integration for prompt marking and navigation\n" +
                $"    // When enabled, Ctrl+Up/Down jumps between shell prompts.\n" +
                $"    // Also requires shell-side config (see docs/shell-integration.md)\n" +
                $"    public bool? EnableShellIntegration => true;\n" +
                $"    \n" +
                 $"    // =========================================================================\n" +
                 $"    // TRANSPARENCY SETTINGS (Optional - null uses defaults)\n" +
                $"    // =========================================================================\n" +
                $"    // There are two ways to achieve transparency:\n" +
                $"    //\n" +
                $"    // 1. WindowOpacity (0-100): Makes the entire window semi-transparent\n" +
                $"    //    - 100 = fully opaque, 0 = fully transparent\n" +
                $"    //    - Works on all platforms, but behavior varies:\n" +
                $"    //      * X11/Windows/macOS: Uses Avalonia's Opacity property\n" +
                $"    //      * Wayland (except Hyprland): Uses brush alpha\n" +
                $"    //      * Hyprland: Use windowrulev2 in hyprland.conf instead\n" +
                $"    //\n" +
                $"    // 2. Transparency (Blur/Acrylic/Transparent): Enables native blur effects\n" +
                $"    //    - Uses Avalonia's TransparencyLevelHint system\n" +
                $"    //    - Platform support varies (best on Windows, limited on Linux)\n" +
                $"    //\n" +
                $"    // For Hyprland users:\n" +
                $"    //   Add to ~/.config/hypr/hyprland.conf:\n" +
                $"    //   windowrulev2 = opacity 0.5,class:^Dotty$\n" +
                $"    //\n" +
                $"    // Window opacity level (0-100). Set to null for fully opaque.\n" +
                $"    // public byte? WindowOpacity => 85;  // 85% opaque\n" +
                $"    \n" +
                $"    // Transparency level for native blur effects\n" +
                $"    // Options: None, Transparent, Blur, Acrylic\n" +
                $"    public TransparencyLevel? Transparency => null;  // Default: None\n" +
                $"    \n" +
                $"    // =========================================================================\n" +
                $"    // UI COLORS (Optional - null uses defaults)\n" +
                $"    // =========================================================================\n" +
                $"    // ARGB format: 0xAARRGGBB\n" +
                $"    \n" +
                $"    // Selection highlight color\n" +
                $"    public uint? SelectionColor => null;  // Default: 0x{defaultSelectionColor:X8}\n" +
                $"    \n" +
                $"    // Tab bar background color\n" +
                $"    public uint? TabBarBackgroundColor => null;\n" +
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
