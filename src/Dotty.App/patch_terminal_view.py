import sys

content = open("Views/TerminalView.axaml.cs", "r").read()

new_props = """
        private Dotty.App.ViewModels.TerminalSession? _session;
        public Dotty.App.ViewModels.TerminalSession? Session
        {
            get => _session;
            set
            {
                if (_session != null)
                {
                    _session.RenderScheduled -= OnRenderScheduled;
                    this.RawInput -= _session.WriteInput;
                }
                
                _session = value;
                
                if (_session != null)
                {
                    _session.RenderScheduled += OnRenderScheduled;
                    this.RawInput += _session.WriteInput;
                    
                    if (_session.Adapter?.Buffer is not null)
                        SetBuffer(_session.Adapter.Buffer);
                        
                    _session.Start();
                    
                    UpdateSize();
                }
            }
        }
        
        private void OnRenderScheduled()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
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
                int cols = (int)Math.Max(1, (bounds.Width - padding) / cellWidth);
                int rows = (int)Math.Max(1, (bounds.Height - padding) / cellHeight);
                _session.Resize(cols, rows);
            }
        }
"""

if "public Dotty.App.ViewModels.TerminalSession? Session" not in content:
    content = content.replace("public bool KeypadApplicationMode { get; set; }", "public bool KeypadApplicationMode { get; set; }\n" + new_props)
    
    # ensure we call UpdateSize on resize/layout
    size_hook = """
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateSize();
        }
        
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty)
            {
                UpdateSize();
            }
        }
"""
    if "protected override void OnSizeChanged" not in content:
        content = content.replace("public TerminalView()", size_hook + "\n        public TerminalView()")

with open("Views/TerminalView.axaml.cs", "w") as f:
    f.write(content)
