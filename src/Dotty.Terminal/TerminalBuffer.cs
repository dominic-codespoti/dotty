using System;
using System.Collections.Generic;
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
            public char Ch;
            public string? Foreground;
            public string? Background;
            public bool Bold;
        }

        private Cell[,] _main;
        private Cell[,] _alt;
        private Cell[,]? _savedMain;
        private bool _usingAlt;

        public int Columns { get; }
        public int Rows { get; }

        public int CursorRow { get; private set; }
        public int CursorCol { get; private set; }

        public TerminalBuffer(int rows = 24, int columns = 80)
        {
            Rows = rows;
            Columns = columns;
            _main = new Cell[Rows, Columns];
            _alt = new Cell[Rows, Columns];
            _usingAlt = false;
            CursorRow = 0;
            CursorCol = 0;
            ClearScreen();
        }

        private Cell[,] ActiveBuffer => _usingAlt ? _alt : _main;
    // When a carriage return happens, many programs expect the following output
    // to overwrite the existing line. We use this flag to erase the remainder
    // of the current line on the next printable write.
    private bool _clearLineOnNextWrite = false;

        public void ClearScreen()
        {
            ClearBuffer(_main);
            ClearBuffer(_alt);
            CursorRow = 0;
            CursorCol = 0;
        }

        public void ClearScrollback()
        {
            // No-op for now; scrollback not implemented in grid mode.
            ClearScreen();
        }

        private static void ClearBuffer(Cell[,] buf)
        {
            int r = buf.GetLength(0);
            int c = buf.GetLength(1);
            for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
            {
                buf[i, j].Ch = ' ';
                buf[i, j].Foreground = null;
                buf[i, j].Background = null;
                buf[i, j].Bold = false;
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
                for (int j = 0; j < Columns; j++) buf[CursorRow, j].Ch = ' ';
            }
            else if (mode == 0)
            {
                for (int j = CursorCol; j < Columns; j++) buf[CursorRow, j].Ch = ' ';
            }
            else if (mode == 1)
            {
                for (int j = 0; j <= CursorCol; j++) buf[CursorRow, j].Ch = ' ';
            }
        }

        public void WriteText(ReadOnlySpan<char> text, string? foreground)
        {
            var buf = ActiveBuffer;
            foreach (var ch in text)
            {
                // Skip most C0 control characters except CR/LF/TAB which we handle
                if (ch != '\r' && ch != '\n' && ch != '\t' && (ch < ' ' || ch == '\u007f'))
                {
                    // Ignore other control characters
                    continue;
                }

                if (ch == '\r')
                {
                    CarriageReturn();
                    continue;
                }

                if (ch == '\n')
                {
                    LineFeed();
                    continue;
                }

                // If a CR was seen just before this printable, clear the remainder
                // of the line so we don't leave trailing characters from previous
                // longer outputs (prevents staircase artifacts).
                if (_clearLineOnNextWrite)
                {
                    // erase from cursor to end of line
                    var b = ActiveBuffer;
                    for (int j = CursorCol; j < Columns; j++) b[CursorRow, j].Ch = ' ';
                    _clearLineOnNextWrite = false;
                }

                // Handle tab by expanding to the next tab stop (8 columns)
                if (ch == '\t')
                {
                    int tabStop = 8;
                    int spaces = tabStop - (CursorCol % tabStop);
                    for (int s = 0; s < spaces; s++)
                    {
                        buf[CursorRow, CursorCol].Ch = ' ';
                        buf[CursorRow, CursorCol].Foreground = foreground;
                        CursorCol++;
                        if (CursorCol >= Columns)
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
                }
                else
                {
                    buf[CursorRow, CursorCol].Ch = ch;
                    buf[CursorRow, CursorCol].Foreground = foreground;
                    CursorCol++;
                    if (CursorCol >= Columns)
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
                if (CursorCol >= Columns)
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
        }

        private void ScrollUp(int lines)
        {
            var buf = ActiveBuffer;
            for (int i = 0; i < Rows - lines; i++)
            for (int j = 0; j < Columns; j++)
                buf[i, j] = buf[i + lines, j];

            for (int i = Rows - lines; i < Rows; i++)
            for (int j = 0; j < Columns; j++)
                buf[i, j].Ch = ' ';
        }

        public string GetCurrentDisplay(bool showCursor = false, string? promptPrefix = null)
        {
            var sb = new StringBuilder();
            var buf = ActiveBuffer;
            for (int i = 0; i < Rows; i++)
            {
                for (int j = 0; j < Columns; j++)
                {
                    var ch = buf[i, j].Ch;
                    if (ch == '\0') ch = ' ';
                    sb.Append(ch);
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public string GetCurrentLine()
        {
            var buf = ActiveBuffer;
            var sb = new StringBuilder();
            for (int j = 0; j < Columns; j++)
            {
                var ch = buf[CursorRow, j].Ch;
                if (ch == '\0') ch = ' ';
                sb.Append(ch);
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

        // Convenience: append printable text (no color info)
        public void AppendText(ReadOnlySpan<char> text)
        {
            WriteText(text, null);
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
                buf[CursorRow, j].Ch = ' ';
            }

            CursorCol = Math.Clamp(start, 0, Columns - 1);
        }

    }
}
