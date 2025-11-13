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
        private readonly List<string> _scrollback = new();
        private readonly StringBuilder _currentLine = new();
        public int Columns { get; }
        public int Rows { get; }

        public TerminalBuffer(int rows = 24, int columns = 80)
        {
            Rows = rows;
            Columns = columns;
        }

        public IReadOnlyList<string> Scrollback => _scrollback;

        public void ClearScreen()
        {
            _scrollback.Clear();
            _currentLine.Clear();
        }

        public void ClearScrollback()
        {
            _scrollback.Clear();
        }

        public void AppendText(ReadOnlySpan<char> text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    int len = i - start;
                    _currentLine.Append(text.Slice(start, len).ToString());
                    _scrollback.Add(_currentLine.ToString());
                    _currentLine.Clear();
                    start = i + 1;
                }
            }

            if (start < text.Length)
            {
                _currentLine.Append(text.Slice(start).ToString());
            }
        }

        public string GetCurrentDisplay()
        {
            var sb = new StringBuilder();
            int start = Math.Max(0, _scrollback.Count - Rows + 1);
            for (int i = start; i < _scrollback.Count; i++)
            {
                sb.AppendLine(_scrollback[i]);
            }
            sb.Append(_currentLine.ToString());
            return sb.ToString();
        }
    }
}
