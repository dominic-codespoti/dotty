using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen
{
    internal void SetRectangleCellStyle(int row, int column, ushort styleId)
    {
        int physicalRow = GetPhysicalRow(row);
        ref CellHot cell = ref UnsafeAsRef<CellHot>(_cellsPtr, physicalRow * Columns + column);
        cell.StyleId = styleId;
    }
}
