using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dotty.Terminal;

namespace Dotty.App.Controls
{
    public partial class TerminalView : UserControl
    {
        private TerminalGrid? _grid;
    private TerminalCanvas? _canvas;
        private string _lineBuffer = string.Empty;
        private bool _suppressText = false;

        public event Action<byte[]>? RawInput;
        public event EventHandler<string?>? Submitted;

        public TerminalView()
        {
            InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _grid = this.FindControl<TerminalGrid>("PART_Grid");
            _canvas = _grid?.FindControl<TerminalCanvas>("PART_Canvas");
            // Focusable border is the root; subscribe to input events
            this.AddHandler(KeyDownEvent, TerminalView_KeyDown, RoutingStrategies.Tunnel);
            this.AddHandler(TextInputEvent, TerminalView_TextInput, RoutingStrategies.Tunnel);
            this.AddHandler(PointerPressedEvent, TerminalView_PointerPressed, RoutingStrategies.Tunnel);
        }

        private void TerminalView_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            try { this.Focus(); } catch { }
        }

        private void TerminalView_TextInput(object? sender, TextInputEventArgs e)
        {
            if (e.Text == null) return;
            if (_suppressText) return;
            var bytes = Encoding.UTF8.GetBytes(e.Text);
            RawInput?.Invoke(bytes);
            // update line buffer for simple line editing (used by Submitted)
            _lineBuffer += e.Text;
        }

        private void TerminalView_KeyDown(object? sender, KeyEventArgs e)
        {
            // Handle special keys
            if (e.Key == Key.Enter)
            {
                // send LF to pty
                RawInput?.Invoke(new byte[] { (byte)'\n' });
                // raise submitted with current line buffer
                Submitted?.Invoke(this, _lineBuffer);
                _lineBuffer = string.Empty;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Back)
            {
                // DEL to pty
                RawInput?.Invoke(new byte[] { 0x7f });
                if (_lineBuffer.Length > 0) _lineBuffer = _lineBuffer.Substring(0, _lineBuffer.Length - 1);
                e.Handled = true;
                return;
            }

            // Arrow keys -> send escape sequences
            switch (e.Key)
            {
                case Key.Left:
                    RawInput?.Invoke(Encoding.UTF8.GetBytes("\u001b[D")); e.Handled = true; break;
                case Key.Right:
                    RawInput?.Invoke(Encoding.UTF8.GetBytes("\u001b[C")); e.Handled = true; break;
                case Key.Up:
                    RawInput?.Invoke(Encoding.UTF8.GetBytes("\u001b[A")); e.Handled = true; break;
                case Key.Down:
                    RawInput?.Invoke(Encoding.UTF8.GetBytes("\u001b[B")); e.Handled = true; break;
                default:
                    break;
            }
        }

        public void SetBuffer(TerminalBuffer buffer)
        {
            _grid?.SetBuffer(buffer);
        }

        public void SetPlainText(string text)
        {
            _grid?.SetPlainText(text);
        }

        public void AppendPlainText(string text)
        {
            _grid?.AppendPlainText(text);
        }

        public void FocusInput()
        {
            try { this.Focus(); } catch { }
        }

        public bool TryGetTerminalMetrics(out double cellWidth, out double cellHeight, out Thickness padding)
        {
            padding = _grid?.CanvasPadding ?? new Thickness(0);

            double fontSize = FontSize;
            if (double.IsNaN(fontSize) || fontSize <= 0)
            {
                fontSize = 13.0;
            }

            cellWidth = Math.Max(4.0, fontSize * 0.6);
            cellHeight = Math.Max(8.0, fontSize * 1.2);

            if (_canvas == null)
            {
                _canvas = _grid?.FindControl<TerminalCanvas>("PART_Canvas");
            }

            if (_canvas == null)
            {
                return false;
            }

            cellWidth = Math.Max(1.0, _canvas.CellWidth);
            cellHeight = Math.Max(1.0, _canvas.CellHeight);
            return true;
        }

        public void Clear()
        {
            try { _grid?.SetPlainText(string.Empty); } catch { }
        }

        // Clear only the input line buffer (used after submit); does NOT clear the displayed terminal buffer
        public void ClearInput()
        {
            try { _lineBuffer = string.Empty; } catch { }
        }


        public string? WorkingDirectory { get; set; }
    }
}
