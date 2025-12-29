using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Platform;

namespace Dotty.App.Tools;

internal static class FontProbe
{
    // A short list (top offenders) to inspect by default
    private static readonly int[] DefaultCodepoints = new[] {
        0x2588, // █
        0x2500, // ─
        0x2550, // ═
        0x2551, // ║
        0x2501, // ━
        0x2557, //╗
        0x255D, //╝
        0x255A, //╚
        0x2554, //╔
        0x2502, //│
        0x26A1, //⚡
        0x2022, //•
        0x256E, //╮
        0x2570, //╰
        0x256F, //╯
        0xE0B6, // PUA
        0xE0B4, // PUA
        0xF444, // PUA
        0xF0A9, // PUA
    };

    public static void Run(IEnumerable<int>? codepoints = null)
    {
        var cps = (codepoints ?? DefaultCodepoints).ToArray();

        Console.WriteLine("[Dotty][Probe] Scanning system fonts for glyph coverage...");

        var fonts = FontManager.Current.SystemFonts.ToArray();
        Console.WriteLine($"[Dotty][Probe] Found {fonts.Length} installed font families to check.");

        var matches = new Dictionary<int, List<string>>();
        foreach (var cp in cps) matches[cp] = new List<string>();

        for (int i = 0; i < fonts.Length; i++)
        {
            var fam = fonts[i];
            var famName = fam.Name ?? $"(unknown-{i})";
            if (i % 100 == 0) Console.WriteLine($"[Dotty][Probe] Checking {i+1}/{fonts.Length}: {famName}");

            try
            {
                var tf = new Typeface(fam);
                if (!FontManager.Current.TryGetGlyphTypeface(tf, out var gf)) continue;

                // Try to inspect mapping tables first
                bool[] has = new bool[cps.Length];
                try
                {
                    var prop = gf.GetType().GetProperty("CharacterToGlyphMap") ?? gf.GetType().GetProperty("CharacterMap");
                    if (prop != null)
                    {
                        var map = prop.GetValue(gf) as System.Collections.IDictionary;
                        if (map != null)
                        {
                            for (int j = 0; j < cps.Length; j++)
                            {
                                var cp = cps[j];
                                if (cp <= 0xFFFF)
                                {
                                    var ch = (char)cp;
                                    if (map.Contains(ch)) has[j] = true;
                                }
                                if (map.Contains(cp) || map.Contains((uint)cp) || map.Contains((long)cp)) has[j] = true;
                            }
                        }
                    }
                }
                catch { }

                // If not found yet, try HarfBuzz TryGet... methods if available
                for (int j = 0; j < cps.Length; j++)
                {
                    if (has[j]) continue;
                    var cp = (uint)cps[j];
                    try
                    {
                        var fontProp = gf.GetType().GetProperty("Font");
                        var font = fontProp?.GetValue(gf);
                        if (font != null)
                        {
                            var tryNames = new[] { "TryGetNominalGlyph", "TryGetGlyph", "TryGetVariationGlyph" };
                            foreach (var name in tryNames)
                            {
                                // Suppress ILLinker trimming warning for runtime method lookup
#pragma warning disable IL2075
                                var meth = font.GetType().GetMethod(name, new[] { typeof(uint), typeof(uint).MakeByRefType() })
                                          ?? font.GetType().GetMethod(name, new[] { typeof(int), typeof(int).MakeByRefType() });
#pragma warning restore IL2075
                                if (meth != null)
                                {
                                    var outParamType = meth.GetParameters()[1].ParameterType.GetElementType();
                                    object outValue = outParamType == typeof(uint) ? (object)0u : (object)0;
                                    var args = new object[] { cp, outValue };
                                    var okObj = meth.Invoke(font, args);
                                    var ok = okObj is bool b && b;
                                    if (ok)
                                    {
                                        var glyphObj = args[1];
                                        if (glyphObj is uint gUint)
                                        {
                                            if (gUint != 0) { has[j] = true; break; }
                                        }
                                        else if (glyphObj is int gInt)
                                        {
                                            if (gInt != 0) { has[j] = true; break; }
                                        }
                                        else if (glyphObj != null && !glyphObj.Equals(0)) { has[j] = true; break; }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                for (int j = 0; j < cps.Length; j++)
                {
                    if (has[j]) matches[cps[j]].Add(famName);
                }
            }
            catch { }
        }

        Console.WriteLine("[Dotty][Probe] Results:");
        foreach (var cp in cps)
        {
            var ch = char.ConvertFromUtf32(cp);
            string name;
            try { name = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch, 0).ToString(); } catch { name = ""; }
            var list = matches[cp];
            Console.WriteLine($"U+{cp:X4} '{ch}' -> {list.Count} matching families");
            foreach (var fam in list.Take(12)) Console.WriteLine($"  - {fam}");
        }

        // show families that cover many
        var famCoverage = matches.SelectMany(kv => kv.Value.Select(f => (cp: kv.Key, fam: f))).GroupBy(t => t.fam).Select(g => new { fam = g.Key, cps = g.Select(x => x.cp).Distinct().ToArray() }).OrderByDescending(x => x.cps.Length);
        Console.WriteLine("[Dotty][Probe] Candidate families covering multiple requested codepoints:");
        foreach (var item in famCoverage.Take(40))
        {
            Console.WriteLine($"  - {item.fam}: covers {item.cps.Length} -> {string.Join(',', item.cps.Select(x=>$"U+{x:X4}"))}");
        }

        Console.WriteLine("[Dotty][Probe] Done.");
    }
}
