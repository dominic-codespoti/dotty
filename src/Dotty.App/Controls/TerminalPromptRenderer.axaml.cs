using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dotty.App.Controls
{
    // Keep a legacy usercontrol around under a different name so XAML-based templates don't break immediately.
    public partial class TerminalPromptRenderer_Old : UserControl
    {
        private Panel? _panel;

        public TerminalPromptRenderer_Old()
        {
            this.InitializeComponent();
            this.AttachedToVisualTree += (_, __) => _panel = this.FindControl<Panel>("PART_PromptPanel");
        }

        private List<Dotty.App.PromptSegment>? _segments;
        public List<Dotty.App.PromptSegment>? Segments
        {
            get => _segments;
            set
            {
                _segments = value;
                UpdateVisuals();
            }
        }

        private void UpdateVisuals()
        {
            if (_panel == null) return;
            _panel.Children.Clear();
            if (_segments == null) return;

            foreach (var seg in _segments)
            {
                var text = seg.Text ?? string.Empty;
                // Create a TextBlock for the segment
                var tb = new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

                if (!string.IsNullOrEmpty(seg.Foreground))
                {
                    try { tb.Foreground = new SolidColorBrush(Color.Parse(seg.Foreground)); } catch { }
                }

                if (!string.IsNullOrEmpty(seg.Background))
                {
                    try
                    {
                        var bg = new SolidColorBrush(Color.Parse(seg.Background));
                        var border = new Border { Background = bg, Padding = new Avalonia.Thickness(1,0), Child = tb };
                        _panel.Children.Add(border);
                        continue;
                    }
                    catch { }
                }

                if (seg.Bold)
                    tb.FontWeight = FontWeight.Bold;
                if (seg.Italic)
                    tb.FontStyle = FontStyle.Italic;
                if (seg.Underline)
                    tb.TextDecorations = Avalonia.Media.TextDecorations.Underline;

                _panel.Children.Add(tb);
            }
        }
    }
}
