using System;
using Avalonia.Media;
using Xunit;

namespace Dotty.App.Tests;

public class FontFallbackDiagnosticsTests
{
    [Fact(Skip = "Platform font diagnostics require Avalonia font manager; skipped in headless test environment")]
    public void ReportInstalledSystemFontsAndGlyphSupport()
    {
        var candidates = Dotty.App.Services.Defaults.DefaultFontStack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var interesting = new[] { '\uE0B6', '\uE0B4', '\u2022', '\uF444', '\uF0A9' };

        // FontManager.Current requires an Avalonia platform to be registered; in
        // test environments it may not be available (no GUI platform). If the
        // platform is missing, skip the diagnostic portion gracefully.
        try
        {
            Console.WriteLine("Installed system fonts (sample):");
            int shown = 0;
            foreach (var f in FontManager.Current.SystemFonts)
            {
                Console.WriteLine($" - {f.Name}");
                if (++shown >= 10) break;
            }
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Skipping font diagnostics: no Avalonia font manager available in test environment.");
            return; // non-fatal - environment not suitable for platform font tests
        }

        foreach (var g in interesting)
        {
            Console.WriteLine($"Glyph U+{(int)g:X4} ('{g}') availability:");
            foreach (var candidate in candidates)
            {
                try
                {
                    var fam = new FontFamily(candidate);
                    var tf = new Typeface(fam);
                    if (FontManager.Current.TryGetGlyphTypeface(tf, out var gf))
                    {
                        var prop = gf.GetType().GetProperty("CharacterToGlyphMap") ?? gf.GetType().GetProperty("CharacterMap");
                        bool supports = false;
                        if (prop != null)
                        {
                            var map = prop.GetValue(gf) as System.Collections.IDictionary;
                            if (map != null)
                            {
                                var cp = (int)g;
                                supports = map.Contains(g) || map.Contains(cp) || map.Contains((uint)cp);
                            }
                        }

                        Console.WriteLine($"  {candidate}: {(supports ? "supports" : "no")}");
                    }
                    else
                    {
                        Console.WriteLine($"  {candidate}: no glyph typeface");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {candidate}: error {ex.Message}");
                }
            }
        }

        Assert.True(true);
    }
}
