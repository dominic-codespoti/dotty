using System.Text;

namespace Dotty.Performance.Tests.Data;

/// <summary>
/// Deterministic, input-independent workloads for the scan, OSC, reflow and text-format
/// benchmarks. Unlike <see cref="TestDataGenerator"/> nothing here uses shared mutable
/// <see cref="Random"/> state, so every benchmark process builds byte-identical data
/// regardless of which other benchmarks ran first. All line breaks are CRLF so that
/// output stays left-aligned on a real terminal grid.
/// </summary>
public static class BenchmarkContent
{
    /// <summary>Size of the parser scan inputs (read results as ns per 64 KiB).</summary>
    public const int ScanBytes = 64 * 1024;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789 (),.;{}=+-_/";

    private static readonly string[] Words =
    [
        "alpha", "error", "bravo", "request", "charlie", "timeout", "delta", "warning",
        "echo", "connection", "foxtrot", "retry", "golf", "buffer", "hotel", "cache",
        "india", "error", "juliet", "session", "kilo", "render", "lima", "scroll",
    ];

    /// <summary>
    /// Repeats <paramref name="unit"/> (index 0, 1, ...) until the next unit would no longer
    /// fit into <paramref name="totalBytes"/>, then pads with ASCII '.'. Units must be non-empty
    /// and self-contained (a complete escape sequence or a complete text element) so the padding
    /// never splits one.
    /// </summary>
    public static byte[] Fill(int totalBytes, Func<int, string> unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var buffer = new byte[totalBytes];
        int position = 0;
        for (int index = 0; ; index++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(unit(index));
            if (bytes.Length == 0)
                throw new InvalidOperationException("Fill units must not be empty.");
            if (position + bytes.Length > totalBytes)
                break;
            bytes.CopyTo(buffer, position);
            position += bytes.Length;
        }

        while (position < totalBytes)
            buffer[position++] = (byte)'.';
        return buffer;
    }

    /// <summary>Printable-ASCII text of exactly <paramref name="length"/> chars, varying with <paramref name="index"/>.</summary>
    public static string AsciiRun(int index, int length)
    {
        var chars = new char[length];
        int start = index * 7;
        for (int i = 0; i < length; i++)
            chars[i] = Alphabet[(start + i) % Alphabet.Length];
        return new string(chars);
    }

    /// <summary>Runs of printable ASCII, optionally each followed by CRLF.</summary>
    public static byte[] AsciiRuns(int runLength, bool crlf, int totalBytes = ScanBytes) =>
        Fill(totalBytes, index => crlf ? AsciiRun(index, runLength) + "\r\n" : AsciiRun(index, runLength));

    /// <summary>One <paramref name="runLength"/>-byte ASCII run per SGR; a CRLF after every fourth run.</summary>
    public static byte[] AsciiRunsSplitBySgr(int runLength, int totalBytes = ScanBytes) =>
        Fill(totalBytes, index =>
            $"\u001b[3{index % 8}m" + AsciiRun(index, runLength) + (index % 4 == 3 ? "\r\n" : string.Empty));

    /// <summary>A continuous run of 3-byte CJK ideographs (U+4E00..), no line breaks.</summary>
    public static byte[] CjkRun(int totalBytes = ScanBytes) =>
        Fill(totalBytes, index => char.ConvertFromUtf32(0x4E00 + index % 5000));

    /// <summary>Log-like lines: an ASCII prefix, a short CJK tail, then CRLF.</summary>
    public static byte[] MixedAsciiCjkLines(int totalBytes = ScanBytes) =>
        Fill(totalBytes, index => $"[{index % 1000:D3}] request handled: " + CjkText(index, 10) + "\r\n");

    /// <summary>Lines of 30 letters, each followed by U+0301 COMBINING ACUTE ACCENT.</summary>
    public static byte[] CombiningMarkLines(int totalBytes = ScanBytes) =>
        Fill(totalBytes, index =>
        {
            var builder = new StringBuilder(100);
            for (int i = 0; i < 30; i++)
                builder.Append((char)('a' + (index + i) % 26)).Append('\u0301');
            return builder.Append("\r\n").ToString();
        });

    /// <summary>Family emoji (man ZWJ woman ZWJ girl) separated by spaces, 8 per line.</summary>
    public static byte[] EmojiZwjLines(int totalBytes = ScanBytes) =>
        Fill(totalBytes, index =>
            "\U0001F468\u200D\U0001F469\u200D\U0001F467" + (index % 8 == 7 ? "\r\n" : " "));

    /// <summary><paramref name="count"/> CJK ideographs (each 2 columns wide), varying with <paramref name="index"/>.</summary>
    public static string CjkText(int index, int count)
    {
        var builder = new StringBuilder(count * 2);
        for (int i = 0; i < count; i++)
            builder.Append(char.ConvertFromUtf32(0x4E00 + (index * 31 + i * 17) % 5000));
        return builder.ToString();
    }

    /// <summary>Word soup of exactly <paramref name="length"/> chars.</summary>
    public static string WordText(int index, int length)
    {
        var builder = new StringBuilder(length + 16);
        for (int word = index; builder.Length < length; word += 5)
        {
            if (builder.Length != 0)
                builder.Append(' ');
            builder.Append(Words[word % Words.Length]);
        }

        builder.Length = length;
        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="lines"/> lines "[nnnnnn] words", each <paramref name="minLength"/> to
    /// <paramref name="minLength"/> + <paramref name="lengthSpan"/> - 1 chars before the CRLF; with
    /// <paramref name="promptMarkEvery"/> &gt; 0 an OSC 133;A is emitted before every n-th line.
    /// Lines longer than the terminal width soft-wrap (the realistic reflow input).
    /// </summary>
    public static byte[] WorkloadLines(int lines, int minLength, int lengthSpan, int promptMarkEvery = 0)
    {
        var builder = new StringBuilder(lines * (minLength + lengthSpan + 4));
        for (int i = 0; i < lines; i++)
        {
            if (promptMarkEvery > 0 && i % promptMarkEvery == 0)
                builder.Append("\u001b]133;A\u0007");
            int length = minLength + (int)((long)i * 37 % lengthSpan);
            string prefix = $"[{i % 1_000_000:D6}] ";
            builder.Append(prefix).Append(WordText(i, Math.Max(0, length - prefix.Length))).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Lines of an odd-width ASCII prefix followed by 35..59 CJK ideographs, so wide-glyph wrap
    /// boundaries fall on both parities of the target column count.
    /// </summary>
    public static byte[] WideCharLines(int lines)
    {
        var builder = new StringBuilder(lines * 140);
        for (int i = 0; i < lines; i++)
        {
            builder.Append($"[{i % 1_000_000:D6}] ");
            builder.Append(CjkText(i, 35 + i * 7 % 25)).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// A full <paramref name="rows"/> x <paramref name="columns"/> grid of log lines (every 7th is an
    /// ERROR line), separated by CRLF without a trailing one, so nothing scrolls.
    /// </summary>
    public static byte[] LogGrid(int rows, int columns)
    {
        var builder = new StringBuilder(rows * columns);
        for (int row = 0; row < rows; row++)
        {
            if (row != 0)
                builder.Append("\r\n");
            string prefix = row % 7 == 0 ? "[ERROR] error: " : "[INFO]  ";
            builder.Append(prefix).Append(WordText(row, columns - 1 - prefix.Length));
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>A full grid of CJK text: every row is <paramref name="columns"/> / 2 ideographs.</summary>
    public static byte[] CjkGrid(int rows, int columns)
    {
        var builder = new StringBuilder(rows * columns * 2);
        for (int row = 0; row < rows; row++)
        {
            if (row != 0)
                builder.Append("\r\n");
            builder.Append(CjkText(row, columns / 2));
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// <paramref name="lines"/> single-row lines of exactly <paramref name="columns"/> - 1 columns
    /// (ASCII words), so every line scrolls into history as one non-wrapped row.
    /// </summary>
    public static byte[] ScrollbackLines(int lines, int columns)
    {
        var builder = new StringBuilder(lines * (columns + 1));
        for (int i = 0; i < lines; i++)
            builder.Append(WordText(i, columns - 1)).Append("\r\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Like <see cref="ScrollbackLines"/> with CJK text of (columns - 2) / 2 ideographs.</summary>
    public static byte[] CjkScrollbackLines(int lines, int columns)
    {
        var builder = new StringBuilder(lines * columns * 2);
        for (int i = 0; i < lines; i++)
            builder.Append(CjkText(i, (columns - 2) / 2)).Append("\r\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
