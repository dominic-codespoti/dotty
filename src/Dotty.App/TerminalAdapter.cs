using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Dotty.Terminal;

namespace Dotty.App
{
    /// <summary>
    /// Adapter that connects the parser callbacks to a TerminalBuffer and exposes a render event.
    /// Keeps responsibilities minimal: buffer management and render notification.
    /// </summary>
    public class TerminalAdapter : ITerminalHandler
    {
    private readonly TerminalBuffer _buffer;
    private TerminalBuffer.CellAttributes _currentAttributes = TerminalBuffer.CellAttributes.Default;
    // Prompt semantic collection removed: we let the shell render the prompt natively.

        public TerminalAdapter(int rows = 24, int columns = 80)
        {
            _buffer = new TerminalBuffer(rows, columns);
        }

        /// <summary>
        /// Raised when the display should be re-rendered. Argument is the full text to display for now.
        /// </summary>
        public event Action<string>? RenderRequested;
    // OSC / shell-integration diagnostics removed — we no longer collect or
    // process OSC markers in the adapter.

    // Expose underlying buffer for renderers to pull a snapshot
    public TerminalBuffer Buffer => _buffer;

        public void ResizeBuffer(int rows, int columns)
        {
            try
            {
                _buffer.Resize(rows, columns);
                RequestRender();
            }
            catch { }
        }

        public void OnPrint(ReadOnlySpan<char> text)
        {
            try
            {
                var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_ADAPTER");
                if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                {
                    try { Console.Error.WriteLine("[ADAPTER_PRINT] '" + text.ToString().Replace("\n", "\\n") + "'"); } catch { }
                }
            }
            catch { }

            var s = text.ToString();
            var plainText = StripAnsi(s);

            // Write printable text into the buffer with the current graphics attributes
            // (foreground/background/bold) as tracked by OnSetGraphicsRendition.
            // If reverse-video is active, swap foreground/background.
            _buffer.WriteText(text, _currentAttributes);
            RequestRender();
        }

        public void OnOperatingSystemCommand(ReadOnlySpan<char> payload)
        {
            // OSC handling removed — ignore OS command (OSC) payloads.
        }

        private static string StripAnsi(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            // Remove OSC sequences ESC ] ... BEL
            input = Regex.Replace(input, "\u001b\\].*?\u0007", string.Empty, RegexOptions.Singleline);
            // Remove CSI sequences ESC [ ... letter
            input = Regex.Replace(input, "\u001b\\[[0-9;?]*[ -/]*[@-~]", string.Empty);
            // Remove any remaining simple ESC sequences
            input = Regex.Replace(input, "\u001b.", string.Empty);
            return input;
        }

        // (TrimAfterLastNewline removed with prompt plumbing cleanup)

        public void OnClearScreen()
        {
            // Legacy full-screen clear -> translate to erase-display mode 2
            _buffer.EraseDisplay(2);
            RequestRender();
        }

        public void OnClearScrollback()
        {
            _buffer.ClearScrollback();
            RequestRender();
        }

        public void OnEraseDisplay(int mode)
        {
            _buffer.EraseDisplay(mode);
            RequestRender();
        }

        public void OnSetGraphicsRendition(ReadOnlySpan<char> parameters)
        {
            // Minimal SGR handling: track current foreground color so we can color prompt segments.
            var s = parameters.ToString();
            if (string.IsNullOrEmpty(s))
            {
                ResetAttributes();
                return;
            }

            var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                ResetAttributes();
                return;
            }

            int i = 0;
            while (i < parts.Length)
            {
                if (!int.TryParse(parts[i], out var code))
                {
                    i++;
                    continue;
                }

                switch (code)
                {
                    case 0:
                        ResetAttributes();
                        i++;
                        break;
                    case 1:
                        _currentAttributes.Bold = true;
                        i++;
                        break;
                    case 2:
                        _currentAttributes.Faint = true;
                        i++;
                        break;
                    case 3:
                        _currentAttributes.Italic = true;
                        i++;
                        break;
                    case 4:
                        _currentAttributes.Underline = true;
                        i++;
                        break;
                    case 7:
                        _currentAttributes.Inverse = true;
                        i++;
                        break;
                    case 22:
                        _currentAttributes.Bold = false;
                        _currentAttributes.Faint = false;
                        i++;
                        break;
                    case 23:
                        _currentAttributes.Italic = false;
                        i++;
                        break;
                    case 24:
                        _currentAttributes.Underline = false;
                        i++;
                        break;
                    case 27:
                        _currentAttributes.Inverse = false;
                        i++;
                        break;
                    case 39:
                        _currentAttributes.Foreground = null;
                        i++;
                        break;
                    case 49:
                        _currentAttributes.Background = null;
                        i++;
                        break;
                    case 59:
                        _currentAttributes.UnderlineColor = null;
                        i++;
                        break;
                    case 38:
                        if (!TryParseExtendedColor(parts, ref i, out var fg))
                        {
                            i++;
                        }
                        else
                        {
                            _currentAttributes.Foreground = fg;
                        }
                        break;
                    case 48:
                        if (!TryParseExtendedColor(parts, ref i, out var bg))
                        {
                            i++;
                        }
                        else
                        {
                            _currentAttributes.Background = bg;
                        }
                        break;
                    case 58:
                        if (!TryParseExtendedColor(parts, ref i, out var ul))
                        {
                            i++;
                        }
                        else
                        {
                            _currentAttributes.UnderlineColor = ul;
                        }
                        break;
                    default:
                        if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
                        {
                            _currentAttributes.Foreground = SgrCodeToHex(code);
                        }
                        else if ((code >= 40 && code <= 47) || (code >= 100 && code <= 107))
                        {
                            _currentAttributes.Background = SgrCodeToHexBackground(code);
                        }
                        i++;
                        break;
                }
            }
        }

        private void ResetAttributes()
        {
            _currentAttributes = TerminalBuffer.CellAttributes.Default;
        }

        private static bool TryParseExtendedColor(string[] parts, ref int index, out string? hex)
        {
            hex = null;
            if (index + 1 >= parts.Length)
            {
                return false;
            }

            var mode = parts[index + 1];
            if (mode == "2")
            {
                if (index + 4 < parts.Length &&
                    byte.TryParse(parts[index + 2], out var r) &&
                    byte.TryParse(parts[index + 3], out var g) &&
                    byte.TryParse(parts[index + 4], out var b))
                {
                    hex = $"#{r:X2}{g:X2}{b:X2}";
                    index += 5;
                    return true;
                }
                return false;
            }

            if (mode == "5")
            {
                if (index + 2 < parts.Length && int.TryParse(parts[index + 2], out var idx))
                {
                    hex = Sgr256ToHex(idx);
                    index += 3;
                    return true;
                }
                return false;
            }

            return false;
        }

        // Cursor and screen control -> forward to buffer and request render
        public void OnMoveCursor(int row, int col)
        {
            // API is 1-based coordinates; convert to 0-based and clamp inside buffer
            _buffer.SetCursor(Math.Max(0, row - 1), Math.Max(0, col - 1));
            RequestRender();
        }

        public void OnCursorUp(int n)
        {
            _buffer.MoveCursorBy(-Math.Max(1, n), 0);
            RequestRender();
        }

        public void OnCursorDown(int n)
        {
            _buffer.MoveCursorBy(Math.Max(1, n), 0);
            RequestRender();
        }

        public void OnCursorForward(int n)
        {
            _buffer.MoveCursorBy(0, Math.Max(1, n));
            RequestRender();
        }

        public void OnCursorBack(int n)
        {
            _buffer.MoveCursorBy(0, -Math.Max(1, n));
            RequestRender();
        }

        public void OnEraseLine(int mode)
        {
            _buffer.EraseLine(mode);
            RequestRender();
        }

        public void OnCarriageReturn()
        {
            _buffer.CarriageReturn();
            RequestRender();
        }

        public void OnLineFeed()
        {
            _buffer.LineFeed();
            RequestRender();
        }

        public void OnSetAlternateScreen(bool enabled)
        {
            _buffer.SetAlternateScreen(enabled);
            RequestRender();
        }

        public void OnSetCursorVisibility(bool visible)
        {
            _buffer.SetCursorVisible(visible);
            RequestRender();
        }

        private static string? SgrCodeToHex(int code)
        {
            return code switch
            {
                30 => "#000000",
                31 => "#AA0000",
                32 => "#00AA00",
                33 => "#AA5500",
                34 => "#0000AA",
                35 => "#AA00AA",
                36 => "#00AAAA",
                37 => "#AAAAAA",
                90 => "#555555",
                91 => "#FF5555",
                92 => "#55FF55",
                93 => "#FFFF55",
                94 => "#5555FF",
                95 => "#FF55FF",
                96 => "#55FFFF",
                97 => "#FFFFFF",
                _ => null,
            };
        }

        private static string? SgrCodeToHexBackground(int code)
        {
            // background codes are foreground + 10 (e.g., 40 -> 30, 100 -> 90)
            if (code >= 40 && code <= 47)
            {
                return SgrCodeToHex(code - 10);
            }
            if (code >= 100 && code <= 107)
            {
                return SgrCodeToHex(code - 10);
            }
            return null;
        }

        private static string? Sgr256ToHex(int idx)
        {
            if (idx < 0 || idx > 255) return null;
            if (idx <= 15)
            {
                // Map 0-7 -> 30-37, 8-15 -> 90-97
                int code = (idx < 8) ? (30 + idx) : (90 + (idx - 8));
                return SgrCodeToHex(code);
            }
            if (idx >= 16 && idx <= 231)
            {
                int c = idx - 16;
                int r = c / 36;
                int g = (c / 6) % 6;
                int b = c % 6;
                int R = r == 0 ? 0 : 55 + r * 40;
                int G = g == 0 ? 0 : 55 + g * 40;
                int B = b == 0 ? 0 : 55 + b * 40;
                return $"#{R:X2}{G:X2}{B:X2}";
            }
            // grayscale 232-255
            if (idx >= 232 && idx <= 255)
            {
                int gray = 8 + (idx - 232) * 10;
                if (gray < 0) gray = 0;
                if (gray > 255) gray = 255;
                return $"#{gray:X2}{gray:X2}{gray:X2}";
            }
            return null;
        }

        public void OnBell()
        {
            // No-op currently; could raise a sound or visual bell.
        }

        // Semantic prompt handling removed — we keep raw OSC diagnostics but
        // do not collect or strip prompt text from the buffer.

        private void RequestRender()
        {
            // Render a snapshot of the buffer; the renderer will decide how to
            // display the cursor. We no longer strip or hide the shell prompt.
            RenderRequested?.Invoke(_buffer.GetCurrentDisplay(showCursor: false, promptPrefix: null));
        }

        // Allow external callers to request a render (for example after user input)
        public void RequestRenderExtern()
        {
            RequestRender();
        }
    }

    // PromptSegment removed — semantic prompt plumbing cleaned up.
}
