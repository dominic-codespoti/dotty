using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

// DECRQCRA: CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y  →  DCS Pi ! ~ XXXX ST
public partial class TerminalAdapter
{
    private bool _checksumReportEnabled;

    /// <summary>
    /// Whether DECRQCRA requests are answered. Default <c>false</c>: the reply lets a program read screen
    /// contents back, so the host opts in (<c>vt.checksumReportEnabled</c>; the esctest host sets it directly).
    /// When false no reply is sent at all. Safe to set from any thread.
    /// </summary>
    public bool ChecksumReportEnabled
    {
        get => Volatile.Read(ref _checksumReportEnabled);
        set => Volatile.Write(ref _checksumReportEnabled, value);
    }

    /// <summary>
    /// <paramref name="id"/> is echoed back; the other fields are the raw parameters (0 = omitted/default).
    /// </summary>
    public void OnRequestChecksum(int id, int page, int top, int left, int bottom, int right)
    {
        if (!ChecksumReportEnabled)
            return;
        var handler = ReplyRequested;
        if (handler is null)
            return;

        int checksum = ComputeRectChecksum(page, top, left, bottom, right);

        // ESC P <id> ! ~ XXXX ESC \  (id is at most 10 digits)
        Span<char> reply = stackalloc char[32];
        reply[0] = '\x1b';
        reply[1] = 'P';
        int length = 2;
        id.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = '!';
        reply[length++] = '~';
        ((uint)checksum).TryFormat(reply[length..], out written, "X4");
        length += written;
        reply[length++] = '\x1b';
        reply[length++] = '\\';
        handler(reply[..length]);
    }

    /// <summary>
    /// Plain 16-bit sum of the code points in the rectangle (the xterm <c>checksumExtension</c> with the
    /// positive, no-attributes, no-trim, drawn and byte flags: not negated, no attribute bits). An unwritten
    /// cell counts as a space, the continuation half of a wide character is skipped, and combining marks
    /// are excluded (only the base code point of a cluster counts). Omitted bounds default to the whole
    /// screen; in origin mode (DECOM) rows are relative to the scroll region. An empty or out-of-range
    /// rectangle, or a page other than 0/1 (Dotty has a single page), sums to 0.
    /// </summary>
    private int ComputeRectChecksum(int page, int top, int left, int bottom, int right)
    {
        if (page > 1)
            return 0;

        int rows = _buffer.Rows;
        int cols = _buffer.Columns;
        int rowBase = 0;
        int regionRows = rows;
        if (_buffer.OriginMode)
        {
            _buffer.GetOriginRegionRows(out int regionTop, out int regionBottom);
            rowBase = regionTop;
            regionRows = regionBottom - regionTop + 1;
        }

        int firstRow = top <= 0 ? 1 : top;
        int firstCol = left <= 0 ? 1 : left;
        int lastRow = bottom <= 0 || bottom > regionRows ? regionRows : bottom;
        int lastCol = right <= 0 || right > cols ? cols : right;
        if (firstRow > lastRow || firstCol > lastCol)
            return 0;

        uint sum = 0;
        for (int row = rowBase + firstRow - 1; row <= rowBase + lastRow - 1; row++)
        {
            ReadOnlySpan<CellHot> cells = _buffer.GetRowCells(row);
            int end = Math.Min(lastCol, cells.Length);
            for (int col = firstCol - 1; col < end; col++)
            {
                CellHot cell = cells[col];
                if (cell.IsContinuation)
                    continue;
                sum = unchecked(sum + (cell.Rune == 0 ? 0x20u : cell.Rune));
            }
        }

        return (int)(sum & 0xFFFF);
    }
}
