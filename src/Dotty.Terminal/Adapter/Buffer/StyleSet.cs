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
        if (attrs.IsDefaultColors && !attrs.Bold && !attrs.Italic && !attrs.Underline
            && !attrs.DoubleUnderline && !attrs.Faint && !attrs.Inverse
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
}
