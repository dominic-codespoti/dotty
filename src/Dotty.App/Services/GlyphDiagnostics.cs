using System;
using Avalonia.Media;

namespace Dotty.App.Services;

internal static class GlyphDiagnostics
{
    public static void Run()
    {
        try
        {
            var candidates = Defaults.DefaultFontStack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var interesting = new[] { '\uE0B6', '\uE0B4', '\u2022', '\uF444', '\uF0A9' };

            Console.WriteLine("[Dotty][GlyphDiag] Installed system fonts (sample):");
            int shown = 0;
            foreach (var f in FontManager.Current.SystemFonts)
            {
                Console.WriteLine($"[Dotty][GlyphDiag]  - {f.Name}");
                if (++shown >= 10) break;
            }

            foreach (var g in interesting)
            {
                Console.WriteLine($"[Dotty][GlyphDiag] Glyph U+{(int)g:X4} ('{g}') availability:");
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var fam = new FontFamily(candidate);
                        var tf = new Typeface(fam);
                        if (FontManager.Current.TryGetGlyphTypeface(tf, out var gf))
                        {
                                Console.WriteLine($"[Dotty][GlyphDiag]   {candidate}: got GlyphTypeface -> {gf.GetType().FullName}");
                                // Reflectively dump some helpful properties (if present) and sample CharacterMap keys
                                try
                                {
                                    var props = gf.GetType().GetProperties();
                                    foreach (var p in props)
                                    {
                                        if (p.Name == "CharacterMap" || p.Name == "CharacterToGlyphMap") continue;
                                        try { var v = p.GetValue(gf); if (v != null) Console.WriteLine($"[Dotty][GlyphDiag]     Prop {p.Name} = {v}"); } catch { }
                                    }

                                    var cmapProp = gf.GetType().GetProperty("CharacterMap") ?? gf.GetType().GetProperty("CharacterToGlyphMap");
                                    if (cmapProp != null)
                                    {
                                        var map = cmapProp.GetValue(gf) as System.Collections.IDictionary;
                                        if (map != null)
                                        {
                                            Console.WriteLine($"[Dotty][GlyphDiag]     CharacterMap keys sample (count={map.Count}):");
                                            int i = 0;
                                            foreach (var key in map.Keys)
                                            {
                                                if (++i > 20) break;
                                                Console.WriteLine($"[Dotty][GlyphDiag]       [{i}] ({key?.GetType().Name}) => {key}");
                                            }
                                        }
                                        else
                                        {
                                            Console.WriteLine("[Dotty][GlyphDiag]     CharacterMap present but not enumerable as IDictionary");
                                        }
                                    }
                                    else
                                    {
                                            Console.WriteLine("[Dotty][GlyphDiag]     No CharacterMap/CharacterToGlyphMap property found on GlyphTypeface");
                                            // Try to inspect HarfBuzz face/font for glyph mapping methods
                                            try
                                            {
                                                var faceProp = gf.GetType().GetProperty("Face");
                                                var fontProp = gf.GetType().GetProperty("Font");
                                                var face = faceProp?.GetValue(gf);
                                                var font = fontProp?.GetValue(gf);
                                                if (face != null)
                                                {
                                                    Console.WriteLine($"[Dotty][GlyphDiag]     Found Face object: {face.GetType().FullName}");
                                                    var faceMethods = face.GetType().GetMethods();
                                                    foreach (var m in faceMethods)
                                                    {
                                                        if (m.Name.IndexOf("Glyph", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0)
                                                        {
                                                            Console.WriteLine($"[Dotty][GlyphDiag]       Face method: {m.Name}");
                                                        }
                                                    }
                                                }
                                                if (font != null)
                                                {
                                                    Console.WriteLine($"[Dotty][GlyphDiag]     Found Font object: {font.GetType().FullName}");
                                                    var fontMethods = font.GetType().GetMethods();
                                                    foreach (var m in fontMethods)
                                                    {
                                                            if (m.Name.IndexOf("Glyph", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0)
                                                            {
                                                                var paramTypes = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name));
                                                                Console.WriteLine($"[Dotty][GlyphDiag]       Font method: {m.Name}({paramTypes})");
                                                            }
                                                    }

                                                    // Try calling some common method names with the codepoint
                                                    var cp = (uint)g;
                                                    var tryMethods = new[] { "TryGetNominalGlyph", "TryGetGlyph", "TryGetVariationGlyph" };
                                                    foreach (var name in tryMethods)
                                                    {
                                                        // look for (uint cp, out uint glyph) or (int cp, out int glyph)
                                                        var meth = font.GetType().GetMethod(name, new[] { typeof(uint), typeof(uint).MakeByRefType() })
                                                                  ?? font.GetType().GetMethod(name, new[] { typeof(int), typeof(int).MakeByRefType() });
                                                        if (meth != null)
                                                        {
                                                            try
                                                            {
                                                                var outParamType = meth.GetParameters()[1].ParameterType.GetElementType();
                                                                var outValue = outParamType == typeof(uint) ? (object)0u : 0;
                                                                var args = new object[] { cp, outValue };
                                                                var ok = (bool)meth.Invoke(font, args);
                                                                var glyphId = args[1];
                                                                Console.WriteLine($"[Dotty][GlyphDiag]       Invoked {name}({cp}) => success={ok}, glyph={glyphId}");
                                                            }
                                                            catch (Exception ex)
                                                            {
                                                                Console.WriteLine($"[Dotty][GlyphDiag]       Invoking {name} failed: {ex.Message}");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"[Dotty][GlyphDiag]     error inspecting Face/Font: {ex.Message}");
                                            }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[Dotty][GlyphDiag]     error inspecting glyph typeface: {ex.Message}");
                                }
                                        var prop = gf.GetType().GetProperty("CharacterToGlyphMap") ?? gf.GetType().GetProperty("CharacterMap");
                                        bool supports = false;
                                if (prop != null)
                                {
                                    var map = prop.GetValue(gf) as System.Collections.IDictionary;
                                    if (map != null)
                                    {
                                        var cp = (int)g;
                                        // Check multiple key types
                                        supports = map.Contains(g) || map.Contains(cp) || map.Contains((uint)cp) || map.Contains((long)cp);
                                    }
                                }
                                        else
                                        {
                                            // If no CharacterMap, try HarfBuzz Font TryGet.. methods
                                            try
                                            {
                                                var fontProp = gf.GetType().GetProperty("Font");
                                                var font = fontProp?.GetValue(gf);
                                                if (font != null)
                                                {
                                                    var cp = (uint)g;
                                                    var tryNames = new[] { "TryGetNominalGlyph", "TryGetGlyph", "TryGetVariationGlyph" };
                                                    foreach (var name in tryNames)
                                                    {
                                                        var meth = font.GetType().GetMethod(name, new[] { typeof(uint), typeof(uint).MakeByRefType() })
                                                                  ?? font.GetType().GetMethod(name, new[] { typeof(int), typeof(int).MakeByRefType() });
                                                        if (meth != null)
                                                        {
                                                            var outParamType = meth.GetParameters()[1].ParameterType.GetElementType();
                                                            var outValue = outParamType == typeof(uint) ? (object)0u : 0;
                                                            var args = new object[] { cp, outValue };
                                                            var ok = (bool)meth.Invoke(font, args);
                                                            var glyphId = args[1];
                                                            Console.WriteLine($"[Dotty][GlyphDiag]     {name}({cp}) => success={ok}, glyph={glyphId}");
                                                            if (ok && !glyphId.Equals(0))
                                                            {
                                                                supports = true;
                                                                break;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            catch { }
                                        }

                                Console.WriteLine($"[Dotty][GlyphDiag]   {candidate}: {(supports ? "supports" : "no")}");
                        }
                        else
                        {
                            Console.WriteLine($"[Dotty][GlyphDiag]   {candidate}: no glyph typeface");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Dotty][GlyphDiag]   {candidate}: error {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try { Console.WriteLine($"[Dotty][GlyphDiag] error: {ex}"); } catch { }
        }
    }
}
