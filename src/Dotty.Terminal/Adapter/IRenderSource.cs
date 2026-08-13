using System;

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
    ReadOnlySpan<ulong> RowGenerations { get; }

    /// <summary>Zero-copy read-only view of one visible row's hot cells.</summary>
    ReadOnlySpan<CellHot> GetRowCells(int row);

    /// <summary>Zero-copy read-only view of one visible row's cold cells.</summary>
    ReadOnlySpan<ColdCell> GetRowColdCells(int row);

    /// <summary>Identity generation of one visible row (bump-only).</summary>
    ulong GetRowGeneration(int row);

    ref readonly CellAttributes GetStyle(ushort styleId);

    /// <summary>Text of one scrollback line, newest = 0.</summary>
    string GetScrollbackLineText(int index);

    /// <summary>Diagnostic state string (debug overlay; never per-frame content).</summary>
    string GetDebugInfo();
}
