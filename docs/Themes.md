# Dotty Themes

Dotty comes with a curated set of popular terminal themes. This guide documents all built-in themes with their color values and characteristics.

## Quick Reference

```csharp
using Dotty.Abstractions.Themes;

// Dark themes
public IColorScheme? Colors => BuiltInThemes.DarkPlus;       // VS Code Dark+ (default)
public IColorScheme? Colors => BuiltInThemes.Dracula;        // Vibrant dark
public IColorScheme? Colors => BuiltInThemes.OneDark;        // Atom-inspired
public IColorScheme? Colors => BuiltInThemes.GruvboxDark;    // Warm dark
public IColorScheme? Colors => BuiltInThemes.CatppuccinMocha; // Pastel dark
public IColorScheme? Colors => BuiltInThemes.TokyoNight;      // Deep blues

// Light themes
public IColorScheme? Colors => BuiltInThemes.LightPlus;      // VS Code Light+
public IColorScheme? Colors => BuiltInThemes.OneLight;       // Balanced light
public IColorScheme? Colors => BuiltInThemes.GruvboxLight;   // Warm light
public IColorScheme? Colors => BuiltInThemes.CatppuccinLatte; // Pastel light
    public IColorScheme? Colors => BuiltInThemes.SolarizedLight; // Low contrast
```

---

## Transparency

Dotty supports window transparency/opacity control through the theming system. This allows you to create translucent terminal windows that blend with the desktop background.

### Opacity Range

- **100** = Fully opaque (default)
- **0** = Fully transparent
- **Recommended values**: 85-95 for subtle effect

### Creating a Translucent Theme

```csharp
using Dotty.Abstractions.Themes;

/// <summary>
/// Translucent dark theme with 85% opacity (15% transparent)
/// </summary>
public class TranslucentDarkTheme : DarkPlusTheme
{
    public override byte Opacity => 85;
}

// Use it in your config
public partial class MyConfig : IDottyConfig
{
    public IColorScheme? Colors => new TranslucentDarkTheme();
}
```

### Time-Based Opacity

You can make the terminal automatically adjust opacity based on time of day:

```csharp
public class AdaptiveOpacityTheme : DarkPlusTheme
{
    // More transparent at night (90%), fully opaque during day
    public override byte Opacity => DateTime.Now.Hour is >= 20 or < 6 ? 90 : 100;
}
```

### Platform Support Notes

The transparency feature uses Avalonia's `Window.Opacity` property, which affects the **entire window uniformly**. For true "see-through" effects with blurred background:

| Platform | Support | Notes |
|----------|---------|-------|
| Windows | Partial | DWM blur requires platform-specific APIs |
| macOS | Partial | NSVisualEffectView for blur effects |
| Linux | Varies | Depends on compositor (KDE, GNOME, etc.) |

### Accessibility Considerations

- **Recommended range**: 85-95 opacity (5-15% transparent)
- Lower values may hurt readability, especially with busy backgrounds
- Consider using darker backgrounds with transparency for better contrast
- Test your configuration with your typical desktop background

### Built-in Themes with Transparency

All built-in themes default to `Opacity = 100` (fully opaque). To use transparency, create a custom theme that overrides the `Opacity` property.

```csharp
// Example: Make any built-in theme translucent
public class TranslucentDracula : DraculaTheme
{
    public override byte Opacity => 90; // 10% transparent
}

public class TranslucentLight : LightPlusTheme
{
    public override byte Opacity => 95; // 5% transparent
}
```

---

## Dark Themes

### DarkPlus (Default)

**VS Code Dark+** - The default theme for Dotty, matching VS Code's default dark theme. Provides excellent readability and familiarity for VS Code users.

```csharp
public IColorScheme? Colors => BuiltInThemes.DarkPlus;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Dark Gray | `#1E1E1E` | `0xFF1E1E1E` |
| Foreground | Light Gray | `#D4D4D4` | `0xFFD4D4D4` |
| Black | Pure Black | `#000000` | `0xFF000000` |
| Red | Red | `#CD3131` | `0xFFCD3131` |
| Green | Teal Green | `#0DBC79` | `0xFF0DBC79` |
| Yellow | Yellow | `#E5E510` | `0xFFE5E510` |
| Blue | Blue | `#2472C8` | `0xFF2472C8` |
| Magenta | Purple | `#BC3FBC` | `0xFFBC3FBC` |
| Cyan | Cyan | `#11A8CD` | `0xFF11A8CD` |
| White | Light Gray | `#E5E5E5` | `0xFFE5E5E5` |
| Bright Black | Gray | `#666666` | `0xFF666666` |
| Bright Red | Bright Red | `#F14C4C` | `0xFFF14C4C` |
| Bright Green | Bright Green | `#23D18B` | `0xFF23D18B` |
| Bright Yellow | Bright Yellow | `#F5F543` | `0xFFF5F543` |
| Bright Blue | Bright Blue | `#3B8EEA` | `0xFF3B8EEA` |
| Bright Magenta | Bright Purple | `#D670D6` | `0xFFD670D6` |
| Bright Cyan | Bright Cyan | `#29B8DB` | `0xFF29B8DB` |
| Bright White | White | `#FFFFFF` | `0xFFFFFFFF` |

**Characteristics:**
- Neutral gray background
- Vibrant but not oversaturated colors
- Excellent readability
- Familiar to VS Code users
- Good for general-purpose use

---

### Dracula

**Dracula** - One of the most popular dark themes in the developer community. Features a dark purple background with bright, saturated colors.

```csharp
public IColorScheme? Colors => BuiltInThemes.Dracula;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Dark Purple | `#282A36` | `0xFF282A36` |
| Foreground | Off White | `#F8F8F2` | `0xFFF8F8F2` |
| Black | Darker Purple | `#21222C` | `0xFF21222C` |
| Red | Pink | `#FF5555` | `0xFFFF5555` |
| Green | Bright Green | `#50FA7B` | `0xFF50FA7B` |
| Yellow | Light Yellow | `#F1FA8C` | `0xFFF1FA8C` |
| Blue | Purple | `#BD93F9` | `0xFFBD93F9` |
| Magenta | Hot Pink | `#FF79C6` | `0xFFFF79C6` |
| Cyan | Light Blue | `#8BE9FD` | `0xFF8BE9FD` |
| White | Off White | `#F8F8F2` | `0xFFF8F8F2` |
| Bright Black | Comment Gray | `#6272A4` | `0xFF6272A4` |
| Bright Red | Light Pink | `#FF6E6E` | `0xFFFF6E6E` |
| Bright Green | Light Green | `#69FF94` | `0xFF69FF94` |
| Bright Yellow | Light Yellow | `#FFFFA5` | `0xFFFFFFA5` |
| Bright Blue | Light Purple | `#D6ACFF` | `0xFFD6ACFF` |
| Bright Magenta | Light Pink | `#FF92DF` | `0xFFFF92DF` |
| Bright Cyan | Light Blue | `#A4FFFF` | `0xFFA4FFFF` |
| Bright White | Pure White | `#FFFFFF` | `0xFFFFFFFF` |

**Characteristics:**
- Distinctive purple-tinted background
- Highly saturated accent colors
- Excellent for code syntax highlighting
- Large community support
- Available for many editors and tools

---

### OneDark

**One Dark** - Inspired by the Atom editor. A subtle dark theme with muted, professional colors.

```csharp
public IColorScheme? Colors => BuiltInThemes.OneDark;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Dark Blue-Gray | `#282C34` | `0xFF282C34` |
| Foreground | Gray-White | `#ABB2BF` | `0xFFABB2BF` |
| Black | Darker Blue-Gray | `#282C34` | `0xFF282C34` |
| Red | Coral Red | `#E06C75` | `0xFFE06C75` |
| Green | Sage Green | `#98C379` | `0xFF98C379` |
| Yellow | Tan Yellow | `#E5C07B` | `0xFFE5C07B` |
| Blue | Sky Blue | `#61AFEF` | `0xFF61AFEF` |
| Magenta | Purple | `#C678DD` | `0xFFC678DD` |
| Cyan | Teal | `#56B6C2` | `0xFF56B6C2` |
| White | Gray | `#ABB2BF` | `0xFFABB2BF` |
| Bright Black | Medium Gray | `#5C6370` | `0xFF5C6370` |
| Bright Red | Light Coral | `#FF7A85` | `0xFFFF7A85` |
| Bright Green | Light Sage | `#B5E090` | `0xFFB5E090` |
| Bright Yellow | Light Tan | `#FFD58F` | `0xFFFFD58F` |
| Bright Blue | Light Sky | `#8CCBFF` | `0xFF8CCBFF` |
| Bright Magenta | Light Purple | `#E599FF` | `0xFFE599FF` |
| Bright Cyan | Light Teal | `#89DDFF` | `0xFF89DDFF` |
| Bright White | White | `#FFFFFF` | `0xFFFFFFFF` |

**Characteristics:**
- Muted, professional appearance
- Blue-tinted background
- Balanced color saturation
- Easy on the eyes for long sessions
- Inspired by Atom editor

---

### GruvboxDark

**Gruvbox Dark** - Warm dark theme with earthy tones. Designed to be easy on the eyes for long coding sessions.

```csharp
public IColorScheme? Colors => BuiltInThemes.GruvboxDark;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Dark Brown | `#282828` | `0xFF282828` |
| Foreground | Warm Beige | `#EBDBB2` | `0xFFEBDBB2` |
| Black | Dark Brown | `#282828` | `0xFF282828` |
| Red | Muted Red | `#CC241D` | `0xFFCC241D` |
| Green | Olive Green | `#98971A` | `0xFF98971A` |
| Yellow | Mustard | `#D79921` | `0xFFD79921` |
| Blue | Teal Blue | `#458588` | `0xFF458588` |
| Magenta | Mauve | `#B16286` | `0xFFB16286` |
| Cyan | Sage | `#689D6A` | `0xFF689D6A` |
| White | Light Beige | `#A89984` | `0xFFA89984` |
| Bright Black | Gray-Brown | `#928374` | `0xFF928374` |
| Bright Red | Bright Red | `#FB4934` | `0xFFFB4934` |
| Bright Green | Bright Green | `#B8BB26` | `0xFFB8BB26` |
| Bright Yellow | Bright Yellow | `#FABD2F` | `0xFFFABD2F` |
| Bright Blue | Bright Teal | `#83A598` | `0xFF83A598` |
| Bright Magenta | Bright Mauve | `#D3869B` | `0xFFD3869B` |
| Bright Cyan | Bright Sage | `#8EC07C` | `0xFF8EC07C` |
| Bright White | Cream | `#FBF1C7` | `0xFFFBF1C7` |

**Characteristics:**
- Warm, earthy background
- Low contrast for reduced eye strain
- Vintage/retro aesthetic
- Excellent for long coding sessions
- Available in dark and light variants

---

### CatppuccinMocha

**Catppuccin Mocha** - Soothing dark theme with pastel colors. The dark variant of the Catppuccin pastel theme.

```csharp
public IColorScheme? Colors => BuiltInThemes.CatppuccinMocha;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Deep Purple | `#1E1E2E` | `0xFF1E1E2E` |
| Foreground | Lavender | `#CDD6F4` | `0xFFCDD6F4` |
| Black | Dark Purple | `#45475A` | `0xFF45475A` |
| Red | Pink | `#F38BA8` | `0xFFF38BA8` |
| Green | Mint | `#A6E3A1` | `0xFFA6E3A1` |
| Yellow | Cream Yellow | `#F9E2AF` | `0xFFF9E2AF` |
| Blue | Periwinkle | `#89B4FA` | `0xFF89B4FA` |
| Magenta | Pink | `#F5C2E7` | `0xFFF5C2E7` |
| Cyan | Teal | `#94E2D5` | `0xFF94E2D5` |
| White | Lavender Gray | `#BAC2DE` | `0xFFBAC2DE` |
| Bright Black | Gray Purple | `#585B70` | `0xFF585B70` |
| Bright Red | Light Pink | `#FFA1C1` | `0xFFFFA1C1` |
| Bright Green | Light Mint | `#B9F0B4` | `0xFFB9F0B4` |
| Bright Yellow | Light Cream | `#FFEFA1` | `0xFFFFEFA1` |
| Bright Blue | Light Periwinkle | `#A3C9FF` | `0xFFA3C9FF` |
| Bright Magenta | Light Pink | `#FFDBF7` | `0xFFFFDBF7` |
| Bright Cyan | Light Teal | `#AAEFDE` | `0xFFAAEFDE` |
| Bright White | White | `#FFFFFF` | `0xFFFFFFFF` |

**Characteristics:**
- Pastel colors on dark purple background
- Soft, soothing appearance
- Middle ground between high and low contrast
- Great for reducing eye fatigue
- Modern and aesthetically pleasing

---

### TokyoNight

**Tokyo Night** - Modern dark theme with deep blues and purples. Celebrates the lights of Downtown Tokyo at night.

```csharp
public IColorScheme? Colors => BuiltInThemes.TokyoNight;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Deep Blue | `#1A1B26` | `0xFF1A1B26` |
| Foreground | Blue-Gray | `#A9B1D6` | `0xFFA9B1D6` |
| Black | Dark Blue | `#414868` | `0xFF414868` |
| Red | Pink Red | `#F7768E` | `0xFFF7768E` |
| Green | Mint | `#73DACA` | `0xFF73DACA` |
| Yellow | Orange Yellow | `#E0AF68` | `0xFFE0AF68` |
| Blue | Cornflower | `#7AA2F7` | `0xFF7AA2F7` |
| Magenta | Lavender | `#BB9AF7` | `0xFFBB9AF7` |
| Cyan | Light Blue | `#7DCFFF` | `0xFF7DCFFF` |
| White | Blue-Gray | `#787C99` | `0xFF787C99` |
| Bright Black | Gray Blue | `#565F89` | `0xFF565F89` |
| Bright Red | Light Pink | `#FF8EA0` | `0xFFFF8EA0` |
| Bright Green | Light Mint | `#8BECC8` | `0xFF8BECC8` |
| Bright Yellow | Light Orange | `#FFD88A` | `0xFFFFD88A` |
| Bright Blue | Light Cornflower | `#9AB8FF` | `0xFF9AB8FF` |
| Bright Magenta | Light Lavender | `#D4BFFF` | `0xFFD4BFFF` |
| Bright Cyan | Light Sky | `#9ED7FF` | `0xFF9ED7FF` |
| Bright White | Light Blue | `#C0CAF5` | `0xFFC0CAF5` |

**Characteristics:**
- Deep blue-purple background
- Modern, sleek appearance
- Inspired by Tokyo city lights
- Vibrant but not overwhelming colors
- Popular among modern developers

---

## Light Themes

### LightPlus

**VS Code Light+** - A clean, bright theme suitable for well-lit environments. The light counterpart to DarkPlus.

```csharp
public IColorScheme? Colors => BuiltInThemes.LightPlus;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | White | `#FFFFFF` | `0xFFFFFFFF` |
| Foreground | Black | `#000000` | `0xFF000000` |
| Black | Black | `#000000` | `0xFF000000` |
| Red | Red | `#CD3131` | `0xFFCD3131` |
| Green | Green | `#00BC00` | `0xFF00BC00` |
| Yellow | Olive | `#949800` | `0xFF949800` |
| Blue | Blue | `#0451A5` | `0xFF0451A5` |
| Magenta | Magenta | `#BC05BC` | `0xFFBC05BC` |
| Cyan | Teal | `#0598BC` | `0xFF0598BC` |
| White | Gray | `#555555` | `0xFF555555` |
| Bright Black | Gray | `#666666` | `0xFF666666` |
| Bright Red | Bright Red | `#F14C4C` | `0xFFF14C4C` |
| Bright Green | Bright Green | `#16C60C` | `0xFF16C60C` |
| Bright Yellow | Olive | `#B5BA00` | `0xFFB5BA00` |
| Bright Blue | Blue | `#0A6BC8` | `0xFF0A6BC8` |
| Bright Magenta | Magenta | `#BC05BC` | `0xFFBC05BC` |
| Bright Cyan | Teal | `#0598BC` | `0xFF0598BC` |
| Bright White | Gray | `#A5A5A5` | `0xFFA5A5A5` |

**Characteristics:**
- Clean white background
- Good contrast for readability
- Familiar to VS Code users
- Suitable for bright environments
- Professional appearance

---

### OneLight

**One Light** - The light counterpart to One Dark. A balanced light theme with professional, muted colors.

```csharp
public IColorScheme? Colors => BuiltInThemes.OneLight;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Light Gray | `#FAFAFA` | `0xFFFAFAFA` |
| Foreground | Dark Gray | `#383A42` | `0xFF383A42` |
| Black | Dark Gray | `#383A42` | `0xFF383A42` |
| Red | Red | `#E45649` | `0xFFE45649` |
| Green | Green | `#50A14F` | `0xFF50A14F` |
| Yellow | Brown | `#C18401` | `0xFFC18401` |
| Blue | Blue | `#4078F2` | `0xFF4078F2` |
| Magenta | Purple | `#A626A4` | `0xFFA626A4` |
| Cyan | Teal | `#0184BC` | `0xFF0184BC` |
| White | Gray | `#A0A1A7` | `0xFFA0A1A7` |
| Bright Black | Medium Gray | `#4F525D` | `0xFF4F525D` |
| Bright Red | Light Red | `#FF6E66` | `0xFFFF6E66` |
| Bright Green | Light Green | `#6BC468` | `0xFF6BC468` |
| Bright Yellow | Orange | `#D9940F` | `0xFFD9940F` |
| Bright Blue | Light Blue | `#6394FF` | `0xFF6394FF` |
| Bright Magenta | Light Purple | `#C053BE` | `0xFFC053BE` |
| Bright Cyan | Light Teal | `#38B7F0` | `0xFF38B7F0` |
| Bright White | White | `#FFFFFF` | `0xFFFFFFFF` |

**Characteristics:**
- Off-white background (not pure white)
- Muted, professional appearance
- Less harsh than pure white themes
- Good for users who prefer light themes but want something refined

---

### GruvboxLight

**Gruvbox Light** - Warm light theme. The light variant of the popular Gruvbox theme.

```csharp
public IColorScheme? Colors => BuiltInThemes.GruvboxLight;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Cream | `#FBF1C7` | `0xFFFBF1C7` |
| Foreground | Dark Brown | `#3C3836` | `0xFF3C3836` |
| Black | Dark Brown | `#3C3836` | `0xFF3C3836` |
| Red | Red | `#CC241D` | `0xFFCC241D` |
| Green | Olive | `#98971A` | `0xFF98971A` |
| Yellow | Yellow | `#D79921` | `0xFFD79921` |
| Blue | Teal | `#458588` | `0xFF458588` |
| Magenta | Purple | `#B16286` | `0xFFB16286` |
| Cyan | Green | `#689D6A` | `0xFF689D6A` |
| White | Brown | `#7C6F64` | `0xFF7C6F64` |
| Bright Black | Brown | `#928374` | `0xFF928374` |
| Bright Red | Dark Red | `#9D0006` | `0xFF9D0006` |
| Bright Green | Dark Olive | `#79740E` | `0xFF79740E` |
| Bright Yellow | Brown | `#B57614` | `0xFFB57614` |
| Bright Blue | Dark Teal | `#076678` | `0xFF076678` |
| Bright Magenta | Dark Purple | `#8F3F71` | `0xFF8F3F71` |
| Bright Cyan | Dark Green | `#427B58` | `0xFF427B58` |
| Bright White | Darker Brown | `#282828` | `0xFF282828` |

**Characteristics:**
- Warm cream background
- Earthy, vintage aesthetic
- Unique among light themes
- Very easy on the eyes
- Consistent with Gruvbox dark variant

---

### CatppuccinLatte

**Catppuccin Latte** - Light counterpart to Catppuccin Mocha. Features soft, warm colors with excellent readability.

```csharp
public IColorScheme? Colors => BuiltInThemes.CatppuccinLatte;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Light Lavender | `#EFF1F5` | `0xFFEFF1F5` |
| Foreground | Dark Gray | `#4C4F69` | `0xFF4C4F69` |
| Black | Gray Purple | `#5C5F77` | `0xFF5C5F77` |
| Red | Red | `#D20F39` | `0xFFD20F39` |
| Green | Green | `#40A02B` | `0xFF40A02B` |
| Yellow | Orange | `#DF8E1D` | `0xFFDF8E1D` |
| Blue | Blue | `#1E66F5` | `0xFF1E66F5` |
| Magenta | Pink | `#EA76CB` | `0xFFEA76CB` |
| Cyan | Teal | `#179299` | `0xFF179299` |
| White | Gray | `#ACB0BE` | `0xFFACB0BE` |
| Bright Black | Gray | `#6C6F85` | `0xFF6C6F85` |
| Bright Red | Light Red | `#EE324C` | `0xFFEE324C` |
| Bright Green | Light Green | `#56C150` | `0xFF56C150` |
| Bright Yellow | Light Orange | `#F0AB39` | `0xFFF0AB39` |
| Bright Blue | Light Blue | `#4C89FF` | `0xFF4C89FF` |
| Bright Magenta | Light Pink | `#F495DA` | `0xFFF495DA` |
| Bright Cyan | Light Teal | `#2AB6B2` | `0xFF2AB6B2` |
| Bright White | Light Gray | `#CCD0DA` | `0xFFCCD0DA` |

**Characteristics:**
- Light lavender-tinted background
- Pastel accent colors
- Soft, warm appearance
- Easy on the eyes
- Consistent with Catppuccin dark variant

---

### SolarizedLight

**Solarized Light** - Carefully selected low-contrast colors designed to reduce eye strain.

```csharp
public IColorScheme? Colors => BuiltInThemes.SolarizedLight;
```

| Role | Color | Hex | ARGB |
|------|-------|-----|------|
| Background | Beige | `#FDF6E3` | `0xFFFDF6E3` |
| Foreground | Gray Blue | `#657B83` | `0xFF657B83` |
| Black | Dark Blue | `#073642` | `0xFF073642` |
| Red | Red | `#DC322F` | `0xFFDC322F` |
| Green | Green | `#859900` | `0xFF859900` |
| Yellow | Yellow | `#B58900` | `0xFFB58900` |
| Blue | Blue | `#268BD2` | `0xFF268BD2` |
| Magenta | Magenta | `#D33682` | `0xFFD33682` |
| Cyan | Teal | `#2AA198` | `0xFF2AA198` |
| White | Beige | `#EEE8D5` | `0xFFEEE8D5` |
| Bright Black | Dark Blue | `#002B36` | `0xFF002B36` |
| Bright Red | Orange | `#CB4B16` | `0xFFCB4B16` |
| Bright Green | Gray | `#586E75` | `0xFF586E75` |
| Bright Yellow | Gray Blue | `#657B83` | `0xFF657B83` |
| Bright Blue | Gray | `#839496` | `0xFF839496` |
| Bright Magenta | Purple | `#6C71C4` | `0xFF6C71C4` |
| Bright Cyan | Gray | `#93A1A1` | `0xFF93A1A1` |
| Bright White | Beige | `#FDF6E3` | `0xFFFDF6E3` |

**Characteristics:**
- Distinctive beige background
- Low contrast design
- Scientifically selected colors
- Consistent across light and dark variants
- Very popular among developers concerned with eye strain

---

## Creating Custom Themes

### Basic Custom Theme

```csharp
using Dotty.Abstractions.Themes;

public class MyCustomTheme : ColorSchemeBase
{
    public MyCustomTheme() : base(
        background: 0xFF1A1B26,
        foreground: 0xFFA9B1D6,
        // ... all 16 ANSI colors
    )
    {
    }
}

// Use it in your config
public partial class MyConfig : IDottyConfig
{
    public IColorScheme? Colors => new MyCustomTheme();
}
```

### Converting from Hex

```csharp
using static Dotty.Abstractions.Themes.ColorSchemeBase;

public class MyHexTheme : ColorSchemeBase
{
    public MyHexTheme() : base(
        background: FromHex("#1A1B26"),
        foreground: FromHex("#A9B1D6"),
        // ... etc
    )
    {
    }
}
```

### Accessibility Considerations

When creating custom themes, ensure adequate contrast ratios:

```csharp
using static Dotty.Abstractions.Themes.ColorSchemeBase;

// WCAG AA requires at least 4.5:1 for normal text
double contrast = CalculateContrastRatio(foreground, background);
if (contrast < 4.5)
{
    // Warning: Low contrast
}
```

**Recommended minimum contrast ratios:**
- Normal text: 4.5:1 (WCAG AA)
- Large text: 3:1
- Enhanced (AAA): 7:1

### Contributing New Themes

If you'd like to contribute a new built-in theme:

1. Create a new class in `/src/Dotty.Abstractions/Themes/`
2. Inherit from `ColorSchemeBase`
3. Add the theme to `BuiltInThemes.cs`
4. Document it in this file
5. Ensure colors are from official theme sources

---

## See Also

- [Configuration Guide](Configuration.md) - Full configuration documentation
- [Sample Config](../samples/Config.cs) - Complete examples
