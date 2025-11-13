using System;
using System.Threading;
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

        public TerminalAdapter(int rows = 24, int columns = 80)
        {
            _buffer = new TerminalBuffer(rows, columns);
        }

        /// <summary>
        /// Raised when the display should be re-rendered. Argument is the full text to display for now.
        /// </summary>
        public event Action<string>? RenderRequested;
    public event Action<string>? PromptDetected;

        public void OnPrint(ReadOnlySpan<char> text)
        {
            _buffer.AppendText(text);
            RequestRender();

            // Heuristic: if the printed chunk ends with a common prompt terminator, treat it as the shell prompt
            var s = text.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                if (s.EndsWith("$ ") || s.EndsWith("# ") || s.EndsWith("> ") || s.EndsWith("% "))
                {
                    PromptDetected?.Invoke(s);
                }
            }
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
            // No-op for now. In future we can track attributes per cell and include them in render.
        }

        public void OnBell()
        {
            // No-op currently; could raise a sound or visual bell.
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
}
