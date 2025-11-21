using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dotty.Terminal;

namespace Dotty.App.Controls
{
    public partial class TerminalGrid : UserControl
    {
        private TerminalBuffer? _lastBuffer;
        private bool _blinkOn = true;
        private CancellationTokenSource? _blinkCts;

        public TerminalGrid()
        {
            InitializeComponent();
            StartBlinkLoop();
        }

        private TerminalCanvas? Canvas => this.FindControl<TerminalCanvas>("PART_Canvas");
        private ScrollViewer? Scroll => this.FindControl<ScrollViewer>("PART_Scroll");

        public void SetBuffer(TerminalBuffer buffer)
        {
            if (buffer == null) return;
            _lastBuffer = buffer;

                Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var canvas = Canvas;
                    if (canvas == null) return;
                    canvas.Buffer = buffer;
                    canvas.ShowCursor = _blinkOn;
                    canvas.InvalidateVisual();
                    try { Scroll?.ScrollToEnd(); } catch { }
                }
                catch { }
            });
        }

        private void StartBlinkLoop()
        {
            try
            {
                _blinkCts = new CancellationTokenSource();
                var ct = _blinkCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(600, ct); } catch (TaskCanceledException) { break; }
                        Dispatcher.UIThread.Post(() =>
                        {
                            _blinkOn = !_blinkOn;
                            try
                            {
                                if (_lastBuffer != null)
                                {
                                    var canvas = Canvas;
                                    if (canvas != null)
                                    {
                                        canvas.ShowCursor = _blinkOn;
                                        canvas.InvalidateVisual();
                                    }
                                }
                            }
                            catch { }
                        });
                    }
                }, ct);
            }
            catch { }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            try { _blinkCts?.Cancel(); } catch { }
            base.OnDetachedFromVisualTree(e);
        }

        public void SetPlainText(string text)
        {
            try
            {
                var buffer = BuildBufferFromPlainText(text);
                SetBuffer(buffer);
            }
            catch { }
        }

        public void AppendPlainText(string text)
        {
            try
            {
                var sb = new StringBuilder();
                if (_lastBuffer != null)
                {
                    sb.Append(_lastBuffer.GetCurrentDisplay());
                }
                sb.Append(text);
                SetPlainText(sb.ToString());
            }
            catch { }
        }

        private static TerminalBuffer BuildBufferFromPlainText(string text)
        {
            var buffer = new TerminalBuffer(24, 120);
            buffer.WriteText(text.AsSpan(), null, null, false);
            buffer.SetCursorVisible(false);
            return buffer;
        }
    }
}
