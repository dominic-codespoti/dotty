using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dotty.Abstractions.Themes;

namespace Dotty.App.Services;

/// <summary>
/// Validates theme JSON files for correctness and accessibility.
/// Checks required fields, color formats, ANSI palette completeness, and contrast ratios.
/// </summary>
public sealed class ThemeValidator
{
    // Maximum file size: 10KB
    private const long MaxFileSizeBytes = 10 * 1024;

    // Minimum contrast ratio for WCAG AA compliance
    private const double MinContrastRatio = 4.5;

    // Hex color regex patterns
    private static readonly Regex HexColorRegex = new(
        @"^(?:0x|#)?[0-9A-Fa-f]{6}$|^(?:0x|#)?[0-9A-Fa-f]{8}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates a new ThemeValidator.
    /// </summary>
    public ThemeValidator()
    {
    }

    /// <summary>
    /// Validates a theme JSON file.
    /// </summary>
    /// <param name="filePath">Path to the JSON file</param>
    /// <returns>Validation result with details</returns>
    public ValidationResult ValidateFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return ValidationResult.Failure($"File not found: {filePath}");
        }

        // Check file size
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            return ValidationResult.Failure(
                $"File size ({fileInfo.Length} bytes) exceeds maximum allowed size ({MaxFileSizeBytes} bytes)");
        }

        string json;
        try
        {
            json = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure($"Failed to read file: {ex.Message}");
        }

        return ValidateJson(json, filePath);
    }

    /// <summary>
    /// Validates a theme JSON string.
    /// </summary>
    /// <param name="json">The JSON string to validate</param>
    /// <param name="sourceName">Name of the source (for error messages)</param>
    /// <returns>Validation result with details</returns>
    public ValidationResult ValidateJson(string json, string sourceName = "<json>")
    {
        var result = new ValidationResult();

        // Try to parse as ThemeDefinition
        ThemeDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<ThemeDefinition>(json, ThemeJsonContext.Default.ThemeDefinition);
        }
        catch (JsonException ex)
        {
            // Try ThemeRoot format
            try
            {
                var root = JsonSerializer.Deserialize<ThemeRoot>(json, ThemeJsonContext.Default.ThemeRoot);
                if (root?.Themes?.Length > 0)
                {
                    definition = root.Themes[0];
                }
                else
                {
                    return ValidationResult.Failure($"Failed to parse JSON: {ex.Message}");
                }
            }
            catch
            {
                return ValidationResult.Failure($"Failed to parse JSON: {ex.Message}");
            }
        }

        if (definition == null)
        {
            return ValidationResult.Failure("Failed to deserialize theme definition");
        }

        // Validate required fields
        ValidateRequiredFields(definition, result, sourceName);

        // Validate colors if present
        if (definition.Colors != null)
        {
            ValidateColors(definition.Colors, result, sourceName);
        }

        // Calculate overall validity
        result.IsValid = !result.Errors.Any();

        return result;
    }

    /// <summary>
    /// Validates required fields in the theme definition.
    /// </summary>
    private static void ValidateRequiredFields(ThemeDefinition definition, ValidationResult result, string sourceName)
    {
        // Check canonicalName
        if (string.IsNullOrWhiteSpace(definition.CanonicalName))
        {
            result.AddError($"[{sourceName}] Missing required field: canonicalName");
        }
        else if (!IsValidIdentifier(definition.CanonicalName))
        {
            result.AddWarning($"[{sourceName}] canonicalName '{definition.CanonicalName}' should be PascalCase (e.g., 'MyCustomTheme')");
        }

        // Check displayName (optional but recommended)
        if (string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            result.AddWarning($"[{sourceName}] Missing recommended field: displayName");
        }

        // Check colors
        if (definition.Colors == null)
        {
            result.AddError($"[{sourceName}] Missing required field: colors");
        }
    }

    /// <summary>
    /// Validates color fields in the theme.
    /// </summary>
    private void ValidateColors(ThemeColors colors, ValidationResult result, string sourceName)
    {
        // Validate background
        if (string.IsNullOrWhiteSpace(colors.Background))
        {
            result.AddError($"[{sourceName}] Missing required color: colors.background");
        }
        else if (!IsValidHexColor(colors.Background))
        {
            result.AddError($"[{sourceName}] Invalid hex format for background: '{colors.Background}'. Expected #RRGGBB or #AARRGGBB");
        }

        // Validate foreground
        if (string.IsNullOrWhiteSpace(colors.Foreground))
        {
            result.AddError($"[{sourceName}] Missing required color: colors.foreground");
        }
        else if (!IsValidHexColor(colors.Foreground))
        {
            result.AddError($"[{sourceName}] Invalid hex format for foreground: '{colors.Foreground}'. Expected #RRGGBB or #AARRGGBB");
        }

        // Validate opacity range
        if (colors.Opacity < 0.0 || colors.Opacity > 1.0)
        {
            result.AddError($"[{sourceName}] Invalid opacity value: {colors.Opacity}. Must be between 0.0 and 1.0");
        }

        // Validate ANSI colors
        if (colors.Ansi == null)
        {
            result.AddError($"[{sourceName}] Missing required field: colors.ansi");
        }
        else if (colors.Ansi.Length != 16)
        {
            result.AddError($"[{sourceName}] ANSI palette must contain exactly 16 colors, found {colors.Ansi.Length}");
        }
        else
        {
            // Validate each ANSI color
            for (int i = 0; i < colors.Ansi.Length; i++)
            {
                var color = colors.Ansi[i];
                if (string.IsNullOrWhiteSpace(color))
                {
                    result.AddError($"[{sourceName}] Missing ANSI color at index {i}");
                }
                else if (!IsValidHexColor(color))
                {
                    result.AddError($"[{sourceName}] Invalid hex format for ANSI color at index {i}: '{color}'");
                }
            }
        }

        // Check contrast ratio
        if (!string.IsNullOrWhiteSpace(colors.Background) && !string.IsNullOrWhiteSpace(colors.Foreground))
        {
            try
            {
                var bgColor = HexToUint(colors.Background);
                var fgColor = HexToUint(colors.Foreground);
                var contrast = CalculateContrastRatio(fgColor, bgColor);

                if (contrast < MinContrastRatio)
                {
                    result.AddWarning($"[{sourceName}] Low contrast ratio: {contrast:F2}:1 (WCAG AA requires at least {MinContrastRatio}:1)");
                }

                result.ContrastRatio = contrast;
            }
            catch (Exception ex)
            {
                result.AddWarning($"[{sourceName}] Could not calculate contrast ratio: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Checks if a string is a valid hex color.
    /// Supports: #RRGGBB, #AARRGGBB, 0xRRGGBB, 0xAARRGGBB, RRGGBB, AARRGGBB
    /// </summary>
    public static bool IsValidHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        return HexColorRegex.IsMatch(hex.Trim());
    }

    /// <summary>
    /// Converts a hex color string to ARGB uint.
    /// </summary>
    private static uint HexToUint(string hex)
    {
        var span = hex.AsSpan().Trim();
        
        // Remove prefix if present
        if (span.Length > 1 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
            span = span.Slice(2);
        else if (span.Length > 0 && span[0] == '#')
            span = span.Slice(1);

        // Parse based on length
        return span.Length switch
        {
            6 => 0xFF000000u | (uint)Convert.ToInt32(span.ToString(), 16),
            8 => (uint)Convert.ToInt32(span.ToString(), 16),
            _ => throw new ArgumentException($"Invalid hex color format: {hex}")
        };
    }

    /// <summary>
    /// Calculates the contrast ratio between two colors (WCAG formula).
    /// </summary>
    public static double CalculateContrastRatio(uint foreground, uint background)
    {
        double fgLuminance = GetRelativeLuminance(foreground);
        double bgLuminance = GetRelativeLuminance(background);

        double lighter = Math.Max(fgLuminance, bgLuminance);
        double darker = Math.Min(fgLuminance, bgLuminance);

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Gets the relative luminance of a color.
    /// </summary>
    private static double GetRelativeLuminance(uint argb)
    {
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8) & 0xFF);
        byte b = (byte)(argb & 0xFF);

        double rsRGB = r / 255.0;
        double gsRGB = g / 255.0;
        double bsRGB = b / 255.0;

        double rLinear = rsRGB <= 0.03928 ? rsRGB / 12.92 : Math.Pow((rsRGB + 0.055) / 1.055, 2.4);
        double gLinear = gsRGB <= 0.03928 ? gsRGB / 12.92 : Math.Pow((gsRGB + 0.055) / 1.055, 2.4);
        double bLinear = bsRGB <= 0.03928 ? bsRGB / 12.92 : Math.Pow((bsRGB + 0.055) / 1.055, 2.4);

        return 0.2126 * rLinear + 0.7152 * gLinear + 0.0722 * bLinear;
    }

    /// <summary>
    /// Checks if a string is a valid PascalCase identifier.
    /// </summary>
    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Should start with uppercase letter
        if (!char.IsUpper(name[0]))
            return false;

        // Should only contain letters and digits
        return name.All(c => char.IsLetterOrDigit(c));
    }
}

/// <summary>
/// Represents the result of theme validation.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>
    /// Whether the theme is valid (no errors).
    /// </summary>
    public bool IsValid { get; internal set; }

    /// <summary>
    /// List of validation errors that prevent the theme from being used.
    /// </summary>
    public IReadOnlyList<string> Errors => _errors.AsReadOnly();
    private readonly List<string> _errors = new();

    /// <summary>
    /// List of validation warnings (non-fatal issues).
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings.AsReadOnly();
    private readonly List<string> _warnings = new();

    /// <summary>
    /// The calculated contrast ratio between foreground and background.
    /// </summary>
    public double? ContrastRatio { get; internal set; }

    /// <summary>
    /// Creates an empty validation result.
    /// </summary>
    public ValidationResult()
    {
        IsValid = true;
    }

    /// <summary>
    /// Creates a failed validation result with a single error.
    /// </summary>
    public static ValidationResult Failure(string error)
    {
        var result = new ValidationResult();
        result.AddError(error);
        result.IsValid = false;
        return result;
    }

    /// <summary>
    /// Adds an error to the validation result.
    /// </summary>
    internal void AddError(string error)
    {
        _errors.Add(error);
        IsValid = false;
    }

    /// <summary>
    /// Adds a warning to the validation result.
    /// </summary>
    internal void AddWarning(string warning)
    {
        _warnings.Add(warning);
    }

    /// <summary>
    /// Gets a summary of the validation result.
    /// </summary>
    public string GetSummary()
    {
        if (IsValid && !_warnings.Any())
        {
            return ContrastRatio.HasValue
                ? $"Valid theme (contrast ratio: {ContrastRatio.Value:F2}:1)"
                : "Valid theme";
        }

        var parts = new List<string>();

        if (!IsValid)
        {
            parts.Add($"{_errors.Count} error(s)");
        }

        if (_warnings.Any())
        {
            parts.Add($"{_warnings.Count} warning(s)");
        }

        if (ContrastRatio.HasValue)
        {
            parts.Add($"contrast ratio: {ContrastRatio.Value:F2}:1");
        }

        return string.Join(", ", parts);
    }
}
