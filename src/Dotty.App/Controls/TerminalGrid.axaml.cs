using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls
{
    public partial class TerminalGrid : UserControl
    {
        private TerminalBuffer? _lastBuffer;
        private bool _blinkOn = true;
        private DispatcherTimer? _blinkTimer;

        public static readonly StyledProperty<Thickness> CanvasPaddingProperty =
            AvaloniaProperty.Register<TerminalGrid, Thickness>(nameof(CanvasPadding), new Thickness(16, 12, 16, 16));

        public static readonly StyledProperty<TerminalCursorShape> CursorShapeProperty =
            AvaloniaProperty.Register<TerminalGrid, TerminalCursorShape>(nameof(CursorShape), TerminalCursorShape.Block);

        public static readonly StyledProperty<TimeSpan> CursorBlinkIntervalProperty =
            AvaloniaProperty.Register<TerminalGrid, TimeSpan>(nameof(CursorBlinkInterval), TimeSpan.FromMilliseconds(600));

        public Thickness CanvasPadding
        {
            get => GetValue(CanvasPaddingProperty);
            set => SetValue(CanvasPaddingProperty, value);
        }

        public TerminalCursorShape CursorShape
        {
            get => GetValue(CursorShapeProperty);
            set => SetValue(CursorShapeProperty, value);
        }

        public TimeSpan CursorBlinkInterval
        {
            get => GetValue(CursorBlinkIntervalProperty);
            set => SetValue(CursorBlinkIntervalProperty, value);
        }

        public TerminalGrid()
        {
            InitializeComponent();
            this.PropertyChanged += OnStyledPropertyChanged;
            StartBlinkLoop();
        }

        private TerminalCanvas? Canvas => this.FindControl<TerminalCanvas>("PART_Canvas");
        private ScrollViewer? Scroll => this.FindControl<ScrollViewer>("PART_Scroll");

        public void SetBuffer(TerminalBuffer buffer)
        {
            if (buffer == null) return;
            _lastBuffer = buffer;

            Action action = () =>
            {
                try
                {
                    var canvas = Canvas;
                    if (canvas == null) return;
                    // Assign the incoming buffer instance. TerminalCanvas now preserves
                    // its composer/front-buffer state on property changes, so replacing
                    // the buffer here is safe and supports callers that provide a
                    // TerminalBuffer instance from elsewhere (e.g. TerminalAdapter).
                    canvas.Buffer = buffer;
                    canvas.CursorShape = CursorShape;
                    canvas.ShowCursor = _blinkOn;
                    try { canvas.OnBufferUpdated(buffer); } catch { }
                    try { canvas.RequestFrame(); } catch { }
                    try { Scroll?.ScrollToEnd(); } catch { }
                }
                catch { }
            };

            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }

        private void StartBlinkLoop()
        {
            try
            {
                _blinkTimer = new DispatcherTimer
                {
                    Interval = GetBlinkInterval()
                };
                _blinkTimer.Tick += BlinkTimerOnTick;
                _blinkTimer.Start();
            }
            catch { }
        }

        private void OnStyledPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == CursorBlinkIntervalProperty)
            {
                try
                {
                    if (_blinkTimer != null)
                    {
                        _blinkTimer.Interval = GetBlinkInterval();
                    }
                }
                catch { }
            }
            else if (e.Property == CursorShapeProperty)
            {
                try
                {
                    var canvas = Canvas;
                    if (canvas != null)
                    {
                        canvas.CursorShape = CursorShape;
                        canvas.InvalidateVisual();
                    }
                }
                catch { }
            }
        }

        private void ToggleCursor()
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
        }

        private TimeSpan GetBlinkInterval()
        {
            var interval = CursorBlinkInterval;
            if (interval <= TimeSpan.Zero)
            {
                interval = TimeSpan.FromMilliseconds(600);
            }
            return interval;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            try
            {
                if (_blinkTimer != null)
                {
                    _blinkTimer.Stop();
                    _blinkTimer.Tick -= BlinkTimerOnTick;
                    _blinkTimer = null;
                }
            }
            catch { }
            try { this.PropertyChanged -= OnStyledPropertyChanged; } catch { }
            base.OnDetachedFromVisualTree(e);
        }

        private void BlinkTimerOnTick(object? sender, EventArgs e)
        {
            ToggleCursor();
            if (_blinkTimer != null)
            {
                _blinkTimer.Interval = GetBlinkInterval();
            }
        }

        public void SetPlainText(string text)
        {
            try
            {
                if (_lastBuffer == null)
                {
                    _lastBuffer = new TerminalBuffer(24, 120);
                }

                // Treat the incoming plain text as content to write into the
                // existing buffer in-place. This preserves row identity and
                // lets the buffer behave like a mutable terminal grid.
                _lastBuffer.ClearScreen();
                _lastBuffer.WriteText(text.AsSpan(), null, null, false);
                _lastBuffer.SetCursorVisible(false);
                SetBuffer(_lastBuffer);
            }
            catch { }
        }

        public void AppendPlainText(string text)
        {
            try
            {
                // Append text into the existing buffer instead of rebuilding a
                // new buffer from the concatenated display. Rebuilding loses
                // row identity and breaks in-place updates (causes duplication).
                if (_lastBuffer == null)
                {
                    SetPlainText(text);
                    return;
                }

                _lastBuffer.WriteText(text.AsSpan(), null, null, false);
                SetBuffer(_lastBuffer);
            }
            catch { }
        }

        private static TerminalBuffer BuildBufferFromPlainText(string text)
        {
            // Keep this helper for parity but prefer SetPlainText/AppendPlainText
            // which now mutate an existing buffer in-place. This helper still
            // produces a fresh buffer when callers explicitly need one.
            var buffer = new TerminalBuffer(24, 120);
            buffer.WriteText(text.AsSpan(), null, null, false);
            buffer.SetCursorVisible(false);
            return buffer;
        }
    }
}
