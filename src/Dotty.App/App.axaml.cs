using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Dotty.App.Services;

namespace Dotty.App;

public partial class App : Application
{
    private static readonly bool s_debugFonts = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTTY_DEBUG_FONTS"));

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply hard-coded defaults (settings removed for now)
        try
        {
            ApplyDefaultsToResources();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to apply defaults: {ex}");
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyDefaultsToResources()
    {
        if (Application.Current == null)
            return;

        var resources = Application.Current.Resources;

        // Resolve the configured stack to an installed family
        resources["TerminalFontFamily"] = ResolveFontFamily(Defaults.DefaultFontStack);
        resources["TerminalFontSize"] = Defaults.DefaultFontSize;

        try { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultBackground)); } catch { resources["TerminalBackground"] = new SolidColorBrush(Color.Parse("#1E1E1E")); }
        try { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse(Defaults.DefaultForeground)); } catch { resources["TerminalForeground"] = new SolidColorBrush(Color.Parse("#D4D4D4")); }
    }

    private static FontFamily ResolveFontFamily(string? fontStack)
    {
        var stack = string.IsNullOrWhiteSpace(fontStack)
            ? Defaults.DefaultFontStack
            : fontStack;

        var candidates = stack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var direct = TryCreateFontFamily(candidate);
            if (direct != null)
            {
                return direct;
            }

            var mapped = FindMatchingSystemFont(candidate);
            if (mapped != null)
            {
                LogFontDebug($"[Dotty] Font '{candidate}' mapped to installed '{mapped.Name}'");
                return mapped;
            }

            LogFontDebug($"[Dotty] Font '{candidate}' not found on this system");
        }

        var fallback = FontManager.Current.DefaultFontFamily;
        LogFontDebug($"[Dotty] Falling back to default font '{fallback.Name}'");
        return fallback;
    }

    private static FontFamily? TryCreateFontFamily(string candidate)
    {
        try
        {
            var family = new FontFamily(candidate);
            var typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
            if (FontManager.Current.TryGetGlyphTypeface(typeface, out _))
            {
                LogFontDebug($"[Dotty] Font '{candidate}' selected");
                return family;
            }
        }
        catch (Exception ex)
        {
            LogFontDebug($"[Dotty] Font '{candidate}' threw '{ex.Message}'");
        }

        return null;
    }

    private static FontFamily? FindMatchingSystemFont(string candidate)
    {
        var normalizedCandidate = NormalizeFontName(candidate);
        FontFamily? partialMatch = null;

        foreach (var family in FontManager.Current.SystemFonts)
        {
            foreach (var name in EnumerateFontNames(family))
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var normalizedName = NormalizeFontName(name);

                if (normalizedName == normalizedCandidate)
                {
                    return family;
                }

                if (normalizedName.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
                    normalizedCandidate.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    partialMatch ??= family;
                }
            }
        }

        return partialMatch;
    }

    private static IEnumerable<string> EnumerateFontNames(FontFamily family)
    {
        yield return family.Name;

        if (family.FamilyNames is { Count: > 0 })
        {
            foreach (var name in family.FamilyNames)
            {
                yield return name;
            }
        }
    }

    private static string NormalizeFontName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static void LogFontDebug(string message)
    {
        if (s_debugFonts)
        {
            Console.WriteLine(message);
        }
    }
}