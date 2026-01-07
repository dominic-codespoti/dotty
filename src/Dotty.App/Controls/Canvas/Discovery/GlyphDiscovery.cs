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
