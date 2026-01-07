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
    private int[] _lastSeenRowVersions;
    private readonly GlyphAtlas _atlas;
    private readonly System.Collections.Generic.Queue<int> _pendingRows = new();
    private readonly System.Collections.Generic.HashSet<int> _pendingSet = new();

    public GlyphDiscovery(int rows, GlyphAtlas atlas)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        _lastSeenRowVersions = new int[Math.Max(0, rows)];
    }

    public void EnsureSize(int rows)
    {
        if (rows == _lastSeenRowVersions.Length) return;
        var newArr = new int[rows];
        var copy = Math.Min(rows, _lastSeenRowVersions.Length);
        Array.Copy(_lastSeenRowVersions, newArr, copy);
        _lastSeenRowVersions = newArr;
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
        EnsureSize(buffer.Rows);

        int processed = 0;
        while (_pendingRows.Count > 0 && processed < maxRows)
        {
            var row = _pendingRows.Dequeue();
            _pendingSet.Remove(row);
            if (row < 0 || row >= buffer.Rows) continue;
            var ver = buffer.GetRowVersion(row);
            if (_lastSeenRowVersions[row] == ver) continue;
            UpdateRow(buffer, row);
            _lastSeenRowVersions[row] = ver;
            processed++;
        }
    }

    public bool HasRowChanged(TerminalBuffer buffer, int row)
    {
        if (buffer == null) return false;
        if (row < 0 || row >= buffer.Rows) return false;
        EnsureSize(buffer.Rows);
        return buffer.GetRowVersion(row) != _lastSeenRowVersions[row];
    }

    public void UpdateRow(TerminalBuffer buffer, int row)
    {
        if (buffer == null) return;
        if (row < 0 || row >= buffer.Rows) return;

        EnsureSize(buffer.Rows);

        var ver = buffer.GetRowVersion(row);
        if (_lastSeenRowVersions[row] == ver) return;

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

        _lastSeenRowVersions[row] = ver;
    }
}
