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
        private TerminalBuffer? _lastBuffer;

        public string? WorkingDirectory { get; set; }
        public bool KeypadApplicationMode { get; set; }

        private Dotty.App.ViewModels.TerminalSession? _session;
        private Action<TimeSpan>? _fpsMeasurementCallback;
        private TimeSpan _lastFrameTime;
        private bool _renderUpdatePending;
        private int _lastCols = -1;
        private int _lastRows = -1;

        
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_session != null)
            {
                _session.RenderScheduled -= OnRenderScheduled;
                this.RawInput -= _session.WriteInput;
            }
            
            // Remove input handlers to prevent accumulation
            RemoveHandler(KeyDownEvent, TerminalView_KeyDown);
            RemoveHandler(TextInputEvent, TerminalView_TextInput);
            RemoveHandler(PointerPressedEvent, TerminalView_PointerPressed);
            RemoveHandler(PointerMovedEvent, TerminalView_PointerMoved);
            RemoveHandler(PointerReleasedEvent, TerminalView_PointerReleased);
        }
        
        public Dotty.App.ViewModels.TerminalSession? Session
        {
            get => _session;
            set
            {
                // Only update if it's a different session
                if (_session == value) return;
                
                // Unsubscribe from old session if exists
                if (_session != null)
                {
                    _session.RenderScheduled -= OnRenderScheduled;
                    this.RawInput -= _session.WriteInput;
                }
                
                _session = value;
                
                if (_session != null)
                {
                    // Connect handlers but don't resize - resize happens via OnSizeChanged
                    _session.RenderScheduled += OnRenderScheduled;
                    // Don't call UpdateSize here - it triggers a resize signal
                    // The initial resize will happen when the view is measured
                }
            }
        }
        
        private void OnMeasureRefreshRate(TimeSpan currentTime)
        {
            if (_session != null)
            {
                if (_lastFrameTime != TimeSpan.Zero && currentTime > _lastFrameTime)
                {
                    var delta = (currentTime - _lastFrameTime).TotalSeconds;
                    if (delta > 0 && delta < 0.25) // Ignore suspensions/huge gaps
                    {
                        // Set TargetFps based on the RequestAnimationFrame interval
                        // E.g. 1 / 0.01666... = ~60 FPS
                        _session.TargetFps = (int)Math.Round(1.0 / delta);
                    }
                }

                _lastFrameTime = currentTime;
                
                // Keep polling to dynamically adapt to monitor moves
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_fpsMeasurementCallback!); 
            }
        }

        private void OnRenderScheduled()
        {
            if (_renderUpdatePending) return;
            _renderUpdatePending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _renderUpdatePending = false;
                if (_session?.Adapter != null)
                {
                    KeypadApplicationMode = _session.Adapter.KeypadApplicationMode;
                    CursorShape = _session.Adapter.CursorShape;
                    SetBuffer(_session.Adapter.Buffer);
                }
            });
        }
        
        private void UpdateSize()
        {
            if (_session == null || _grid == null || _canvas == null) return;
            
            var bounds = this.Bounds;
            if (bounds.Width == 0 || bounds.Height == 0) return;
            
            if (TryGetTerminalMetrics(out var cellWidth, out var cellHeight, out var padding))
            {
                int cols = (int)Math.Max(1, (bounds.Width - padding.Left - padding.Right) / cellWidth);
                int rows = (int)Math.Max(1, (bounds.Height - padding.Top - padding.Bottom) / cellHeight);
                
                // Only resize if size actually changed - prevents shell prompt redraw
                if (cols != _lastCols || rows != _lastRows)
                {
                    _lastCols = cols;
                    _lastRows = rows;
                    _session.Resize(cols, rows);
                }
            }
        }


        private int _cursorShape = 0;
        public int CursorShape
        {
            get => _cursorShape;
            set
            {
                if (_cursorShape != value)
                {
                    _cursorShape = value;
                    UpdateCursorShape();
                }
            }
        }

        private void UpdateCursorShape()
        {
            if (_grid == null) return;
            
            // DECSCUSR mapping to TerminalCursorShape (Block, Beam, Underline)
            TerminalCursorShape shape = _cursorShape switch
            {
                0 => TerminalCursorShape.Block,      // Default
                1 => TerminalCursorShape.Block,      // Blinking Block
                2 => TerminalCursorShape.Block,      // Steady Block
                3 => TerminalCursorShape.Underline,  // Blinking Underline
                4 => TerminalCursorShape.Underline,  // Steady Underline
                5 => TerminalCursorShape.Beam,       // Blinking Bar
                6 => TerminalCursorShape.Beam,       // Steady Bar
                _ => TerminalCursorShape.Block
            };

            _grid.CursorShape = shape;
        }

        public event Action<byte[]>? RawInput;
        public event EventHandler<string?>? Submitted;

        public static readonly RoutedEvent<RoutedEventArgs> NewTabRequestedEvent = RoutedEvent.Register<TerminalView, RoutedEventArgs>("NewTabRequested", RoutingStrategies.Bubble);
        
        public event EventHandler<RoutedEventArgs> NewTabRequested
        {
            add => AddHandler(NewTabRequestedEvent, value);
            remove => RemoveHandler(NewTabRequestedEvent, value);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateSize();
        }
        
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            // Only handle DataContext changes here
            // Size changes are handled by OnSizeChanged
            if (change.Property == DataContextProperty)
            {
                if (DataContext is Dotty.App.ViewModels.TerminalSession session)
                {
                    Session = session;
                }
            }
        }

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

            if (_session != null)
            {
                if (_session.Adapter?.Buffer != null && _grid != null)
                {
                    SetBuffer(_session.Adapter.Buffer);
                }
                
                // Reconnect event handlers (they were disconnected in OnDetached)
                _session.RenderScheduled += OnRenderScheduled;
                this.RawInput += _session.WriteInput;
                
                if (_fpsMeasurementCallback == null)
                {
                    _fpsMeasurementCallback = OnMeasureRefreshRate;
                    _lastFrameTime = TimeSpan.Zero;
                    TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_fpsMeasurementCallback);
                }
            }
            
            // Always add input handlers
            AddHandler(KeyDownEvent, TerminalView_KeyDown, RoutingStrategies.Tunnel);
            AddHandler(TextInputEvent, TerminalView_TextInput, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, TerminalView_PointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, TerminalView_PointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, TerminalView_PointerReleased, RoutingStrategies.Tunnel);
            
            // Request focus so we can receive input
            this.Focus();
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
                ClearSelection: ClearSelection,
                NewTab: () => RaiseEvent(new RoutedEventArgs(NewTabRequestedEvent))
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
                    _suppressText = false;
                    return;
                }

                if (e.Key == Key.V)
                {
                    _ = PasteFromClipboardAsync();
                    e.Handled = true;
                    _suppressText = false;
                    return;
                }
            }

            var encoded = _inputEncoder.Encode(e.Key, e.KeyModifiers, KeypadApplicationMode);
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
            if (_suppressText) 
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(e.Text);
            RawInput?.Invoke(bytes);
            _lineBuffer += e.Text;
        }

        public void SetBuffer(TerminalBuffer buffer)
        {
            _lastBuffer = buffer;
            _grid?.SetBuffer(buffer);
            ResetSelection();
        }
        
        /// <summary>
        /// Forces an immediate render of the current buffer.
        /// Call this when the view becomes visible to avoid white flash.
        /// </summary>
        public void ForceImmediateRender()
        {
            // If we don't have a buffer yet but have a session with a buffer, set it now
            if (_lastBuffer == null && _session?.Adapter?.Buffer != null)
            {
                SetBuffer(_session.Adapter.Buffer);
                return; // SetBuffer will trigger the render
            }
            
            if (_lastBuffer == null) return;
            
            // Force the grid to re-render
            _grid?.SetBuffer(_lastBuffer);
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
            
            // INCREASE TOP PADDING TO FIX SQUASHED UI
            

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

        public void SendRawInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_suppressText) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            RawInput?.Invoke(bytes);
            _lineBuffer += text;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            
            if (_session != null && _fpsMeasurementCallback != null)
            {
                _lastFrameTime = TimeSpan.Zero;
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_fpsMeasurementCallback);
            }
        }
    }
}
