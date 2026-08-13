using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter.Buffer;

public class StyleSet
{
    private readonly ConcurrentDictionary<CellAttributes, ushort> _styleToId = new();
    private CellAttributes[] _idToStyle = new[] { CellAttributes.Default };

    public ushort GetOrCreateId(in CellAttributes attrs)
    {
        if (attrs.IsDefaultColors && !attrs.Bold && !attrs.Italic && attrs.UnderlineStyle == UnderlineStyle.None
            && !attrs.Faint && !attrs.Inverse
            && !attrs.Strikethrough && !attrs.Overline && !attrs.Invisible
            && !attrs.SlowBlink && attrs.HyperlinkId == 0)
            return 0;

        if (_styleToId.TryGetValue(attrs, out var id))
            return id;

        lock (_idToStyle)
        {
            if (_styleToId.TryGetValue(attrs, out id))
                return id;
            id = (ushort)_idToStyle.Length;
            Array.Resize(ref _idToStyle, id + 1);
            _idToStyle[id] = attrs;
            _styleToId.TryAdd(attrs, id);
            return id;
        }
    }

    public ref readonly CellAttributes GetStyle(ushort id)
    {
        if (id >= _idToStyle.Length)
            return ref CellAttributes.Default;
        ref var arr = ref MemoryMarshal.GetArrayDataReference(_idToStyle);
        return ref Unsafe.Add(ref arr, (nint)id);
    }

    /// <summary>
    /// Copies the style table under its lock. The renderer's snapshot path
    /// reads from the copy so rasterization never observes entries being
    /// remapped in place by <see cref="RemapAnsiPalette"/>.
    /// </summary>
    public CellAttributes[] CaptureStyles()
    {
        lock (_idToStyle)
        {
            var copy = new CellAttributes[_idToStyle.Length];
            Array.Copy(_idToStyle, copy, _idToStyle.Length);
            return copy;
        }
    }

    public bool RemapAnsiPalette(uint[] previousPalette, uint[] currentPalette)
    {
        if (previousPalette == null || currentPalette == null || previousPalette.Length != 16 || currentPalette.Length != 16)
        {
            throw new ArgumentException("ANSI palettes must contain exactly 16 colors.");
        }

        bool changed = false;

        lock (_idToStyle)
        {
            for (int i = 1; i < _idToStyle.Length; i++)
            {
                var style = _idToStyle[i];
                var remapped = RemapAnsiPalette(style, previousPalette, currentPalette);
                if (!remapped.Equals(style))
                {
                    _idToStyle[i] = remapped;
                    changed = true;
                }
            }

            if (!changed)
            {
                return false;
            }

            _styleToId.Clear();
            for (ushort id = 1; id < _idToStyle.Length; id++)
            {
                _styleToId[_idToStyle[id]] = id;
            }
        }

        return true;
    }

    private static CellAttributes RemapAnsiPalette(in CellAttributes style, uint[] previousPalette, uint[] currentPalette)
    {
        var remapped = style;
        remapped.Foreground = RemapAnsiColor(style.Foreground, previousPalette, currentPalette);
        remapped.Background = RemapAnsiColor(style.Background, previousPalette, currentPalette);
        remapped.UnderlineColor = RemapAnsiColor(style.UnderlineColor, previousPalette, currentPalette);
        return remapped;
    }

    private static SgrColorArgb RemapAnsiColor(SgrColorArgb color, uint[] previousPalette, uint[] currentPalette)
    {
        if (color.IsEmpty)
        {
            return color;
        }

        for (int i = 0; i < 16; i++)
        {
            if (color.Argb == previousPalette[i])
            {
                return new SgrColorArgb(currentPalette[i]);
            }
        }

        return color;
    }
}
