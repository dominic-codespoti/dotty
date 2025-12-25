using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Utilities;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls.Rendering;

internal sealed class BrushResolver
{
    private readonly Dictionary<string, IBrush> _colorBrushCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IBrush, IBrush> _faintBrushCache = new(ReferenceEqualityComparer.Instance);
    private IBrush _defaultFg = Brushes.White;
    private IBrush _defaultBg = Brushes.Black;

    public void UpdateDefaults(IBrush defaultFg, IBrush defaultBg)
    {
        _defaultFg = defaultFg;
        _defaultBg = defaultBg;
    }

    public void Resolve(in Cell cell, out IBrush foreground, out IBrush background)
    {
        foreground = ResolveColor(cell.Foreground, _defaultFg);
        background = ResolveColor(cell.Background, _defaultBg);

        if (cell.Inverse)
        {
            (foreground, background) = (background, foreground);
        }

        if (cell.Faint)
        {
            foreground = GetFaintBrush(foreground);
        }
    }

    public IBrush Foreground(in Cell cell)
    {
        Resolve(in cell, out var fg, out _);
        return fg;
    }

    public IBrush Background(in Cell cell)
    {
        Resolve(in cell, out _, out var bg);
        return bg;
    }

    public (IBrush fg, IBrush bg) Cursor(in Cell cell)
    {
        Resolve(in cell, out var fg, out var bg);
        return (fg, bg);
    }

    public IBrush Underline(in Cell cell)
    {
        var fg = Foreground(in cell);
        return ResolveColor(cell.UnderlineColor, fg);
    }

    public void ClearCaches()
    {
        _colorBrushCache.Clear();
        _faintBrushCache.Clear();
    }

    private IBrush ResolveColor(SgrColor? color, IBrush fallback)
    {
        var hex = color?.Hex;
        if (string.IsNullOrEmpty(hex))
        {
            return fallback;
        }

        if (_colorBrushCache.TryGetValue(hex, out var cached))
        {
            return cached;
        }

        if (Color.TryParse(hex, out var colorValue))
        {
            var brush = new SolidColorBrush(colorValue);
            _colorBrushCache[hex] = brush;
            return brush;
        }

        return fallback;
    }

    private IBrush GetFaintBrush(IBrush baseBrush)
    {
        if (_faintBrushCache.TryGetValue(baseBrush, out var faint))
        {
            return faint;
        }

        if (baseBrush is ISolidColorBrush solid)
        {
            var opacity = Math.Clamp(solid.Opacity * 0.6, 0.0, 1.0);
            faint = new SolidColorBrush(solid.Color, opacity);
        }
        else
        {
            faint = baseBrush;
        }

        _faintBrushCache[baseBrush] = faint;
        return faint;
    }
}
