# Custom Theme Architecture Design Document

**Dotty Terminal Emulator - User-Defined Theme System**

**Version:** 1.0  
**Status:** Design Specification  
**Target Implementation:** Phase 1-2 (v1.x)

---

## 1. Executive Summary

The Custom Theme System enables users to create, load, and apply their own terminal color themes without modifying Dotty's source code or recompiling. Users define themes as JSON files in `~/.config/dotty/themes/`, following the same schema as built-in themes. The system supports theme inheritance for customization, hot-reloading for rapid iteration, and full validation to ensure theme integrity. This feature bridges the gap between Dotty's rich built-in themes and users' desire for personalized terminal aesthetics, while maintaining full backward compatibility with the existing `BuiltInThemes` API and configuration system.

---

## 2. User Experience

### 2.1 Creating a Custom Theme

Users create themes by placing JSON files in the user themes directory:

```bash
mkdir -p ~/.config/dotty/themes
cat > ~/.config/dotty/themes/my-theme.json << 'EOF'
{
  "name": "MyOceanTheme",
  "description": "A calming ocean-inspired theme",
  "isDark": true,
  "author": "Jane Developer",
  "aliases": ["ocean", "sea"],
  "colors": {
    "background": "#0D1B2A",
    "foreground": "#E0E1DD",
    "ansiBlack": "#1B263B",
    "ansiRed": "#FF6B6B",
    "ansiGreen": "#4ECDC4",
    "ansiYellow": "#FFE66D",
    "ansiBlue": "#45B7D1",
    "ansiMagenta": "#96CEB4",
    "ansiCyan": "#7FDBDA",
    "ansiWhite": "#E0E1DD",
    "ansiBrightBlack": "#415A77",
    "ansiBrightRed": "#FF8E8E",
    "ansiBrightGreen": "#6EE7DE",
    "ansiBrightYellow": "#FFF0A3",
    "ansiBrightBlue": "#6FCBE0",
    "ansiBrightMagenta": "#B8E0D0",
    "ansiBrightCyan": "#A8F0F0",
    "ansiBrightWhite": "#FFFFFF"
  },
  "opacity": 95
}
EOF
```

### 2.2 Format Compatibility

User theme JSON files follow the **exact same schema** as built-in themes defined in `themes.json`:

- Same field names and structure
- Same color format (`#RRGGBB` or `#AARRGGBB`)
- Same metadata fields (`name`, `description`, `isDark`, `aliases`)
- Same optional fields (`opacity` with default 100)

### 2.3 Theme Selection

Users can select themes in two ways:

**Via Configuration (compile-time):**
```csharp
public partial class MyConfig : IDottyConfig
{
    // Reference user theme by name - works just like built-in themes
    public IColorScheme? Colors => UserThemes.Get("MyOceanTheme");
}
```

**Via Runtime API:**
```csharp
// Switch theme at runtime
await ThemeManager.Current.ApplyThemeAsync("MyOceanTheme");

// Or switch with animation
await ThemeManager.Current.ApplyThemeAsync("MyOceanTheme", animate: true);
```

### 2.4 Theme Discovery

Users can list available themes:

```csharp
// Get all themes (built-in + user)
var allThemes = ThemeRegistry.Current.GetAllThemes();

// Filter by dark/light
var darkThemes = ThemeRegistry.Current.GetDarkThemes();

// Search themes
var oceanThemes = ThemeRegistry.Current.SearchThemes("ocean");
```

---

## 3. Directory Structure

### 3.1 User Themes Directory

```
~/.config/dotty/
├── config.toml              # User configuration file
├── themes/                  # User-defined themes directory
│   ├── my-ocean-theme.json
│   ├── solarized-custom.json
│   ├── company-brand.json
│   └── gruvbox-modified.json
└── state.json               # Runtime state (not themes)
```

### 3.2 Cross-Platform Paths

| Platform | User Themes Directory |
|----------|----------------------|
| Linux | `~/.config/dotty/themes/` |
| macOS | `~/Library/Application Support/Dotty/themes/` |
| Windows | `%APPDATA%\Dotty\themes\` |

### 3.3 Directory Resolution

```csharp
public static class ThemePaths
{
    public static string GetUserThemesDirectory()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        
        return Path.Combine(baseDir, "Dotty", "themes");
    }
    
    public static void EnsureThemesDirectoryExists()
    {
        var dir = GetUserThemesDirectory();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            
            // Optionally create a sample theme
            CreateSampleTheme(dir);
        }
    }
}
```

---

## 4. JSON Schema

### 4.1 Schema Definition

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "Dotty User Theme",
  "description": "User-defined theme for Dotty terminal emulator",
  "type": "object",
  "required": ["name", "description", "isDark", "colors"],
  "properties": {
    "name": {
      "type": "string",
      "pattern": "^[a-zA-Z][a-zA-Z0-9_-]{0,31}$",
      "description": "Unique theme identifier (alphanumeric, hyphens, underscores)"
    },
    "description": {
      "type": "string",
      "maxLength": 256,
      "description": "Human-readable theme description"
    },
    "isDark": {
      "type": "boolean",
      "description": "Whether this is a dark theme"
    },
    "author": {
      "type": "string",
      "maxLength": 128,
      "description": "Theme author name (optional)"
    },
    "parent": {
      "type": "string",
      "description": "Parent theme name for inheritance (optional)"
    },
    "aliases": {
      "type": "array",
      "items": {
        "type": "string",
        "pattern": "^[a-zA-Z][a-zA-Z0-9_-]{0,31}$"
      },
      "maxItems": 10,
      "description": "Alternative names for this theme"
    },
    "opacity": {
      "type": "integer",
      "minimum": 0,
      "maximum": 100,
      "default": 100,
      "description": "Window background opacity (0-100)"
    },
    "colors": {
      "type": "object",
      "required": [
        "background", "foreground",
        "ansiBlack", "ansiRed", "ansiGreen", "ansiYellow",
        "ansiBlue", "ansiMagenta", "ansiCyan", "ansiWhite",
        "ansiBrightBlack", "ansiBrightRed", "ansiBrightGreen",
        "ansiBrightYellow", "ansiBrightBlue", "ansiBrightMagenta",
        "ansiBrightCyan", "ansiBrightWhite"
      ],
      "properties": {
        "background": { "$ref": "#/definitions/color" },
        "foreground": { "$ref": "#/definitions/color" },
        "ansiBlack": { "$ref": "#/definitions/color" },
        "ansiRed": { "$ref": "#/definitions/color" },
        "ansiGreen": { "$ref": "#/definitions/color" },
        "ansiYellow": { "$ref": "#/definitions/color" },
        "ansiBlue": { "$ref": "#/definitions/color" },
        "ansiMagenta": { "$ref": "#/definitions/color" },
        "ansiCyan": { "$ref": "#/definitions/color" },
        "ansiWhite": { "$ref": "#/definitions/color" },
        "ansiBrightBlack": { "$ref": "#/definitions/color" },
        "ansiBrightRed": { "$ref": "#/definitions/color" },
        "ansiBrightGreen": { "$ref": "#/definitions/color" },
        "ansiBrightYellow": { "$ref": "#/definitions/color" },
        "ansiBrightBlue": { "$ref": "#/definitions/color" },
        "ansiBrightMagenta": { "$ref": "#/definitions/color" },
        "ansiBrightCyan": { "$ref": "#/definitions/color" },
        "ansiBrightWhite": { "$ref": "#/definitions/color" }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false,
  "definitions": {
    "color": {
      "type": "string",
      "pattern": "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$",
      "description": "Hex color in #RRGGBB or #AARRGGBB format"
    }
  }
}
```

### 4.2 Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Unique identifier (alphanumeric + hyphens/underscores) |
| `description` | string | Human-readable description |
| `isDark` | boolean | Theme category for UI filtering |
| `colors.background` | color | Terminal background color |
| `colors.foreground` | color | Text foreground color |
| `colors.ansiBlack` - `colors.ansiBrightWhite` | color | All 16 ANSI colors |

### 4.3 Optional Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `author` | string | null | Theme creator name |
| `parent` | string | null | Parent theme for inheritance |
| `aliases` | string[] | [] | Alternative theme names |
| `opacity` | integer | 100 | Window transparency (0-100) |

### 4.4 Color Format

Colors use hex notation with optional alpha:

```json
{
  "colors": {
    "background": "#FF1E1E1E",  // Full opacity dark gray
    "foreground": "#E5E5E5",    // Implicit full opacity
    "ansiRed": "#CD3131"
  }
}
```

**Format Rules:**
- `#RRGGBB` - 6 hex digits, alpha defaults to 0xFF (opaque)
- `#AARRGGBB` - 8 hex digits with explicit alpha
- Case insensitive (`#ff0000` = `#FF0000`)
- Must start with `#`

### 4.5 Complete Example

```json
{
  "name": "NordCustom",
  "description": "Nord theme with custom accent colors",
  "author": "Arctic Ice Studio (modified by User)",
  "isDark": true,
  "parent": "OneDark",
  "aliases": ["nord-mod", "arctic"],
  "opacity": 100,
  "colors": {
    "background": "#2E3440",
    "foreground": "#D8DEE9",
    "ansiBlack": "#3B4252",
    "ansiRed": "#BF616A",
    "ansiGreen": "#A3BE8C",
    "ansiYellow": "#EBCB8B",
    "ansiBlue": "#81A1C1",
    "ansiMagenta": "#B48EAD",
    "ansiCyan": "#88C0D0",
    "ansiWhite": "#E5E9F0",
    "ansiBrightBlack": "#4C566A",
    "ansiBrightRed": "#BF616A",
    "ansiBrightGreen": "#A3BE8C",
    "ansiBrightYellow": "#EBCB8B",
    "ansiBrightBlue": "#81A1C1",
    "ansiBrightMagenta": "#B48EAD",
    "ansiBrightCyan": "#8FBCBB",
    "ansiBrightWhite": "#ECEFF4"
  }
}
```

---

## 5. Architecture Components

### 5.1 Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Theme System Architecture                │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│   ┌──────────────────┐      ┌──────────────────┐         │
│   │  BuiltInThemes   │      │  UserThemeLoader   │         │
│   │  (Static API)    │      │  (File I/O)       │         │
│   └────────┬─────────┘      └────────┬─────────┘         │
│            │                         │                      │
│            ▼                         ▼                      │
│   ┌──────────────────────────────────────┐              │
│   │        ThemeRegistry                  │              │
│   │  (Merges built-in + user themes)     │              │
│   └────────┬─────────────────────────────┘              │
│            │                                              │
│            ▼                                              │
│   ┌──────────────────────────────────────┐              │
│   │         ThemeManager                  │              │
│   │  (Apply themes, events, validation)  │              │
│   └────────┬─────────────────────────────┘              │
│            │                                              │
│            ▼                                              │
│   ┌──────────────────────────────────────┐              │
│   │   Terminal UI / ConfigBridge           │              │
│   └──────────────────────────────────────┘              │
│                                                             │
│   ┌──────────────────────────────────────┐              │
│   │   ThemeValidator / HotReloadWatcher  │              │
│   └──────────────────────────────────────┘              │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### 5.2 UserThemeLoader

Responsible for loading user themes from disk.

```csharp
namespace Dotty.Themes.User;

/// <summary>
/// Loads user-defined themes from the filesystem.
/// Handles file I/O, parsing, and error recovery.
/// </summary>
public sealed class UserThemeLoader
{
    private readonly string _themesDirectory;
    private readonly ILogger<UserThemeLoader> _logger;
    private readonly ThemeValidator _validator;
    
    public UserThemeLoader(
        string? themesDirectory = null,
        ILogger<UserThemeLoader>? logger = null,
        ThemeValidator? validator = null)
    {
        _themesDirectory = themesDirectory ?? ThemePaths.GetUserThemesDirectory();
        _logger = logger ?? NullLogger<UserThemeLoader>.Instance;
        _validator = validator ?? new ThemeValidator();
    }
    
    /// <summary>
    /// Loads all valid themes from the user themes directory.
    /// </summary>
    /// <returns>Array of loaded theme definitions (invalid themes skipped)</returns>
    public IReadOnlyList<UserThemeDefinition> LoadFromDirectory()
    {
        var themes = new List<UserThemeDefinition>();
        
        if (!Directory.Exists(_themesDirectory))
        {
            _logger.LogInformation(
                "User themes directory does not exist: {Path}", 
                _themesDirectory);
            return themes;
        }
        
        var jsonFiles = Directory.EnumerateFiles(
            _themesDirectory, 
            "*.json", 
            SearchOption.TopDirectoryOnly);
        
        foreach (var file in jsonFiles)
        {
            try
            {
                var theme = LoadFromFile(file);
                if (theme != null)
                {
                    themes.Add(theme);
                    _logger.LogDebug(
                        "Loaded user theme '{Theme}' from {File}",
                        theme.Name, file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, 
                    "Failed to load theme from {File}", file);
                // Continue loading other themes
            }
        }
        
        return themes;
    }
    
    /// <summary>
    /// Loads a single theme from a file.
    /// </summary>
    /// <param name="filePath">Path to the JSON file</param>
    /// <returns>Theme definition or null if invalid</returns>
    public UserThemeDefinition? LoadFromFile(string filePath)
    {
        // Security: Validate path is within themes directory
        if (!IsPathSafe(filePath))
        {
            _logger.LogWarning(
                "Rejecting potentially unsafe theme path: {Path}",
                filePath);
            return null;
        }
        
        // Security: Check file size (prevent DoS with huge files)
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > 10 * 1024) // 10KB limit
        {
            _logger.LogWarning(
                "Theme file too large: {Size} bytes (max 10KB)",
                fileInfo.Length);
            return null;
        }
        
        // Read and parse
        var json = File.ReadAllText(filePath);
        
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 10  // Prevent stack overflow attacks
        };
        
        UserThemeDefinition? theme;
        try
        {
            theme = JsonSerializer.Deserialize<UserThemeDefinition>(
                json, options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Invalid JSON in theme file: {File}", filePath);
            return null;
        }
        
        if (theme == null)
        {
            return null;
        }
        
        // Validate
        var validation = _validator.Validate(theme);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Theme validation failed for {File}: {Errors}",
                filePath, string.Join(", ", validation.Errors));
            return null;
        }
        
        // Store file path for hot reload tracking
        theme.SourceFile = filePath;
        theme.LastModified = fileInfo.LastWriteTimeUtc;
        
        return theme;
    }
    
    /// <summary>
    /// Validates that a file path is within the themes directory.
    /// </summary>
    private bool IsPathSafe(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fullThemesDir = Path.GetFullPath(_themesDirectory);
        
        return fullPath.StartsWith(
            fullThemesDir, 
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Extended theme definition with user theme metadata.
/// </summary>
public sealed class UserThemeDefinition : ThemeDefinition
{
    /// <summary>
    /// Path to the source JSON file.
    /// </summary>
    public string? SourceFile { get; set; }
    
    /// <summary>
    /// Last modified timestamp of the source file.
    /// </summary>
    public DateTime LastModified { get; set; }
    
    /// <summary>
    /// Theme author (optional).
    /// </summary>
    public string? Author { get; init; }
    
    /// <summary>
    /// Parent theme name for inheritance.
    /// </summary>
    public string? Parent { get; init; }
    
    /// <summary>
    /// True if this is a user-defined theme (vs built-in).
    /// </summary>
    public bool IsUserTheme => true;
}
```

### 5.3 ThemeManager

Central service for applying themes at runtime.

```csharp
namespace Dotty.Themes;

/// <summary>
/// Manages theme application and switching at runtime.
/// </summary>
public interface IThemeManager
{
    /// <summary>
    /// Raised when the current theme changes.
    /// </summary>
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    
    /// <summary>
    /// Gets the currently active theme.
    /// </summary>
    IColorScheme CurrentTheme { get; }
    
    /// <summary>
    /// Gets the name of the current theme.
    /// </summary>
    string CurrentThemeName { get; }
    
    /// <summary>
    /// Applies a theme by name.
    /// </summary>
    /// <param name="themeName">Theme name or alias</param>
    /// <param name="animate">Whether to animate the transition</param>
    /// <returns>True if theme was applied successfully</returns>
    Task<bool> ApplyThemeAsync(string themeName, bool animate = false);
    
    /// <summary>
    /// Applies a theme directly.
    /// </summary>
    Task ApplyThemeAsync(IColorScheme theme, string name);
    
    /// <summary>
    /// Gets available themes from the registry.
    /// </summary>
    IReadOnlyList<ThemeInfo> GetAvailableThemes();
}

/// <summary>
/// Event args for theme change notifications.
/// </summary>
public sealed class ThemeChangedEventArgs : EventArgs
{
    public ThemeChangedEventArgs(
        string previousTheme, 
        string newTheme, 
        IColorScheme previousScheme,
        IColorScheme newScheme)
    {
        PreviousTheme = previousTheme;
        NewTheme = newTheme;
        PreviousScheme = previousScheme;
        NewScheme = newScheme;
    }
    
    public string PreviousTheme { get; }
    public string NewTheme { get; }
    public IColorScheme PreviousScheme { get; }
    public IColorScheme NewScheme { get; }
}

/// <summary>
/// Information about an available theme.
/// </summary>
public sealed record ThemeInfo(
    string Name,
    string Description,
    bool IsDark,
    bool IsUserTheme,
    string[] Aliases,
    string? Author,
    byte Opacity);

/// <summary>
/// Default implementation of IThemeManager.
/// </summary>
public sealed class ThemeManager : IThemeManager
{
    private static readonly Lazy<ThemeManager> _instance = 
        new(() => new ThemeManager());
    public static ThemeManager Current => _instance.Value;
    
    private readonly IThemeRegistry _registry;
    private readonly IServiceProvider? _serviceProvider;
    private IColorScheme _currentTheme;
    private string _currentThemeName;
    
    private ThemeManager()
    {
        _registry = ThemeRegistry.Current;
        _currentTheme = BuiltInThemes.DarkPlus;
        _currentThemeName = "DarkPlus";
    }
    
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    
    public IColorScheme CurrentTheme => _currentTheme;
    public string CurrentThemeName => _currentThemeName;
    
    public async Task<bool> ApplyThemeAsync(
        string themeName, 
        bool animate = false)
    {
        // Resolve theme name
        var theme = _registry.GetTheme(themeName);
        if (theme == null)
        {
            return false;
        }
        
        // Get the full resolved name
        var resolvedName = _registry.GetThemeName(themeName) ?? themeName;
        
        if (animate)
        {
            await AnimateThemeTransitionAsync(theme);
        }
        
        return ApplyThemeInternal(theme, resolvedName);
    }
    
    public Task ApplyThemeAsync(IColorScheme theme, string name)
    {
        ApplyThemeInternal(theme, name);
        return Task.CompletedTask;
    }
    
    private bool ApplyThemeInternal(IColorScheme theme, string name)
    {
        var previousTheme = _currentThemeName;
        var previousScheme = _currentTheme;
        
        _currentTheme = theme;
        _currentThemeName = name;
        
        // Notify listeners
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(
            previousTheme, name, previousScheme, theme));
        
        // Update ConfigBridge if available
        UpdateConfigBridge(theme);
        
        return true;
    }
    
    private void UpdateConfigBridge(IColorScheme theme)
    {
        // This updates the runtime configuration
        // without modifying the compiled config
        if (Application.Current is App app)
        {
            app.UpdateColorScheme(theme);
        }
    }
    
    private async Task AnimateThemeTransitionAsync(IColorScheme target)
    {
        // Implement smooth color transition
        // over 200-300ms for better UX
        var duration = TimeSpan.FromMilliseconds(250);
        var steps = 30;
        var delay = duration / steps;
        
        var start = _currentTheme;
        
        for (int i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var interpolated = InterpolateColors(start, target, t);
            UpdateConfigBridge(interpolated);
            await Task.Delay(delay);
        }
    }
    
    private IColorScheme InterpolateColors(
        IColorScheme a, 
        IColorScheme b, 
        float t)
    {
        // Create interpolated color scheme
        return new InterpolatedColorScheme(a, b, t);
    }
    
    public IReadOnlyList<ThemeInfo> GetAvailableThemes()
    {
        return _registry.GetAllThemes()
            .Select(t => new ThemeInfo(
                t.Name,
                t.Description,
                t.IsDark,
                t is UserThemeDefinition,
                t.Aliases,
                (t as UserThemeDefinition)?.Author,
                t.Opacity))
            .ToList();
    }
}
```

### 5.4 ThemeRegistry

Merges built-in and user themes with proper resolution.

```csharp
namespace Dotty.Themes;

/// <summary>
/// Registry that combines built-in and user themes.
/// </summary>
public interface IThemeRegistry
{
    /// <summary>
    /// Gets a theme by name or alias.
    /// </summary>
    IColorScheme? GetTheme(string name);
    
    /// <summary>
    /// Gets the canonical name for a theme.
    /// </summary>
    string? GetThemeName(string nameOrAlias);
    
    /// <summary>
    /// Gets all available themes.
    /// </summary>
    IReadOnlyList<ThemeDefinition> GetAllThemes();
    
    /// <summary>
    /// Gets only dark themes.
    /// </summary>
    IReadOnlyList<ThemeDefinition> GetDarkThemes();
    
    /// <summary>
    /// Gets only light themes.
    /// </summary>
    IReadOnlyList<ThemeDefinition> GetLightThemes();
    
    /// <summary>
    /// Refreshes the user theme cache.
    /// </summary>
    void RefreshUserThemes();
}

/// <summary>
/// Default implementation combining built-in and user themes.
/// </summary>
public sealed class ThemeRegistry : IThemeRegistry
{
    private static readonly Lazy<ThemeRegistry> _instance = 
        new(() => new ThemeRegistry());
    public static ThemeRegistry Current => _instance.Value;
    
    private readonly Dictionary<string, ThemeDefinition> _themes;
    private readonly UserThemeLoader _userLoader;
    private readonly object _lock = new();
    
    private ThemeRegistry()
    {
        _themes = new Dictionary<string, ThemeDefinition>(
            StringComparer.OrdinalIgnoreCase);
        _userLoader = new UserThemeLoader();
        
        LoadBuiltInThemes();
        LoadUserThemes();
    }
    
    private void LoadBuiltInThemes()
    {
        // Load all built-in themes from BuiltInThemeRegistry
        foreach (var theme in BuiltInThemeRegistry.AllThemes)
        {
            _themes[theme.Name] = theme;
            
            // Index aliases
            foreach (var alias in theme.Aliases)
            {
                _themes[alias] = theme;
            }
        }
    }
    
    private void LoadUserThemes()
    {
        var userThemes = _userLoader.LoadFromDirectory();
        
        foreach (var theme in userThemes)
        {
            // User themes can override built-in themes
            _themes[theme.Name] = theme;
            
            foreach (var alias in theme.Aliases)
            {
                _themes[alias] = theme;
            }
        }
    }
    
    public IColorScheme? GetTheme(string name)
    {
        lock (_lock)
        {
            if (_themes.TryGetValue(name, out var definition))
            {
                // Resolve inheritance if needed
                if (definition is UserThemeDefinition userTheme 
                    && !string.IsNullOrEmpty(userTheme.Parent))
                {
                    return ResolveInheritedTheme(userTheme);
                }
                
                return new JsonBackedColorScheme(definition.Name);
            }
            
            return null;
        }
    }
    
    public string? GetThemeName(string nameOrAlias)
    {
        lock (_lock)
        {
            if (_themes.TryGetValue(nameOrAlias, out var theme))
            {
                return theme.Name; // Return canonical name
            }
            return null;
        }
    }
    
    private IColorScheme ResolveInheritedTheme(UserThemeDefinition child)
    {
        var parentName = child.Parent!;
        
        // Get parent (built-in or user)
        var parent = GetTheme(parentName) as JsonBackedColorScheme;
        if (parent == null)
        {
            // Parent not found, return child as-is
            return new UserColorScheme(child);
        }
        
        // Check for circular inheritance
        var visited = new HashSet<string> { child.Name };
        var currentParent = child.Parent;
        while (!string.IsNullOrEmpty(currentParent))
        {
            if (!visited.Add(currentParent))
            {
                // Circular reference detected
                return new UserColorScheme(child);
            }
            
            // Get next parent
            if (_themes.TryGetValue(currentParent, out var parentDef)
                && parentDef is UserThemeDefinition parentUser)
            {
                currentParent = parentUser.Parent;
            }
            else
            {
                break;
            }
        }
        
        // Create merged color scheme
        return new InheritedColorScheme(child, parent);
    }
    
    public IReadOnlyList<ThemeDefinition> GetAllThemes()
    {
        lock (_lock)
        {
            // Return unique themes (by canonical name)
            return _themes.Values
                .DistinctBy(t => t.Name)
                .OrderBy(t => t.Name)
                .ToList();
        }
    }
    
    public IReadOnlyList<ThemeDefinition> GetDarkThemes() =>
        GetAllThemes().Where(t => t.IsDark).ToList();
    
    public IReadOnlyList<ThemeDefinition> GetLightThemes() =>
        GetAllThemes().Where(t => !t.IsDark).ToList();
    
    public void RefreshUserThemes()
    {
        lock (_lock)
        {
            // Remove old user themes
            var toRemove = _themes.Values
                .OfType<UserThemeDefinition>()
                .SelectMany(t => new[] { t.Name }.Concat(t.Aliases))
                .ToList();
            
            foreach (var key in toRemove)
            {
                _themes.Remove(key);
            }
            
            // Reload
            LoadUserThemes();
        }
    }
}
```

### 5.5 ThemeValidator

Validates theme JSON structure and color values.

```csharp
namespace Dotty.Themes.Validation;

/// <summary>
/// Validates theme definitions for correctness and security.
/// </summary>
public sealed class ThemeValidator
{
    private static readonly Regex ColorPattern = new(
        @
"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$",
        RegexOptions.Compiled);
    
    private static readonly Regex NamePattern = new(
        @"^[a-zA-Z][a-zA-Z0-9_-]{0,31}$",
        RegexOptions.Compiled);
    
    /// <summary>
    /// Validates a user theme definition.
    /// </summary>
    /// <returns>Validation result with success flag and any errors</returns>
    public ValidationResult Validate(UserThemeDefinition theme)
    {
        var errors = new List<string>();
        
        // Validate required fields
        if (string.IsNullOrWhiteSpace(theme.Name))
        {
            errors.Add("Theme name is required");
        }
        else if (!NamePattern.IsMatch(theme.Name))
        {
            errors.Add($"Invalid theme name '{theme.Name}'. " +
                "Must start with letter, alphanumeric/hyphens/underscores only, max 32 chars");
        }
        
        if (string.IsNullOrWhiteSpace(theme.Description))
        {
            errors.Add("Theme description is required");
        }
        else if (theme.Description.Length > 256)
        {
            errors.Add("Description must be 256 characters or less");
        }
        
        // Validate colors
        if (theme.Colors == null)
        {
            errors.Add("Colors section is required");
        }
        else
        {
            ValidateColor(theme.Colors.Background, "background", errors);
            ValidateColor(theme.Colors.Foreground, "foreground", errors);
            ValidateColor(theme.Colors.AnsiBlack, "ansiBlack", errors);
            ValidateColor(theme.Colors.AnsiRed, "ansiRed", errors);
            ValidateColor(theme.Colors.AnsiGreen, "ansiGreen", errors);
            ValidateColor(theme.Colors.AnsiYellow, "ansiYellow", errors);
            ValidateColor(theme.Colors.AnsiBlue, "ansiBlue", errors);
            ValidateColor(theme.Colors.AnsiMagenta, "ansiMagenta", errors);
            ValidateColor(theme.Colors.AnsiCyan, "ansiCyan", errors);
            ValidateColor(theme.Colors.AnsiWhite, "ansiWhite", errors);
            ValidateColor(theme.Colors.AnsiBrightBlack, "ansiBrightBlack", errors);
            ValidateColor(theme.Colors.AnsiBrightRed, "ansiBrightRed", errors);
            ValidateColor(theme.Colors.AnsiBrightGreen, "ansiBrightGreen", errors);
            ValidateColor(theme.Colors.AnsiBrightYellow, "ansiBrightYellow", errors);
            ValidateColor(theme.Colors.AnsiBrightBlue, "ansiBrightBlue", errors);
            ValidateColor(theme.Colors.AnsiBrightMagenta, "ansiBrightMagenta", errors);
            ValidateColor(theme.Colors.AnsiBrightCyan, "ansiBrightCyan", errors);
            ValidateColor(theme.Colors.AnsiBrightWhite, "ansiBrightWhite", errors);
        }
        
        // Validate opacity
        if (theme.Opacity > 100)
        {
            errors.Add("Opacity must be between 0 and 100");
        }
        
        // Validate aliases
        if (theme.Aliases != null)
        {
            if (theme.Aliases.Length > 10)
            {
                errors.Add("Maximum 10 aliases allowed");
            }
            
            foreach (var alias in theme.Aliases)
            {
                if (!NamePattern.IsMatch(alias))
                {
                    errors.Add($"Invalid alias '{alias}'");
                }
            }
        }
        
        // Validate parent reference
        if (!string.IsNullOrEmpty(theme.Parent))
        {
            if (!NamePattern.IsMatch(theme.Parent))
            {
                errors.Add($"Invalid parent theme name '{theme.Parent}'");
            }
        }
        
        return new ValidationResult(errors.Count == 0, errors);
    }
    
    private void ValidateColor(string color, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            errors.Add($"Color '{name}' is required");
            return;
        }
        
        if (!ColorPattern.IsMatch(color))
        {
            errors.Add($"Invalid color '{color}' for '{name}'. " +
                "Expected format: #RRGGBB or #AARRGGBB");
        }
    }
}

/// <summary>
/// Theme validation result.
/// </summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);
```

---

## 6. Theme Resolution Order

### 6.1 Resolution Priority

When a theme is requested by name, the system resolves it using the following priority order:

```
1. User themes by name/alias (highest priority)
2. Built-in themes by name/alias
3. Default to DarkPlus (lowest priority - fallback)
```

### 6.2 Resolution Flow

```
User requests theme "Ocean"
         │
         ▼
┌─────────────────────┐
│  Search User Themes │
│  by name "Ocean"    │
└──────────┬──────────┘
           │
    Found? │
     ┌─────┴─────┐
    YES         NO
     │           │
     ▼           ▼
┌──────────┐ ┌─────────────────────┐
│ Return   │ │ Search Built-in     │
│ Theme    │ │ Themes by "Ocean"   │
└──────────┘ └──────────┬────────────┘
                       │
                Found? │
                 ┌─────┴─────┐
                YES         NO
                 │           │
                 ▼           ▼
            ┌──────────┐ ┌──────────┐
            │ Return   │ │ Return   │
            │ Theme    │ │ DarkPlus │
            └──────────┘ │ (default)│
                       └──────────┘
```

---

## 7. Theme Inheritance

Theme inheritance allows users to create themes that extend existing themes, only overriding specific colors. The system prevents circular references and supports multi-level inheritance chains.

**Example:**
```json
{
  "name": "MyDracula",
  "description": "Dracula with custom background",
  "isDark": true,
  "parent": "Dracula",
  "colors": {
    "background": "#1A1A2E",
    "foreground": "#EAEAEA"
  }
}
```

---

## 8. API Design

The `IThemeManager` interface provides:
- `ThemeChanged` event for notifications
- `ApplyThemeAsync()` for runtime switching
- `AvailableThemes` property for discovery
- Animation support for smooth transitions

---

## 9. Hot Reload

FileSystemWatcher monitors `~/.config/dotty/themes/` with:
- 300ms debouncing to prevent rapid-fire reloads
- Validation before application
- Error notifications for invalid themes
- Automatic theme refresh when current theme is edited

---

## 10. Security

Safety measures include:
- Path validation (prevent `../` attacks)
- File size limits (10KB max)
- JSON depth limits (10 levels)
- Color validation (regex for hex only)
- Name validation (alphanumeric + hyphens/underscores)

---

## 11. Backward Compatibility

The system maintains full compatibility:
- `BuiltInThemes` API unchanged
- Source generator unaffected
- `ConfigBridge` continues working
- User themes are purely additive

---

## 12. Implementation Roadmap

| Phase | Feature | Timeline |
|-------|---------|----------|
| 1 | Static loading from disk | v1.x |
| 2 | Hot reload with FileSystemWatcher | v1.x |
| 3 | Theme inheritance | v1.x |
| 4 | Import from other formats | v2.x |

---

## Summary

This architecture provides a robust, secure, and extensible system for user-defined themes in Dotty. The phased approach allows incremental delivery while maintaining backward compatibility throughout.
