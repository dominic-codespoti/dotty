using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.App.Tests;

/// <summary>Where the scenario leaves the terminal when <see cref="ReflowScenario.Build"/> returns.</summary>
internal enum ReflowScenarioKind
{
    /// <summary>Main screen only; the alternate screen was never entered.</summary>
    Main,

    /// <summary>The alternate screen is active (the main screen, with its scrollback, is hidden).</summary>
    AltActive,

    /// <summary>The alternate screen was entered and left again: it exists but the main screen is active.</summary>
    AltLeft,
}

/// <summary>
/// Seeded random terminal content and resize sequences for the reflow oracle tests (plan 08, WI-R1). Everything goes
/// through the public parser, so scenarios exercise the same write paths as a real session: ASCII, wide CJK, combining
/// marks, SGR styles, OSC 8 hyperlinks, soft-wrapped and hard-broken lines (CRLF, bare LF, bare CR), cursor jumps, line
/// erases, tabs, DECSC/DECRC saved cursors, OSC 133;A prompt marks and the alternate screen. Content is written before any
/// resize, and wide characters only while the grid is at least four columns wide, so the scenarios stay inside what the
/// writer supports; the resize sequences then reach 1 and 2 columns and 1 row (reflow narrows wide glyphs there).
///
/// Partial scroll regions (DECSTBM) are deliberately not generated: scroll-region accounting is a separate, known-open area
/// (plan 08, WI-P6) and must not make reflow equivalence tests fail for an unrelated reason.
///
/// <see cref="Random"/> with an explicit seed is stable across .NET versions, so a seed always produces the same bytes.
/// </summary>
internal static class ReflowScenario
{
    private static readonly string[] Words =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima",
    ];

    private static readonly string[] WideText = ["\u4e2d", "\u6587", "\u5b57", "\u754c", "\u6f22", "\u8bed"];

    internal static TerminalAdapter Build(int seed, ReflowScenarioKind kind)
    {
        var random = new Random(seed);
        int rows = random.Next(2, 8);
        int columns = random.Next(4, 15);
        int capacity = random.Next(4) switch { 0 => 0, 1 => 5, 2 => 24, _ => 80 };
        var adapter = new TerminalAdapter(rows, columns, capacity);
        var parser = new BasicAnsiParser { Handler = adapter };

        int operations = random.Next(12, 90);
        int enterAt = kind == ReflowScenarioKind.Main ? -1 : random.Next(operations / 2, operations);
        int leaveAt = kind == ReflowScenarioKind.AltLeft ? Math.Min(operations - 1, enterAt + random.Next(1, 12)) : -1;

        var script = new StringBuilder(operations * 12);
        int links = 0;
        for (int i = 0; i < operations; i++)
        {
            if (i == enterAt)
                script.Append("\u001b[?1049h");
            AppendOperation(script, random, rows, columns, ref links);
            if (i == leaveAt)
                script.Append("\u001b[?1049l");
        }

        parser.Feed(Encoding.UTF8.GetBytes(script.ToString()));
        return adapter;
    }

    private static void AppendOperation(StringBuilder script, Random random, int rows, int columns, ref int links)
    {
        switch (random.Next(17))
        {
            case 0:
            case 1:
            case 2:
            case 3:
                // Longer than one row about half the time: soft wraps.
                script.Append(Word(random, random.Next(1, columns * 2 + 1)));
                break;
            case 4:
                for (int i = random.Next(1, 4); i > 0; i--)
                    script.Append(WideText[random.Next(WideText.Length)]);
                break;
            case 5:
                for (int i = random.Next(1, 4); i > 0; i--)
                    script.Append((char)('a' + random.Next(26))).Append('\u0301');
                break;
            case 6:
            case 7:
                script.Append("\r\n");
                break;
            case 8:
                script.Append('\n');
                break;
            case 9:
                script.Append('\r');
                break;
            case 10:
                script.Append("\u001b[").Append(random.Next(1, rows + 1)).Append(';').Append(random.Next(1, columns + 1)).Append('H');
                break;
            case 11:
                script.Append("\u001b[").Append(random.Next(3) switch { 0 => "", 1 => "1", _ => "2" }).Append('K');
                break;
            case 12:
                script.Append("\u001b[1;3").Append(random.Next(1, 8)).Append('m')
                    .Append(Word(random, random.Next(1, columns + 1))).Append("\u001b[0m");
                break;
            case 13:
                links++;
                script.Append("\u001b]8;;https://example.test/").Append(links).Append('\u0007')
                    .Append(Word(random, random.Next(1, columns + 1))).Append("\u001b]8;;\u0007");
                break;
            case 14:
                script.Append(random.Next(4) == 0 ? "\u001b8" : "\u001b7");
                break;
            case 15:
                script.Append("\u001b]133;A\u0007");
                break;
            default:
                script.Append('\t');
                break;
        }
    }

    private static string Word(Random random, int length)
    {
        var builder = new StringBuilder(length + 8);
        while (builder.Length < length)
        {
            if (builder.Length != 0)
                builder.Append(' ');
            builder.Append(Words[random.Next(Words.Length)]);
        }

        builder.Length = length;
        return builder.ToString();
    }

    /// <summary>
    /// Next resize target. Mixes rows-only, columns-only and both-changing steps with the extremes (1 and 2 columns,
    /// 1 row). May return the current size; callers skip such steps (<c>TerminalBuffer.Resize</c> ignores them).
    /// </summary>
    internal static (int Rows, int Columns) NextSize(Random random, int currentRows, int currentColumns)
    {
        int rows = random.Next(1, 10);
        int columns = random.Next(1, 19);
        switch (random.Next(8))
        {
            case 0:
                columns = 1;
                break;
            case 1:
                columns = 2;
                break;
            case 2:
                rows = 1;
                break;
            case 3:
            case 4:
                columns = currentColumns; // rows-only
                break;
            case 5:
                rows = currentRows; // columns-only
                break;
        }

        return (rows, columns);
    }
}
