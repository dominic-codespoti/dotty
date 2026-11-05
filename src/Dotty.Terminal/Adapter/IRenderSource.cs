using System;
using Dotty.Abstractions.Config;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Read surface the frame composer needs. Implemented by
/// <see cref="TerminalBuffer"/> (live; the renderer holds SyncRoot) and by
/// <see cref="RenderSnapshot"/> (immutable copy; rasterized without the lock).
/// The composer depends on this interface so the same render path serves both.
/// </summary>
public interface IRenderSource
{
    int Rows { get; }
    int Columns { get; }
    int ScrollbackCount { get; }
    bool IsAlternateScreenActive { get; }
    int CursorRow { get; }
    int CursorCol { get; }
    /// <summary>DECTCEM cursor visibility.</summary>
    bool CursorVisible { get; }
    TerminalCursorShape CursorShape { get; }
    bool CursorBlinking { get; }
    /// <summary>True while no DECSCUSR 1-6 style is in effect; the renderer then uses the configured cursor style.</summary>
    bool CursorStyleIsDefault { get; }
    ReadOnlySpan<ulong> RowGenerations { get; }

    /// <summary>Zero-copy read-only view of one visible row's hot cells.</summary>
    ReadOnlySpan<CellHot> GetRowCells(int row);

    /// <summary>Zero-copy read-only view of one visible row's cold cells.</summary>
    ReadOnlySpan<ColdCell> GetRowColdCells(int row);

    /// <summary>Identity generation of one visible row (bump-only).</summary>
    ulong GetRowGeneration(int row);

    ref readonly CellAttributes GetStyle(ushort styleId);

    /// <summary>
    /// URL for a cold-cell <see cref="ColdCell.HyperlinkId"/> (OSC 8), or null when the id is
    /// 0/unknown. Snapshots resolve against the table captured with them.
    /// </summary>
    string? GetHyperlinkUrl(ushort id);

    /// <summary>
    /// Text of one scrollback line. Index 0 is the OLDEST line and <c>ScrollbackCount - 1</c> the newest, so a line's
    /// row number is <c>index - ScrollbackCount</c> (-1 = newest). Empty when the source did not materialize it.
    /// </summary>
    string GetScrollbackLineText(int index);

    /// <summary>
    /// Cells of one scrollback line (same index convention as <see cref="GetScrollbackLineText"/>: 0 = oldest), covering
    /// columns 0..the line's last occupied column. An empty line returns true with empty spans. Returns false when the
    /// source holds no cell data for that index (a visible-only <see cref="RenderSnapshot"/> or an out-of-range index);
    /// callers then fall back to <see cref="GetScrollbackLineText"/>. The spans are valid until the source is mutated
    /// (live buffer) or disposed (snapshot).
    /// </summary>
    bool TryGetScrollbackRow(int index, out ReadOnlySpan<CellHot> hot, out ReadOnlySpan<ColdCell> cold);

    /// <summary>Diagnostic state string (debug overlay; never per-frame content).</summary>
    string GetDebugInfo();
}
