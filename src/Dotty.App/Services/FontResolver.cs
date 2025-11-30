using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;

namespace Dotty.App.Services
{
    public static class FontResolver
    {
        public static FontFamily ResolveFontFamily(string? fontStack)
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
                    return mapped;
                }
            }

            return FontManager.Current.DefaultFontFamily;
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
