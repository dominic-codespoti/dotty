using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;

namespace Dotty.App.Services
{
    public static class FontResolver
    {
        public static Action<FontFamily>? FontResolved;

        public static FontFamily ResolveFontFamily(string? fontStack)
        {
            var stack = string.IsNullOrWhiteSpace(fontStack)
                ? Defaults.DefaultFontStack
                : fontStack;

            var candidates = stack.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            FontFamily? symbolFallback = null;
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var direct = TryCreateFontFamily(candidate);
                if (direct != null)
                {
                    if (!FontHelpers.IsLikelySymbolFontName(candidate) && !FontHelpers.IsLikelySymbolFontName(direct.Name))
                    {
                        NotifyFontResolved(direct);
                        return direct;
                    }

                    symbolFallback ??= direct;
                    continue;
                }

                var mapped = FindMatchingSystemFont(candidate);
                if (mapped != null)
                {
                    if (!FontHelpers.IsLikelySymbolFontName(candidate) && !FontHelpers.IsLikelySymbolFontName(mapped.Name))
                    {
                        NotifyFontResolved(mapped);
                        return mapped;
                    }

                    symbolFallback ??= mapped;
                }
            }

            if (symbolFallback != null)
            {
                NotifyFontResolved(symbolFallback);
                return symbolFallback;
            }

            // If none of the individual candidates resolved, return a composite FontFamily
            // containing the entire stack. Platforms that support composite family names
            // will perform per-glyph fallback across the listed families.
            try
            {
                var fallback = new FontFamily(stack);
                NotifyFontResolved(fallback);
                return fallback;
            }
            catch
            {
                var fallback = FontManager.Current.DefaultFontFamily;
                NotifyFontResolved(fallback);
                return fallback;
            }
        }

        private static void NotifyFontResolved(FontFamily family)
        {
            try
            {
                FontResolved?.Invoke(family);
            }
            catch
            {
            }
        }

        private static FontFamily? TryCreateFontFamily(string candidate)
        {
            var family = new FontFamily(candidate);
            var typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
            if (FontManager.Current.TryGetGlyphTypeface(typeface, out _))
            {
                return family;
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
    }
}
