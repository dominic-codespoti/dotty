using System;

namespace Dotty.Abstractions.Config;

/// <summary>
/// Effective scrollback limits for a terminal session: a line cap and an optional cap on
/// the raw ring size. Whichever limit is reached first applies.
/// </summary>
/// <param name="MaxLines">Maximum history lines; 0 means no history.</param>
/// <param name="MaxBytes">
/// Maximum raw ring bytes, counted as <c>(lines + rows) * columns * <see cref="BytesPerCell"/></c>;
/// 0 means no byte cap.
/// </param>
public readonly record struct ScrollbackPolicy(int MaxLines, long MaxBytes)
{
    /// <summary>Upper bound for <see cref="MaxLines"/>.</summary>
    public const int HardMaxLines = 1_000_000;

    /// <summary>Smallest accepted non-zero <see cref="MaxBytes"/>: 1 MiB.</summary>
    public const long MinNonZeroBytes = 1024 * 1024;

    /// <summary>Raw bytes one cell occupies in the ring (hot + cold halves).</summary>
    public const int BytesPerCell = 12;

    /// <summary>The built-in policy: <see cref="DottyDefaults.ScrollbackLines"/> lines, <see cref="DottyDefaults.ScrollbackLimitBytes"/> bytes.</summary>
    public static ScrollbackPolicy Default => new(DottyDefaults.ScrollbackLines, DottyDefaults.ScrollbackLimitBytes);

    /// <summary>A line-count-only policy (no byte cap).</summary>
    public static ScrollbackPolicy Fixed(int lines) => new(lines, 0);

    /// <summary>
    /// Clamps to supported ranges: negative lines fall back to the default line count and values above
    /// <see cref="HardMaxLines"/> are clamped; negative bytes or a non-zero value below
    /// <see cref="MinNonZeroBytes"/> fall back to the default byte limit.
    /// </summary>
    public ScrollbackPolicy Normalize()
    {
        int lines = MaxLines < 0 ? DottyDefaults.ScrollbackLines : Math.Min(MaxLines, HardMaxLines);
        long bytes = MaxBytes < 0 || (MaxBytes > 0 && MaxBytes < MinNonZeroBytes)
            ? DottyDefaults.ScrollbackLimitBytes
            : MaxBytes;
        return new ScrollbackPolicy(lines, bytes);
    }

    /// <summary>
    /// History lines that fit for a viewport of <paramref name="rows"/> x <paramref name="columns"/>:
    /// <c>min(MaxLines, floor(MaxBytes / (columns * BytesPerCell)) - rows)</c>, never negative.
    /// </summary>
    public int CapacityFor(int rows, int columns)
    {
        int lines = Math.Max(0, MaxLines);
        if (MaxBytes <= 0)
            return lines;

        long rowBytes = (long)Math.Max(1, columns) * BytesPerCell;
        long fit = MaxBytes / rowBytes - Math.Max(0, rows);
        if (fit <= 0)
            return 0;
        return fit < lines ? (int)fit : lines;
    }
}
