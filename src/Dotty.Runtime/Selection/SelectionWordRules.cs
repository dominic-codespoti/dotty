using System.Buffers;
using System;
using System.Globalization;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Selection;

/// <summary>Class of one grid cluster for double-click word selection; a word is a maximal run of one class.</summary>
public enum SelectionCharClass
{
    Whitespace,
    Word,
    Punctuation,
}

/// <summary>
/// Word rules shared by double-click selection (and, later, accessibility boundary moves): letters, digits, combining
/// marks, <c>_</c>, emoji clusters and the configured punctuation (<c>selection.wordChars</c>) form words; other
/// punctuation forms runs of its own; blanks are whitespace.
/// </summary>
public static class SelectionWordRules
{
    public static SelectionCharClass Classify(ReadOnlySpan<char> cluster, ReadOnlySpan<char> wordChars)
    {
        int index = 0;
        while (index < cluster.Length && char.IsWhiteSpace(cluster[index])) index++;
        if (index >= cluster.Length) return SelectionCharClass.Whitespace;

        if (Rune.DecodeFromUtf16(cluster[index..], out Rune rune, out _) != OperationStatus.Done)
            return SelectionCharClass.Punctuation;

        if (rune.Value == '_' || IsWordCategory(Rune.GetUnicodeCategory(rune)) || IsEmoji(cluster, rune.Value))
            return SelectionCharClass.Word;

        if (rune.IsBmp && wordChars.IndexOf((char)rune.Value) >= 0)
            return SelectionCharClass.Word;

        return SelectionCharClass.Punctuation;
    }

    private static bool IsWordCategory(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    // Emoji clusters: wide symbols of U+2300..U+2BFF (⌚, ⭐), the pictograph planes (ZWJ sequences, skin tones, flags)
    // and any cluster carrying VS16 or ZWJ.
    private static bool IsEmoji(ReadOnlySpan<char> cluster, int codePoint) =>
        codePoint is >= 0x1F000 and <= 0x1FAFF
        || (codePoint is >= 0x2300 and <= 0x2BFF and not 0x2329 and not 0x232A && UnicodeWidth.GetWidth(cluster) == 2)
        || cluster.IndexOf('\uFE0F') >= 0
        || cluster.IndexOf('\u200D') >= 0;
}
