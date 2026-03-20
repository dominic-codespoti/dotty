using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Dotty.App.Controls;
using Dotty.Terminal.Adapter;
using Dotty.App.Input;

namespace Dotty.App.Views
{
    public partial class TerminalView : UserControl
    {
        private TerminalGrid? _grid;
        private TerminalCanvas? _canvas;
        private string _lineBuffer = string.Empty;
        private bool _suppressText = false;
        private readonly SelectionController _selectionController = new();
        private readonly SelectionContextMenuBuilder _contextMenuBuilder;
        private readonly TerminalInputEncoder _inputEncoder = new();

        public string? WorkingDirectory { get; set; }
        public event Action<byte[]>? RawInput;
        public event EventHandler<string?>? Submitted;

        public TerminalView()
        {
            _contextMenuBuilder = new SelectionContextMenuBuilder(_selectionController);
            InitializeComponent();
            AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _grid = this.FindControl<TerminalGrid>("PART_Grid");
            _canvas = _grid?.FindControl<TerminalCanvas>("PART_Canvas");

            AddHandler(KeyDownEvent, TerminalView_KeyDown, RoutingStrategies.Tunnel);
            AddHandler(TextInputEvent, TerminalView_TextInput, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, TerminalView_PointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, TerminalView_PointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, TerminalView_PointerReleased, RoutingStrategies.Tunnel);
        }

        private void TerminalView_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            EnsureCanvas();
            if (_canvas == null) return;
            var current = e.GetCurrentPoint(_canvas);
            
            // Handle right-click for context menu
            if (current.Properties.IsRightButtonPressed)
            {
                ShowContextMenu(e);
                return;
            }
            
            if (!current.Properties.IsLeftButtonPressed) return;
            if (!TryGetCellFromPointer(e, out int row, out int column)) return;
            
            // Handle double-click to select entire line
            if (e.ClickCount == 2)
            {
                var buffer = _canvas.Buffer;
                if (buffer != null)
                {
                    _selectionController.SelectLine(row, buffer.Columns);
                    UpdateCanvasSelection();
                }
                return;
            }
            
            _selectionController.BeginSelection(row, column);
            UpdateCanvasSelection();
            // Focus after selection to ensure keyboard input works
            try { Focus(); } catch { }
        }

        private void TerminalView_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_selectionController.IsDragging) return;
            if (!TryGetCellFromPointer(e, out int row, out int column)) return;
            _selectionController.UpdateSelection(row, column);
            UpdateCanvasSelection();
        }

        private void TerminalView_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_selectionController.IsDragging) return;
            _selectionController.EndSelection();
            UpdateCanvasSelection();
        }

        private void ShowContextMenu(PointerPressedEventArgs e)
        {
            var actions = new SelectionContextMenuBuilder.SelectionContextMenuActions(
                CopyAsync: CopySelectionAsync,
                PasteAsync: PasteFromClipboardAsync,
                SelectAll: SelectAll,
                ClearSelection: ClearSelection
            );
            var menu = _contextMenuBuilder.Build(actions);
            menu.Open(this);
        }

        private void ClearSelection()
        {
            _selectionController.Clear();
            UpdateCanvasSelection();
        }

        private void SelectAll()
        {
            EnsureCanvas();
            var buffer = _canvas?.Buffer;
            if (buffer == null) return;
            _selectionController.SelectAll(buffer.Rows, buffer.Columns);
            UpdateCanvasSelection();
        }

        private void EnsureCanvas()
        {
            if (_canvas != null) return;
            _grid ??= this.FindControl<TerminalGrid>("PART_Grid");
            _canvas = _grid?.FindControl<TerminalCanvas>("PART_Canvas");
        }

        private void TerminalView_KeyDown(object? sender, KeyEventArgs e)
        {
            var modifiers = e.KeyModifiers;
            if (modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift))
            {
                if (e.Key == Key.C && _selectionController.HasSelection)
                {
                    _ = CopySelectionAsync();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.V)
                {
                    _ = PasteFromClipboardAsync();
                    e.Handled = true;
                    return;
                }
            }

            var encoded = _inputEncoder.Encode(e.Key, e.KeyModifiers);
            if (encoded != null)
            {
                RawInput?.Invoke(encoded);
                if (e.Key == Key.Enter)
                {
                    Submitted?.Invoke(this, _lineBuffer);
                    _lineBuffer = string.Empty;
                }
                e.Handled = true;
                _suppressText = true;
                return;
            }

            _suppressText = false;
        }

        private void TerminalView_TextInput(object? sender, TextInputEventArgs e)
        {
            if (e.Text == null) return;
            if (_suppressText) return;

            var bytes = Encoding.UTF8.GetBytes(e.Text);
            RawInput?.Invoke(bytes);
            _lineBuffer += e.Text;
        }

        public void SetBuffer(TerminalBuffer buffer)
        {
            _grid?.SetBuffer(buffer);
            ResetSelection();
        }

        public void SetPlainText(string text)
        {
            _grid?.SetPlainText(text);
            ResetSelection();
        }

        public void AppendPlainText(string text)
        {
            _grid?.AppendPlainText(text);
            ResetSelection();
        }

        public void FocusInput()
        {
            try { Focus(); } catch { }
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
            ResetSelection();
        }

        private void ResetSelection()
        {
            // Don't clear selection while user is actively dragging
            if (_selectionController.IsDragging) return;
            _selectionController.Clear();
            UpdateCanvasSelection();
        }

        private void UpdateCanvasSelection()
        {
            if (_canvas == null) return;
            _canvas.SelectionRange = _selectionController.Range;
        }

        // Clear only the input line buffer (used after submit); does NOT clear the displayed terminal buffer
        public void ClearInput()
        {
            try { _lineBuffer = string.Empty; } catch { }
        }

        private bool TryGetCellFromPointer(PointerEventArgs e, out int row, out int column)
        {
            row = column = 0;
            EnsureCanvas();
            if (_canvas == null) return false;
            var buffer = _canvas.Buffer;
            if (buffer == null) return false;

            var position = e.GetPosition(_canvas);
            var padding = _canvas.ContentPadding;
            var x = position.X - padding.Left;
            var y = position.Y - padding.Top;
            x = Math.Max(0, x);
            y = Math.Max(0, y);

            var cellWidth = Math.Max(1.0, _canvas.CellWidth);
            var cellHeight = Math.Max(1.0, _canvas.CellHeight);

            column = (int)Math.Floor(x / cellWidth);
            
            // Adjust row for scrollback
            int scrollbackCount = buffer.ScrollbackCount;
            // The canvas handles visually shifting the viewport, we must convert pointer Y
            // into virtual row coordinates:
            // Since Y=0 is visually offset down inside the ScrollViewer, we must map 
            // the pointer coordinate Y to the physical pixel space. Wait, TerminalView receives 
            // e.GetPosition relative to the Canvas which is full height!
            // Let's verify: In Avalonia, pointer on a scrolled Canvas is already scaled to full Canvas bounds.
            row = (int)Math.Floor(y / cellHeight) - scrollbackCount;
            
            column = Math.Clamp(column, 0, buffer.Columns - 1);
            row = Math.Clamp(row, -scrollbackCount, buffer.Rows - 1);
            return true;
        }

        private async Task CopySelectionAsync()
        {
            if (_canvas?.Buffer == null) return;
            var text = _selectionController.ExtractText(_canvas.Buffer);
            if (string.IsNullOrEmpty(text)) return;

            var clipboard = GetClipboard();
            if (clipboard == null) return;
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch { }
        }

        private async Task PasteFromClipboardAsync()
        {
            var clipboard = GetClipboard();
            if (clipboard == null) return;
            try
            {
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    SendRawInput(text);
                }
            }
            catch { }
        }

        private IClipboard? GetClipboard()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            return topLevel?.Clipboard;
        }

        private void SendRawInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_suppressText) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            RawInput?.Invoke(bytes);
            _lineBuffer += text;
        }
    }
}
