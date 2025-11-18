using System;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Avalonia.Threading;
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
        private string? _currentForeground; // hex like #RRGGBB
        private readonly List<PromptSegment> _recentSegments = new();
        private bool _semanticPromptSupported;
        private bool _collectingSemanticPrompt;
        private readonly StringBuilder _semanticPromptBuilder = new();
        private readonly List<PromptSegment> _semanticPromptSegments = new();

        public TerminalAdapter(int rows = 24, int columns = 80)
        {
            _buffer = new TerminalBuffer(rows, columns);
        }

        /// <summary>
        /// Raised when the display should be re-rendered. Argument is the full text to display for now.
        /// </summary>
        public event Action<string>? RenderRequested;
    public event Action<string>? PromptDetected;
    public event Action<List<PromptSegment>>? PromptSegmentsDetected;
    // Diagnostic: raw OSC payloads received (for debugging emitter content)
    public event Action<string>? RawOscReceived;
    public event Action<string>? CwdDetected;
    public event Action<int>? StatusUpdated;

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

            // Record segment with the current tracked foreground color (use sanitized text)
            var seg = new PromptSegment { Text = plainText, Foreground = _currentForeground };
            _recentSegments.Add(seg);
            if (_recentSegments.Count > 256) _recentSegments.RemoveRange(0, _recentSegments.Count - 256);

            if (_collectingSemanticPrompt && !string.IsNullOrEmpty(plainText))
            {
                _semanticPromptBuilder.Append(plainText);
                _semanticPromptSegments.Add(new PromptSegment
                {
                    Text = plainText,
                    Foreground = _currentForeground
                });
            }

            _buffer.AppendText(text);
            RequestRender();

            if (!_semanticPromptSupported && !string.IsNullOrEmpty(plainText))
            {
                if (plainText.EndsWith("$ ") || plainText.EndsWith("# ") || plainText.EndsWith("> ") || plainText.EndsWith("% "))
                {
                    var promptText = _buffer.GetCurrentLine()
                        .Replace("\r", string.Empty)
                        .Replace("\n", string.Empty);

                    if (!string.IsNullOrEmpty(promptText))
                    {
                        PromptDetected?.Invoke(promptText);

                        var segments = new List<PromptSegment>();
                        int needed = promptText.Length;
                        for (int i = _recentSegments.Count - 1; i >= 0 && needed > 0; i--)
                        {
                            var ps = _recentSegments[i];
                            var segmentText = TrimAfterLastNewline(ps.Text);
                            if (string.IsNullOrEmpty(segmentText))
                                continue;

                            segments.Insert(0, new PromptSegment
                            {
                                Text = segmentText,
                                Foreground = ps.Foreground,
                                Background = ps.Background,
                                Bold = ps.Bold,
                                Italic = ps.Italic,
                                Underline = ps.Underline
                            });

                            needed -= segmentText.Length;
                        }
                        if (segments.Count > 0)
                        {
                            PromptSegmentsDetected?.Invoke(segments);
                        }

                        _buffer.RemoveTrailingPromptIfMatches(promptText);
                        RequestRender();
                    }
                }
            }
        }

        public void OnOperatingSystemCommand(ReadOnlySpan<char> payload)
        {
            // Expect payload in the form "1338;<base64-json>" as produced by the helper rcfile
            var s = payload.ToString();
            RawOscReceived?.Invoke(s);
            if (HandleSemanticPromptOsc(s))
            {
                return;
            }
            if (s.StartsWith("1338;"))
            {
                var b64 = s.Substring(5);
                try
                {
                    var bytes = Convert.FromBase64String(b64);
                    var json = System.Text.Encoding.UTF8.GetString(bytes);

                    System.Text.Json.JsonDocument? doc = null;
                    try
                    {
                        doc = System.Text.Json.JsonDocument.Parse(json);
                    }
                    catch
                    {
                        // Some shells may emit raw ESC bytes inside the JSON string (invalid JSON).
                        // Try to sanitize by escaping literal ESC (0x1B) so the parser can succeed.
                        try
                        {
                            var sanitized = json.Replace("\u001b", "\\u001b");
                            sanitized = sanitized.Replace("\x1b", "\\u001b");
                            sanitized = sanitized.Replace(((char)27).ToString(), "\\u001b");
                            doc = System.Text.Json.JsonDocument.Parse(sanitized);
                        }
                        catch
                        {
                            // give up
                            doc = null;
                        }
                    }

                    if (doc != null)
                    {
                        if (doc.RootElement.TryGetProperty("cwd", out var cwd))
                        {
                            CwdDetected?.Invoke(cwd.GetString() ?? string.Empty);
                        }
                        if (doc.RootElement.TryGetProperty("status", out var status))
                        {
                            try { StatusUpdated?.Invoke(status.GetInt32()); } catch { }
                        }
                    }
                }
                catch
                {
                    // Ignore malformed markers
                }
            }
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

        private static string TrimAfterLastNewline(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            int lastIndex = text.LastIndexOfAny(new[] { '\r', '\n' });
            if (lastIndex >= 0)
            {
                if (lastIndex + 1 >= text.Length)
                    return string.Empty;

                return text.Substring(lastIndex + 1);
            }

            return text;
        }

        public void OnClearScreen()
        {
            _buffer.ClearScreen();
            RequestRender();
        }

        public void OnClearScrollback()
        {
            _buffer.ClearScrollback();
            RequestRender();
        }

        public void OnSetGraphicsRendition(ReadOnlySpan<char> parameters)
        {
            // Minimal SGR handling: track current foreground color so we can color prompt segments.
            var s = parameters.ToString();
            if (string.IsNullOrEmpty(s)) return;
            var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < parts.Length)
            {
                if (int.TryParse(parts[i], out var code))
                {
                    if (code == 39)
                    {
                        _currentForeground = null; // reset to default
                        i++;
                    }
                    else if (code == 38 && i + 1 < parts.Length && parts[i + 1] == "2")
                    {
                        // truecolor: 38;2;r;g;b
                        if (i + 4 < parts.Length && byte.TryParse(parts[i + 2], out var r) && byte.TryParse(parts[i + 3], out var g) && byte.TryParse(parts[i + 4], out var b))
                        {
                            _currentForeground = $"#{r:X2}{g:X2}{b:X2}";
                            i += 5;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    else if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
                    {
                        _currentForeground = SgrCodeToHex(code);
                        i++;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }
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

        public void OnBell()
        {
            // No-op currently; could raise a sound or visual bell.
        }

        private bool HandleSemanticPromptOsc(string payload)
        {
            const string prefix = "133;";
            if (string.IsNullOrEmpty(payload) || !payload.StartsWith(prefix) || payload.StartsWith("1338;"))
            {
                return false;
            }

            _semanticPromptSupported = true;
            var rest = payload.Substring(prefix.Length);
            if (string.IsNullOrEmpty(rest))
            {
                return true;
            }

            var parts = rest.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return true;
            }

            switch (parts[0])
            {
                case "A":
                case "P":
                    StartSemanticPromptCollection();
                    break;
                case "B":
                case "I":
                case "N":
                    FlushSemanticPromptCollection();
                    break;
                default:
                    break;
            }

            return true;
        }

        private void StartSemanticPromptCollection()
        {
            if (_collectingSemanticPrompt)
            {
                FlushSemanticPromptCollection();
            }

            _semanticPromptSupported = true;
            _collectingSemanticPrompt = true;
            _semanticPromptBuilder.Clear();
            _semanticPromptSegments.Clear();
        }

        private void FlushSemanticPromptCollection()
        {
            if (!_collectingSemanticPrompt)
            {
                _semanticPromptBuilder.Clear();
                _semanticPromptSegments.Clear();
                return;
            }

            _collectingSemanticPrompt = false;
            var promptText = _semanticPromptBuilder.ToString();
            if (string.IsNullOrEmpty(promptText))
            {
                _semanticPromptBuilder.Clear();
                _semanticPromptSegments.Clear();
                return;
            }

            PromptDetected?.Invoke(promptText);
            if (_semanticPromptSegments.Count > 0)
            {
                PromptSegmentsDetected?.Invoke(new List<PromptSegment>(_semanticPromptSegments));
            }

            _buffer.RemoveTrailingPromptIfMatches(promptText);
            RequestRender();

            _semanticPromptBuilder.Clear();
            _semanticPromptSegments.Clear();
        }

        private void RequestRender()
        {
            // Do not include prompt or cursor in the main display; the UI input area shows the prompt and the caret.
            RenderRequested?.Invoke(_buffer.GetCurrentDisplay(showCursor: false, promptPrefix: null));
        }

        // Allow external callers to request a render (for example after user input)
        public void RequestRenderExtern()
        {
            RequestRender();
        }
    }

    /// <summary>
    /// A small model representing a piece of prompt text and an optional foreground color (hex string).
    /// </summary>
    public class PromptSegment
    {
        public string? Text { get; set; }
        public string? Foreground { get; set; }
            public string? Background { get; set; }
            public bool Bold { get; set; }
            public bool Italic { get; set; }
            public bool Underline { get; set; }
    }
}
