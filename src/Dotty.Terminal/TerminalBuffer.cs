using System;
using System.Globalization;
using System.Text;

namespace Dotty.Terminal
{
    /// <summary>
    /// Very small screen model for now: stores visible lines and a simple scrollback.
    /// Designed to be called from parser callbacks; it is not thread-safe by itself.
    /// </summary>
    public class TerminalBuffer
    {
        public struct Cell
        {
            public string? Grapheme;
            public string? Foreground;
            public string? Background;
            public bool Bold;
            public byte Width; // 0,1,2
            public bool IsContinuation;

            public void Reset()
            {
                Grapheme = null;
                Foreground = null;
                Background = null;
                Bold = false;
                Width = 0;
                IsContinuation = false;
            }

            public bool IsEmpty => string.IsNullOrEmpty(Grapheme) && !IsContinuation;
        }

        private Cell[,] _main;
        private Cell[,] _alt;
        private Cell[,]? _savedMain;
        private bool _usingAlt;

        public int Columns { get; }
        public int Rows { get; }

        public int CursorRow { get; private set; }
        public int CursorCol { get; private set; }
        public bool CursorVisible { get; private set; } = true;

        private bool _clearLineOnNextWrite;

        public TerminalBuffer(int rows = 24, int columns = 80)
        {
            Rows = rows;
            Columns = columns;
            _main = new Cell[Rows, Columns];
            _alt = new Cell[Rows, Columns];
            ClearScreen();
        }

        private Cell[,] ActiveBuffer => _usingAlt ? _alt : _main;

        public void ClearScreen()
        {
            ClearBuffer(_main);
            ClearBuffer(_alt);
            CursorRow = 0;
            CursorCol = 0;
            _clearLineOnNextWrite = false;
        }

        public void ClearScrollback()
        {
            ClearScreen();
        }

        private static void ClearBuffer(Cell[,] buf)
        {
            int rows = buf.GetLength(0);
            int cols = buf.GetLength(1);
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                ref var cell = ref buf[r, c];
                cell.Reset();
            }
        }

        public void SetCursor(int row, int col)
        {
            CursorRow = Math.Clamp(row, 0, Rows - 1);
            CursorCol = Math.Clamp(col, 0, Columns - 1);
        }

        public void MoveCursorBy(int dRow, int dCol)
        {
            SetCursor(CursorRow + dRow, CursorCol + dCol);
        }

        public void CarriageReturn()
        {
            CursorCol = 0;
            _clearLineOnNextWrite = true;
        }

        public void LineFeed()
        {
            CursorRow++;
            // Many outputs emit LF alone and expect the cursor to move to the start of the next line.
            // Reset column to 0 to avoid staircasing when applications rely on LF semantics.
            CursorCol = 0;
            if (CursorRow >= Rows)
            {
                ScrollUp(1);
                CursorRow = Rows - 1;
            }
        }

        public void EraseLine(int mode)
        {
            var buf = ActiveBuffer;
            if (mode == 2)
            {
                for (int j = 0; j < Columns; j++) ClearCell(buf, CursorRow, j);
            }
            else if (mode == 0)
            {
                for (int j = CursorCol; j < Columns; j++) ClearCell(buf, CursorRow, j);
            }
            else if (mode == 1)
            {
                for (int j = 0; j <= CursorCol; j++) ClearCell(buf, CursorRow, j);
            }
        }

        // Erase display according to CSI n J modes
        // mode 0: erase from cursor to end of screen
        // mode 1: erase from start of screen to cursor
        // mode 2: erase entire screen
        public void EraseDisplay(int mode)
        {
            var buf = ActiveBuffer;
            if (mode == 2)
            {
                ClearBuffer(buf);
                CursorRow = 0;
                CursorCol = 0;
                return;
            }

            if (mode == 0)
            {
                for (int j = CursorCol; j < Columns; j++) ClearCell(buf, CursorRow, j);
                for (int r = CursorRow + 1; r < Rows; r++)
                    for (int c = 0; c < Columns; c++) ClearCell(buf, r, c);
                return;
            }

            if (mode == 1)
            {
                for (int r = 0; r < CursorRow; r++)
                    for (int c = 0; c < Columns; c++) ClearCell(buf, r, c);
                for (int j = 0; j <= CursorCol; j++) ClearCell(buf, CursorRow, j);
                return;
            }
        }

        public void WriteText(ReadOnlySpan<char> text, string? foreground, string? background = null, bool bold = false)
        {
            if (text.IsEmpty)
            {
                return;
            }

            var enumerator = StringInfo.GetTextElementEnumerator(text.ToString());
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                
                // Handle CRLF specifically as it is a single grapheme cluster
                if (element == "\r\n")
                {
                    CarriageReturn();
                    LineFeed();
                    continue;
                }

                if (element.Length == 1 && TryHandleControlChar(element[0], foreground, background, bold))
                {
                    continue;
                }

                if (_clearLineOnNextWrite)
                {
                    ClearLineFromCursor();
                    _clearLineOnNextWrite = false;
                }

                WriteGrapheme(element, foreground, background, bold);
            }
        }

        private bool TryHandleControlChar(char ch, string? foreground, string? background, bool bold)
        {
            switch (ch)
            {
                case '\r':
                    CarriageReturn();
                    return true;
                case '\n':
                    LineFeed();
                    return true;
                case '\t':
                    WriteTab(foreground, background, bold);
                    return true;
                case '\b':
                case '\u007f':
                    ErasePreviousGlyph();
                    return true;
                default:
                    return char.IsControl(ch);
            }
        }

        private void WriteTab(string? foreground, string? background, bool bold)
        {
            int tabStop = 8;
            int spaces = tabStop - (CursorCol % tabStop);
            for (int i = 0; i < spaces; i++)
            {
                WriteGrapheme(" ", foreground, background, bold);
            }
        }

        private void ClearLineFromCursor()
        {
            var buf = ActiveBuffer;
            for (int j = CursorCol; j < Columns; j++)
            {
                ClearCell(buf, CursorRow, j);
            }
        }

        private void WriteGrapheme(string grapheme, string? foreground, string? background, bool bold)
        {
            if (string.IsNullOrEmpty(grapheme))
            {
                return;
            }

            int width = UnicodeWidth.GetWidth(grapheme);
            if (width == 0)
            {
                if (AttachCombiningMark(grapheme))
                {
                    return;
                }

                width = 1;
            }

            EnsureSpace(width);

            var buf = ActiveBuffer;
            ref var cell = ref buf[CursorRow, CursorCol];
            cell.Grapheme = grapheme;
            cell.Foreground = foreground;
            cell.Background = background;
            cell.Bold = bold;
            cell.Width = (byte)Math.Clamp(width, 1, 2);
            cell.IsContinuation = false;

            for (int i = 1; i < width; i++)
            {
                ref var cont = ref buf[CursorRow, CursorCol + i];
                cont.Reset();
                cont.IsContinuation = true;
                cont.Background = background;
                cont.Foreground = foreground;
                cont.Bold = bold;
            }

            AdvanceCursor(width);
        }

        private void EnsureSpace(int width)
        {
            if (width <= 0) width = 1;
            if (CursorCol > Columns - width)
            {
                CursorCol = 0;
                CursorRow++;
                if (CursorRow >= Rows)
                {
                    ScrollUp(1);
                    CursorRow = Rows - 1;
                }
            }
        }

        private void AdvanceCursor(int width)
        {
            CursorCol += width;
            while (CursorCol >= Columns)
            {
                CursorCol -= Columns;
                CursorRow++;
                if (CursorRow >= Rows)
                {
                    ScrollUp(1);
                    CursorRow = Rows - 1;
                }
            }
        }

        private bool AttachCombiningMark(string mark)
        {
            var (row, col) = GetPreviousBaseCell();
            if (row < 0)
            {
                return false;
            }

            var buf = ActiveBuffer;
            ref var cell = ref buf[row, col];
            if (string.IsNullOrEmpty(cell.Grapheme))
            {
                return false;
            }

            cell.Grapheme += mark;
            return true;
        }

        private (int row, int col) GetPreviousBaseCell()
        {
            int row = CursorRow;
            int col = CursorCol;

            if (row == 0 && col == 0)
            {
                return (-1, -1);
            }

            if (col == 0)
            {
                row--;
                col = Columns - 1;
            }
            else
            {
                col--;
            }

            var buf = ActiveBuffer;
            while (row >= 0)
            {
                ref var cell = ref buf[row, col];
                if (!cell.IsContinuation)
                {
                    if (!cell.IsEmpty)
                    {
                        return (row, col);
                    }

                    return (-1, -1);
                }

                if (col == 0)
                {
                    row--;
                    col = Columns - 1;
                }
                else
                {
                    col--;
                }
            }

            return (-1, -1);
        }

        private void ErasePreviousGlyph()
        {
            if (CursorRow == 0 && CursorCol == 0)
            {
                return;
            }

            MoveCursorBackward();

            var buf = ActiveBuffer;
            while (CursorRow >= 0)
            {
                ref var cell = ref buf[CursorRow, CursorCol];
                if (cell.IsContinuation)
                {
                    cell.Reset();
                    MoveCursorBackward();
                    continue;
                }

                if (!cell.IsEmpty)
                {
                    int width = Math.Max(1, (int)cell.Width);
                    cell.Reset();
                    for (int i = 1; i < width && CursorCol + i < Columns; i++)
                    {
                        ref var cont = ref buf[CursorRow, CursorCol + i];
                        if (!cont.IsContinuation)
                        {
                            break;
                        }
                        cont.Reset();
                    }
                }
                break;
            }
        }

        private void MoveCursorBackward()
        {
            if (CursorCol > 0)
            {
                CursorCol--;
            }
            else if (CursorRow > 0)
            {
                CursorRow--;
                CursorCol = Columns - 1;
            }
            else
            {
                CursorCol = 0;
                CursorRow = 0;
            }
        }

        private void ClearCell(Cell[,] buf, int row, int col)
        {
            if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            {
                return;
            }

            ref var cell = ref buf[row, col];
            int width = Math.Max(1, (int)cell.Width);
            bool isContinuation = cell.IsContinuation;
            cell.Reset();

            if (!isContinuation)
            {
                for (int i = 1; i < width && col + i < Columns; i++)
                {
                    ref var cont = ref buf[row, col + i];
                    if (!cont.IsContinuation)
                    {
                        break;
                    }
                    cont.Reset();
                }
            }
        }

        private void ScrollUp(int lines)
        {
            var buf = ActiveBuffer;
            for (int i = 0; i < Rows - lines; i++)
            for (int j = 0; j < Columns; j++)
                buf[i, j] = buf[i + lines, j];

            for (int i = Rows - lines; i < Rows; i++)
            for (int j = 0; j < Columns; j++)
            {
                ref var cell = ref buf[i, j];
                cell.Reset();
            }
        }

        public string GetCurrentDisplay(bool showCursor = false, string? promptPrefix = null)
        {
            var sb = new StringBuilder();
            var buf = ActiveBuffer;
            for (int i = 0; i < Rows; i++)
            {
                for (int j = 0; j < Columns; j++)
                {
                    var cell = buf[i, j];
                    if (cell.IsContinuation)
                    {
                        sb.Append(' ');
                    }
                    else if (string.IsNullOrEmpty(cell.Grapheme))
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        sb.Append(cell.Grapheme);
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Return the character at the given buffer coordinates.
        public char GetCharAt(int row, int col)
        {
            if (row < 0 || row >= Rows || col < 0 || col >= Columns) return '\0';
            var cell = ActiveBuffer[row, col];
            if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme))
            {
                return ' ';
            }

            return cell.Grapheme![0];
        }

        // Return a copy of the cell at the given coordinates. Safe for out-of-range queries.
        public Cell GetCell(int row, int col)
        {
            if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            {
                return new Cell { Grapheme = " ", Width = 1, Bold = false };
            }
            var c = ActiveBuffer[row, col];
            return c;
        }

        public string GetCurrentLine()
        {
            var buf = ActiveBuffer;
            var sb = new StringBuilder();
            for (int j = 0; j < Columns; j++)
            {
                var cell = buf[CursorRow, j];
                if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme))
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(cell.Grapheme);
                }
            }
            return sb.ToString().TrimEnd();
        }

        public void SetAlternateScreen(bool enable)
        {
            if (enable == _usingAlt) return;
            if (enable)
            {
                // save main buffer and switch to alt (clear alt)
                _savedMain = _main;
                _usingAlt = true;
                ClearBuffer(_alt);
                CursorRow = 0;
                CursorCol = 0;
            }
            else
            {
                // restore main buffer
                if (_savedMain != null)
                {
                    _main = _savedMain;
                    _savedMain = null;
                }
                _usingAlt = false;
                CursorRow = 0;
                CursorCol = 0;
            }
        }

        public void SetCursorVisible(bool visible)
        {
            CursorVisible = visible;
        }

    // Expose whether the alternate screen is active
    public bool UsingAlternate => _usingAlt;

        // Convenience: append printable text (no color info)
        public void AppendText(ReadOnlySpan<char> text)
        {
            WriteText(text, null, null, false);
        }

        // If the current line ends with the provided promptText, remove it from the buffer
        // and position the cursor at the prompt start. This is used to avoid duplicating the
        // prompt in the main display when the UI renders the prompt separately.
        public void RemoveTrailingPromptIfMatches(string promptText)
        {
            if (string.IsNullOrEmpty(promptText)) return;
            var line = GetCurrentLine(); // trimmed trailing spaces
            if (!line.EndsWith(promptText)) return;

            int start = line.Length - promptText.Length;
            var buf = ActiveBuffer;
            for (int j = start; j < Columns; j++)
            {
                ClearCell(buf, CursorRow, j);
            }

            CursorCol = Math.Clamp(start, 0, Columns - 1);
        }

    }
}
