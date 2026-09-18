using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dotty.Terminal.Adapter.Buffer;

public class StyleSet
{
    private sealed class PublishedState
    {
        public PublishedState(Dictionary<CellAttributes, ushort> styleToId, CellAttributes[] idToStyle)
        {
            StyleToId = styleToId;
            IdToStyle = idToStyle;
        }

        public Dictionary<CellAttributes, ushort> StyleToId { get; }

        public CellAttributes[] IdToStyle { get; }
    }

    private readonly object _syncRoot = new();
    private PublishedState _state = new(
        new Dictionary<CellAttributes, ushort> { [CellAttributes.Default] = 0 },
        new[] { CellAttributes.Default });

    public ushort GetOrCreateId(in CellAttributes attrs)
    {
        var state = Volatile.Read(ref _state);
        if (state.StyleToId.TryGetValue(attrs, out var id))
            return id;

        lock (_syncRoot)
        {
            state = Volatile.Read(ref _state);
            if (state.StyleToId.TryGetValue(attrs, out id))
                return id;

            id = (ushort)state.IdToStyle.Length;
            var updatedStyles = new CellAttributes[state.IdToStyle.Length + 1];
            Array.Copy(state.IdToStyle, updatedStyles, state.IdToStyle.Length);
            updatedStyles[id] = attrs;

            var updatedStyleToId = new Dictionary<CellAttributes, ushort>(state.StyleToId)
            {
                [attrs] = id
            };
            Volatile.Write(ref _state, new PublishedState(updatedStyleToId, updatedStyles));
            return id;
        }
    }

    public ref readonly CellAttributes GetStyle(ushort id)
    {
        var state = Volatile.Read(ref _state);
        var styles = state.IdToStyle;
        if (id >= styles.Length)
            return ref CellAttributes.Default;
        ref var arr = ref MemoryMarshal.GetArrayDataReference(styles);
        return ref Unsafe.Add(ref arr, (nint)id);
    }

    /// <summary>
    /// Returns a defensive copy of the currently published immutable style table.
    /// </summary>
    public CellAttributes[] CaptureStyles()
    {
        var state = Volatile.Read(ref _state);
        return (CellAttributes[])state.IdToStyle.Clone();
    }

    /// <summary>
    /// Returns the published immutable style table for an internal render snapshot.
    /// The returned array must not be exposed for mutation.
    /// </summary>
    internal CellAttributes[] CaptureStylesShared()
    {
        var state = Volatile.Read(ref _state);
        return state.IdToStyle;
    }

    public bool RemapAnsiPalette(uint[] previousPalette, uint[] currentPalette)
    {
        if (previousPalette == null || currentPalette == null || previousPalette.Length != 16 || currentPalette.Length != 16)
        {
            throw new ArgumentException("ANSI palettes must contain exactly 16 colors.");
        }

        bool changed = false;
        lock (_syncRoot)
        {
            var state = Volatile.Read(ref _state);
            var current = state.IdToStyle;
            CellAttributes[]? remappedStyles = null;
            for (int i = 1; i < current.Length; i++)
            {
                var style = current[i];
                var remapped = RemapAnsiPalette(style, previousPalette, currentPalette);
                if (remapped.Equals(style))
                    continue;

                remappedStyles ??= new CellAttributes[current.Length];
                if (!changed)
                    Array.Copy(current, remappedStyles, current.Length);
                remappedStyles[i] = remapped;
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            var finalStyles = remappedStyles!;
            var finalStyleToId = new Dictionary<CellAttributes, ushort>(finalStyles.Length);
            for (ushort id = 0; id < finalStyles.Length; id++)
            {
                finalStyleToId[finalStyles[id]] = id;
            }
            Volatile.Write(ref _state, new PublishedState(finalStyleToId, finalStyles));
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
