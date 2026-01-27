using System;
using Dotty.Terminal.Adapter;
using Dotty.App.Rendering;

namespace Dotty.App.Discovery;

/// <summary>
/// Observes a TerminalBuffer for changed rows and tells a GlyphAtlas about glyphs
/// that appear in changed rows. Tracks per-row versions and only reacts to rows
/// that differ from the last seen version.
/// </summary>
public class GlyphDiscovery
{
    // versions removed; we simply process enqueued rows unconditionally
    private readonly GlyphAtlas _atlas;
    private readonly System.Collections.Generic.Queue<int> _pendingRows = new();
    private readonly System.Collections.Generic.HashSet<int> _pendingSet = new();

    public GlyphDiscovery(int rows, GlyphAtlas atlas)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
    }

    public void EnqueueRow(int row)
    {
        if (row < 0) return;
        if (_pendingSet.Contains(row)) return;
        _pendingSet.Add(row);
        _pendingRows.Enqueue(row);
    }

    public void EnqueueRows(System.Collections.Generic.IEnumerable<int> rows)
    {
        foreach (var r in rows) EnqueueRow(r);
    }

    /// <summary>
    /// Process up to maxRows pending row discoveries. This is budgeted to avoid
    /// doing unlimited work on the UI thread.
    /// </summary>
    public void Process(TerminalBuffer buffer, int maxRows)
    {
        if (buffer == null) return;
        if (maxRows <= 0) return;
        int processed = 0;
        while (_pendingRows.Count > 0 && processed < maxRows)
        {
            var row = _pendingRows.Dequeue();
            _pendingSet.Remove(row);
            if (row < 0 || row >= buffer.Rows) continue;
            // Process the enqueued row so glyph discovery populates the atlas.
            UpdateRow(buffer, row);
            processed++;
        }
    }

    public void EnsureSize(int rows)
    {
        // No-op: sizing for per-row version arrays removed.
    }

    public void UpdateRow(TerminalBuffer buffer, int row)
    {
        if (buffer == null) return;
        if (row < 0 || row >= buffer.Rows) return;

        // sizing not required
        // Row changed: enumerate glyphs and inform atlas
        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (cell.IsContinuation) continue;
            if (cell.IsEmpty) continue;

            var fg = cell.Foreground?.ToString();
            var key = new GlyphKey(cell.Grapheme ?? string.Empty, fg, cell.Bold);
            _atlas.EnsureGlyph(key);
        }

        // No-op: versions removed. Glyph discovery simply ensures glyphs exist in atlas.
    }
}
