using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dotty.Runtime.Input;

namespace Dotty.Runtime.Search;

/// <summary>
/// The search overlay's single-line query and cursor. The cursor always sits on a grapheme-cluster boundary
/// (a UTF-16 index), so moves and deletions never split a surrogate pair, a combining sequence, or a ZWJ emoji.
/// </summary>
public sealed class SearchQueryEditor
{
    private string _text = string.Empty;

    /// <summary>The query.</summary>
    public string Text => _text;

    /// <summary>UTF-16 index of the cursor; always a grapheme boundary of <see cref="Text"/>.</summary>
    public int Cursor { get; private set; }

    /// <summary>Empties the query.</summary>
    public void Clear()
    {
        _text = string.Empty;
        Cursor = 0;
    }

    /// <summary>Replaces the query and puts the cursor at the end.</summary>
    public void SetText(string text)
    {
        _text = Filter(text);
        Cursor = _text.Length;
    }

    /// <summary>Inserts typed text at the cursor; control characters are dropped. True when the query changed.</summary>
    public bool Insert(ReadOnlySpan<char> text)
    {
        string filtered = Filter(text);
        if (filtered.Length == 0)
            return false;
        _text = _text.Insert(Cursor, filtered);
        Cursor = SnapForward(Cursor + filtered.Length);
        return true;
    }

    /// <summary>
    /// Inserts pasted text: neutralised like a terminal paste (<see cref="PasteEncoder.Sanitize"/>), then cut at the
    /// first line break, since a query is one line. True when the query changed.
    /// </summary>
    public bool Paste(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        int lineEnd = text.AsSpan().IndexOfAny('\r', '\n');
        ReadOnlySpan<char> line = lineEnd < 0 ? text : text.AsSpan(0, lineEnd);
        if (line.IsEmpty)
            return false;
        var sanitized = new char[line.Length];
        int length = PasteEncoder.Sanitize(line, sanitized, newlinesToCr: false);
        return Insert(sanitized.AsSpan(0, length));
    }

    public bool MoveLeft(bool word = false)
    {
        int target = word ? WordStart(Cursor) : PreviousBoundary(Cursor);
        return SetCursor(target);
    }

    public bool MoveRight(bool word = false)
    {
        int target = word ? WordEnd(Cursor) : NextBoundary(Cursor);
        return SetCursor(target);
    }

    public bool MoveHome() => SetCursor(0);

    public bool MoveEnd() => SetCursor(_text.Length);

    /// <summary>Deletes the grapheme (or whitespace-delimited word) before the cursor. True when the query changed.</summary>
    public bool Backspace(bool word = false)
    {
        if (Cursor == 0)
            return false;
        int start = word ? WordStart(Cursor) : PreviousBoundary(Cursor);
        _text = _text.Remove(start, Cursor - start);
        Cursor = start;
        return true;
    }

    /// <summary>Deletes the grapheme (or word) after the cursor. True when the query changed.</summary>
    public bool Delete(bool word = false)
    {
        if (Cursor >= _text.Length)
            return false;
        int end = word ? WordEnd(Cursor) : NextBoundary(Cursor);
        _text = _text.Remove(Cursor, end - Cursor);
        return true;
    }

    /// <summary>
    /// Places the cursor before the <paramref name="column"/>-th grapheme (0-based; clamped), as a click into the
    /// input box does. Returns whether the cursor moved.
    /// </summary>
    public bool SetCursorToGrapheme(int column)
    {
        int index = 0;
        for (int i = 0; i < column && index < _text.Length; i++)
            index = NextBoundary(index);
        return SetCursor(index);
    }

    private bool SetCursor(int index)
    {
        if (index == Cursor)
            return false;
        Cursor = index;
        return true;
    }

    private int NextBoundary(int index)
    {
        if (index >= _text.Length)
            return _text.Length;
        return index + StringInfo.GetNextTextElementLength(_text.AsSpan(index));
    }

    private int PreviousBoundary(int index)
    {
        int previous = 0;
        int position = 0;
        while (position < index)
        {
            previous = position;
            position = NextBoundary(position);
        }
        return previous;
    }

    // An inserted fragment can merge with the following text (a combining mark typed before one): keep the
    // cursor on a boundary by moving it to the end of the cluster it landed in.
    private int SnapForward(int index)
    {
        int position = 0;
        while (position < index)
            position = NextBoundary(position);
        return position;
    }

    private int WordStart(int index)
    {
        int start = index;
        while (start > 0 && char.IsWhiteSpace(_text[start - 1]))
            start--;
        while (start > 0 && !char.IsWhiteSpace(_text[start - 1]))
            start--;
        return start;
    }

    private int WordEnd(int index)
    {
        int end = index;
        while (end < _text.Length && char.IsWhiteSpace(_text[end]))
            end++;
        while (end < _text.Length && !char.IsWhiteSpace(_text[end]))
            end++;
        return end;
    }

    private static string Filter(ReadOnlySpan<char> text)
    {
        StringBuilder? builder = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool keep = !char.IsControl(c) && !(char.IsSurrogate(c) && !IsPairedSurrogate(text, i));
            if (keep)
            {
                builder?.Append(c);
                continue;
            }
            builder ??= new StringBuilder(text.Length).Append(text[..i]);
        }
        return builder?.ToString() ?? text.ToString();
    }

    private static bool IsPairedSurrogate(ReadOnlySpan<char> text, int i) =>
        char.IsHighSurrogate(text[i])
            ? i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
            : i > 0 && char.IsHighSurrogate(text[i - 1]);
}
