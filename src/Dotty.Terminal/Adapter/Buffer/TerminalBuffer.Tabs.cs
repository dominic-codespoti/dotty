using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    internal void TabForward(int count, bool moreFix)
    {
        if (moreFix && _autoWrap && _cursor.WrapPending && WrapToNextLine())
            ActiveBuffer.SetRowContinuation(_cursor.Row, true);

        int start = _cursor.Col;
        int target = start;
        int right = GetWriteRightMargin(start);
        for (int i = 0, stops = Math.Max(1, count); i < stops && target < right; i++)
        {
            int next = GetNextTabStopFrom(target);
            if (next <= target)
                break;
            target = Math.Min(next, right);
        }

        // A tab that cannot advance must not cancel a pending printable wrap.
        if (target != start)
            _cursor.Set(_cursor.Row, target, Rows, Columns);
    }
}
