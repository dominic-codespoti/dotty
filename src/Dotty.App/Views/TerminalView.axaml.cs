using System;
using System.Collections.Generic;
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
using Dotty.App.Services;
using Avalonia.Threading;

namespace Dotty.App.Views
{
    public partial class TerminalView : UserControl
    {
        private TerminalGrid? _grid;
        private TerminalCanvas? _canvas;
        private SearchOverlay? _searchOverlay;
        private string _lineBuffer = string.Empty;
        private bool _suppressText = false;
        private readonly SelectionController _selectionController = new();
        private readonly SelectionContextMenuBuilder _contextMenuBuilder;
        private readonly TerminalInputEncoder _inputEncoder = new();
        private TerminalBuffer? _lastBuffer;
        private int? _mouseReportingButton;
        private bool _sessionHandlersAttached;
        private bool _rawInputAttached;

        public string? WorkingDirectory { get; set; }
        public bool KeypadApplicationMode { get; set; }

        private Dotty.App.ViewModels.TerminalSession? _session;
        private Action<TimeSpan>? _fpsMeasurementCallback;
        private TimeSpan _lastFrameTime;
        private bool _renderUpdatePending;
        private int _lastCols = -1;
        private int _lastRows = -1;
        private bool _layoutSizeSyncPending;
        private const int DefaultStartupCols = 80;
        private const int DefaultStartupRows = 24;

        
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            DetachSessionHandlers();
            DetachRawInput();
            
            // Remove input handlers to prevent accumulation
            RemoveHandler(KeyDownEvent, TerminalView_KeyDown);
            RemoveHandler(TextInputEvent, TerminalView_TextInput);
            RemoveHandler(PointerPressedEvent, TerminalView_PointerPressed);
            RemoveHandler(PointerMovedEvent, TerminalView_PointerMoved);
            RemoveHandler(PointerReleasedEvent, TerminalView_PointerReleased);
            RemoveHandler(PointerWheelChangedEvent, TerminalView_PointerWheelChanged);
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
                    DetachSessionHandlers();
                    DetachRawInput();
                }
                
                _session = value;
                _lastCols = -1;
                _lastRows = -1;
                
                if (_session != null)
                {
                    if (VisualRoot != null)
                    {
                        AttachSessionHandlers();
                        AttachRawInput();
                    }

                    TryStartSessionWithCurrentSize();
                }
            }
        }

        private void AttachSessionHandlers()
        {
            if (_session == null || _sessionHandlersAttached)
            {
                return;
            }

            _session.RenderScheduled += OnRenderScheduled;
            _session.ClipboardWriteRequested += OnClipboardWriteRequested;
            _sessionHandlersAttached = true;
        }

        private void DetachSessionHandlers()
        {
            if (_session == null || !_sessionHandlersAttached)
            {
                return;
            }

            _session.RenderScheduled -= OnRenderScheduled;
            _session.ClipboardWriteRequested -= OnClipboardWriteRequested;
            _sessionHandlersAttached = false;
        }

        private void AttachRawInput()
        {
            if (_session == null || _rawInputAttached)
            {
                return;
            }

            this.RawInput += _session.WriteInput;
            _rawInputAttached = true;
        }

        private void DetachRawInput()
        {
            if (_session == null || !_rawInputAttached)
            {
                return;
            }

            this.RawInput -= _session.WriteInput;
            _rawInputAttached = false;
        }

        private void TryStartSessionWithCurrentSize()
        {
            if (_session == null || _session.IsStarted)
            {
                return;
            }

            if (_lastCols <= 0 || _lastRows <= 0)
            {
                if (TryGetSeededStartupBufferSize(_session, out var seededCols, out var seededRows))
                {
                    _lastCols = seededCols;
                    _lastRows = seededRows;
                }
                else
                {
                    UpdateSize();
                }
            }
            else if (IsDefaultStartupSize(_lastCols, _lastRows)
                && TryGetSeededStartupBufferSize(_session, out var seededCols, out var seededRows))
            {
                _lastCols = seededCols;
                _lastRows = seededRows;
            }

            if (_lastCols > 0 && _lastRows > 0)
            {
                _session.Start();
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

            if (ShouldDeferPreStartSizeUpdate(_session, _lastCols, _lastRows))
            {
                return;
            }

            var viewport = GetResizeViewport();
            if (viewport.Width <= 0 || viewport.Height <= 0) return;
            
            if (TryGetTerminalMetrics(out var cellWidth, out var cellHeight, out var padding))
            {
                var (cols, rows) = CalculateTerminalSize(viewport, padding, cellWidth, cellHeight);
                
                // Only resize if size actually changed - prevents shell prompt redraw
                if (cols != _lastCols || rows != _lastRows)
                {
                    _lastCols = cols;
                    _lastRows = rows;
                    _session.Resize(cols, rows);
                }
            }
        }

        internal static bool ShouldDeferPreStartSizeUpdate(Dotty.App.ViewModels.TerminalSession session, int lastCols, int lastRows)
        {
            if (session == null || session.IsStarted)
            {
                return false;
            }

            return (lastCols <= 0 || lastRows <= 0 || IsDefaultStartupSize(lastCols, lastRows))
                && TryGetSeededStartupBufferSize(session, out _, out _);
        }

        internal static bool TryGetSeededStartupBufferSize(Dotty.App.ViewModels.TerminalSession session, out int cols, out int rows)
        {
            cols = 0;
            rows = 0;

            var buffer = session?.Adapter?.Buffer;
            if (buffer == null || buffer.Columns <= 0 || buffer.Rows <= 0)
            {
                return false;
            }

            if (buffer.Columns == DefaultStartupCols && buffer.Rows == DefaultStartupRows)
            {
                return false;
            }

            cols = buffer.Columns;
            rows = buffer.Rows;
            return true;
        }

        internal static bool IsDefaultStartupSize(int cols, int rows)
        {
            return cols == DefaultStartupCols && rows == DefaultStartupRows;
        }

        private void ScheduleLayoutSizeSync()
        {
            if (_layoutSizeSyncPending)
            {
                return;
            }

            _layoutSizeSyncPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _layoutSizeSyncPending = false;
                if (_session == null)
                {
                    return;
                }

                UpdateSize();
                TryStartSessionWithCurrentSize();
            }, DispatcherPriority.Loaded);
        }

        internal Size GetResizeViewport()
        {
            EnsureCanvas();

            var canvasViewport = _canvas?.Viewport ?? default;
            var canvasBounds = _canvas?.Bounds.Size ?? default;
            var gridBounds = _grid?.Bounds.Size ?? default;
            return SelectResizeViewport(Bounds.Size, gridBounds, canvasBounds, canvasViewport);
        }

        internal static Size SelectResizeViewport(Size viewBounds, Size gridBounds, Size canvasBounds, Size canvasViewport)
        {
            if (canvasViewport.Width > 0 && canvasViewport.Height > 0)
            {
                return canvasViewport;
            }

            if (canvasBounds.Width > 0 && canvasBounds.Height > 0)
            {
                return canvasBounds;
            }

            if (gridBounds.Width > 0 && gridBounds.Height > 0)
            {
                return gridBounds;
            }

            return viewBounds;
        }

        internal static (int Cols, int Rows) CalculateTerminalSize(Size viewport, Thickness padding, double cellWidth, double cellHeight)
        {
            var availableWidth = Math.Max(0, viewport.Width - padding.Left - padding.Right);
            var availableHeight = Math.Max(0, viewport.Height - padding.Top - padding.Bottom);
            var cols = (int)Math.Max(1, availableWidth / Math.Max(1.0, cellWidth));
            var rows = (int)Math.Max(1, availableHeight / Math.Max(1.0, cellHeight));
            return (cols, rows);
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
            TryStartSessionWithCurrentSize();
            ScheduleLayoutSizeSync();
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
            _searchOverlay = this.FindControl<SearchOverlay>("PART_SearchOverlay");

            if (_session != null)
            {
                if (_session.Adapter?.Buffer != null && _grid != null)
                {
                    SetBuffer(_session.Adapter.Buffer);
                }
                
                AttachSessionHandlers();
                AttachRawInput();
                
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
            AddHandler(PointerWheelChangedEvent, TerminalView_PointerWheelChanged, RoutingStrategies.Tunnel);

            TryStartSessionWithCurrentSize();
            ScheduleLayoutSizeSync();
            
            // Request focus so we can receive input
            this.Focus();
        }

        private void TerminalView_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            EnsureCanvas();
            if (_canvas == null) return;
            var current = e.GetCurrentPoint(_canvas);

            if (TryHandleMouseReportingPress(e, current))
            {
                return;
            }
            
            // Handle right-click for context menu
            if (current.Properties.IsRightButtonPressed)
            {
                ShowContextMenu(e);
                return;
            }
            
            if (!current.Properties.IsLeftButtonPressed) return;
            if (!TryGetCellFromPointer(e, out int row, out int column)) return;

            if (TryOpenHyperlink(row, column, e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }
            
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
            if (TryHandleMouseReportingMove(e))
            {
                return;
            }

            if (!_selectionController.IsDragging) return;
            if (!TryGetCellFromPointer(e, out int row, out int column)) return;
            _selectionController.UpdateSelection(row, column);
            UpdateCanvasSelection();
        }

        private void TerminalView_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (TryHandleMouseReportingRelease(e))
            {
                return;
            }

            if (!_selectionController.IsDragging) return;
            _selectionController.EndSelection();
            UpdateCanvasSelection();
        }

        private void TerminalView_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!TrySendMouseReport(e, e.Delta.Y > 0 ? 64 : 65, isPress: true, isMove: false)) return;
            e.Handled = true;
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
            // Skip key handling when search overlay is visible (let search handle its own keys)
            if (_searchOverlay != null && _searchOverlay.IsVisible)
            {
                // Only handle Escape to close search, let everything else pass through
                if (e.Key == Key.Escape)
                {
                    HideSearch();
                    e.Handled = true;
                }
                // Don't process other keys - let them reach the search overlay
                return;
            }

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

                if (e.Key == Key.F)
                {
                    ToggleSearch();
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
            
            // Check if search overlay is visible - if so, don't send text to terminal
            if (_searchOverlay != null && _searchOverlay.IsVisible)
            {
                // Search overlay is active, don't process text in terminal
                return;
            }
            
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
                    SendPasteInput(text);
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

        private void SendPasteInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_suppressText) return;

            if (_session?.Adapter?.Buffer?.BracketedPasteMode == true)
            {
                var wrapped = $"\u001b[200~{text}\u001b[201~";
                RawInput?.Invoke(Encoding.UTF8.GetBytes(wrapped));
                _lineBuffer += text;
                return;
            }

            SendRawInput(text);
        }

        private async void OnClipboardWriteRequested(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = GetClipboard();
                if (clipboard == null) return;
                try
                {
                    await clipboard.SetTextAsync(text);
                }
                catch { }
            });
        }

        private bool TryOpenHyperlink(int row, int column, KeyModifiers modifiers)
        {
            var buffer = _canvas?.Buffer;
            if (buffer == null || row < 0 || row >= buffer.Rows || column < 0 || column >= buffer.Columns)
            {
                return false;
            }

            var cell = buffer.GetCell(row, column);
            if (cell.HyperlinkId == 0)
            {
                return false;
            }

            var url = buffer.GetHyperlinkUrl(cell.HyperlinkId);
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            _ = HyperlinkService.OpenUrlAsync(url, modifiers.HasFlag(KeyModifiers.Control));
            return modifiers.HasFlag(KeyModifiers.Control);
        }

        private bool TryHandleMouseReportingPress(PointerPressedEventArgs e, PointerPoint current)
        {
            if (!TryGetMouseReportButton(current.Properties, out var button))
            {
                return false;
            }

            if (!TrySendMouseReport(e, button, isPress: true, isMove: false))
            {
                return false;
            }

            _mouseReportingButton = button;
            _selectionController.Clear();
            UpdateCanvasSelection();
            e.Handled = true;
            return true;
        }

        private bool TryHandleMouseReportingMove(PointerEventArgs e)
        {
            int button = _mouseReportingButton ?? 3;
            if (!TrySendMouseReport(e, button, isPress: true, isMove: true))
            {
                return false;
            }

            e.Handled = true;
            return true;
        }

        private bool TryHandleMouseReportingRelease(PointerReleasedEventArgs e)
        {
            int button = _mouseReportingButton ?? 3;
            if (!TrySendMouseReport(e, button, isPress: false, isMove: false))
            {
                _mouseReportingButton = null;
                return false;
            }

            _mouseReportingButton = null;
            e.Handled = true;
            return true;
        }

        private bool TrySendMouseReport(PointerEventArgs e, int button, bool isPress, bool isMove)
        {
            var adapter = _session?.Adapter;
            if (adapter == null || !adapter.MouseReportingEnabled) return false;
            if (!TryGetCellFromPointer(e, out int row, out int column)) return false;
            if (row < 0) return false;

            var encoded = _inputEncoder.EncodeMouseEvent(
                adapter.CurrentMouseMode,
                adapter.CurrentMouseEncoding,
                button,
                row,
                column,
                isPress,
                isMove,
                e.KeyModifiers);

            if (encoded == null) return false;
            RawInput?.Invoke(encoded);
            return true;
        }

        private static bool TryGetMouseReportButton(PointerPointProperties properties, out int button)
        {
            if (properties.IsLeftButtonPressed)
            {
                button = 0;
                return true;
            }

            if (properties.IsMiddleButtonPressed)
            {
                button = 1;
                return true;
            }

            if (properties.IsRightButtonPressed)
            {
                button = 2;
                return true;
            }

            button = 3;
            return false;
        }

        public string GetScrollbackStats()
        {
            try
            {
                EnsureCanvas();
                if (_canvas?.Buffer == null) return "{}";
                
                var buffer = _canvas.Buffer;
                int sbCount = buffer.ScrollbackCount;
                
                // Get first few lines of scrollback for verification
                var sampleLines = new List<string>();
                int samplesToTake = Math.Min(3, sbCount);
                for (int i = 0; i < samplesToTake; i++)
                {
                    var line = buffer.GetScrollbackLine(i);
                    var content = line.ToString();
                    sampleLines.Add(string.IsNullOrEmpty(content) ? "(empty)" : content.Substring(0, Math.Min(20, content.Length)));
                }
                
                // Count non-empty lines
                int nonEmptyCount = 0;
                for (int i = 0; i < sbCount; i++)
                {
                    var line = buffer.GetScrollbackLine(i);
                    if (line.Length > 0) nonEmptyCount++;
                }
                
                return "{" +
                    $"\"scrollbackCount\":{sbCount}," +
                    $"\"nonEmptyCount\":{nonEmptyCount}," +
                    $"\"sampleLines\":[\"{string.Join("\",\"", sampleLines)}\"]" +
                    "}";
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + ex.Message + "\"}";
            }
        }

        public void ToggleSearch()
        {
            if (_searchOverlay == null) return;
            
            if (_searchOverlay.IsVisible)
            {
                _searchOverlay.HideSearch();
                FocusInput();
            }
            else
            {
                ShowSearch();  // FIXED: This initializes search with buffer
            }
        }

        public void ShowSearch()
        {
            if (_searchOverlay == null) return;
            
            // Initialize search with the actual displayed buffer (_lastBuffer)
            // NOT _canvas.Buffer which might be a different instance or null
            var buffer = _lastBuffer ?? _session?.Adapter?.Buffer;
            if (buffer != null)
            {
                var search = new TerminalSearch(buffer);
                _searchOverlay.InitializeSearch(search);
            }
            
            // Subscribe to match navigation events to update search highlights
            _searchOverlay.MatchNavigated += OnSearchMatchNavigated;
            
            _searchOverlay.ShowSearch();
        }

        private void OnSearchMatchNavigated(object? sender, SearchMatch e)
        {
            // Update the canvas with all search matches for highlighting
            if (_grid != null && _searchOverlay != null)
            {
                _grid.SearchMatches = _searchOverlay.Matches;
            }
        }

        public void HideSearch()
        {
            if (_searchOverlay == null) return;
            
            // Unsubscribe from match navigation events
            _searchOverlay.MatchNavigated -= OnSearchMatchNavigated;
            
            _searchOverlay.HideSearch();
            
            // Clear search highlights from the canvas
            if (_grid != null)
            {
                _grid.SearchMatches = Array.Empty<SearchMatch>();
            }
            
            FocusInput();
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
