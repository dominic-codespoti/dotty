using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Media.Imaging;

namespace Dotty.App.Controls
{
    public class TerminalPromptRenderer : Control
    {
        public static readonly StyledProperty<List<Dotty.App.PromptSegment>?> SegmentsProperty =
            AvaloniaProperty.Register<TerminalPromptRenderer, List<Dotty.App.PromptSegment>?>(nameof(Segments));
        private List<Dotty.App.PromptSegment>? _segmentsBacking;

        public List<Dotty.App.PromptSegment>? Segments
        {
            get => _segmentsBacking ?? GetValue(SegmentsProperty);
            set
            {
                _segmentsBacking = value;
                SetValue(SegmentsProperty, value);
                InvalidateVisual();
                InvalidateMeasure();
            }
        }

        // We rely on the Segments property setter to invalidate visuals; no OnPropertyChanged override needed.

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var segs = _segmentsBacking ?? GetValue(SegmentsProperty);
            if (segs == null || segs.Count == 0) return;

            var x = 0.0;
            var height = Bounds.Height;

            foreach (var seg in segs)
            {
                var text = seg.Text ?? string.Empty;

                var weight = seg.Bold ? FontWeight.Bold : FontWeight.Normal;
                var style = seg.Italic ? FontStyle.Italic : FontStyle.Normal;

                var fontFamily = (FontFamily?)GetValue(TextBlock.FontFamilyProperty) ?? FontFamily.Default;
                var fontSize = 12.0;
                var rawFs = GetValue(TextBlock.FontSizeProperty);
                if (rawFs is double d) fontSize = d;

                var tf = new Typeface(fontFamily, style, weight);

                // Render the segment to a RenderTargetBitmap for pixel-perfect control.
                var tb = new TextBlock { Text = text, FontFamily = fontFamily, FontSize = fontSize };
                if (seg.Bold) tb.FontWeight = FontWeight.Bold;
                if (seg.Italic) tb.FontStyle = FontStyle.Italic;
                if (!string.IsNullOrEmpty(seg.Foreground))
                {
                    try { tb.Foreground = new SolidColorBrush(Color.Parse(seg.Foreground)); } catch { }
                }

                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var desired = tb.DesiredSize;
                var px = Math.Max(1, (int)Math.Ceiling(desired.Width));
                var py = Math.Max(1, (int)Math.Ceiling(desired.Height));
                Size textSize = new Size(px, py);

                // Draw background if present
                if (!string.IsNullOrEmpty(seg.Background))
                {
                    try
                    {
                        var bg = Color.Parse(seg.Background);
                        context.FillRectangle(new SolidColorBrush(bg), new Rect(x, 0, textSize.Width, Math.Max(textSize.Height, height)));
                    }
                    catch { }
                }

                // Foreground brush
                IBrush brush = Brushes.Black;
                if (!string.IsNullOrEmpty(seg.Foreground))
                {
                    try { brush = new SolidColorBrush(Color.Parse(seg.Foreground)); } catch { }
                }

                // Render TextBlock to a bitmap and draw it
                try
                {
                    tb.Arrange(new Rect(0, 0, desired.Width, desired.Height));
                    var rtb = new RenderTargetBitmap(new PixelSize(px, py), new Vector(96, 96));
                    rtb.Render(tb);
                    var src = new Rect(0, 0, px, py);
                    var dest = new Rect(x, (height - textSize.Height) / 2, textSize.Width, textSize.Height);
                    var imgBrush = new ImageBrush(rtb) { Stretch = Stretch.None };
                    context.FillRectangle(imgBrush, dest);
                }
                catch
                {
                    // If bitmap rendering fails, fall back to drawing nothing for this segment.
                }

                // Underline (simple) - draw a 1px rectangle under text
                if (seg.Underline)
                {
                    var underlineY = (height + textSize.Height) / 2 + 1;
                    context.FillRectangle(brush, new Rect(x, underlineY, textSize.Width, 1));
                }

                x += textSize.Width;
            }
        }
    }
}
