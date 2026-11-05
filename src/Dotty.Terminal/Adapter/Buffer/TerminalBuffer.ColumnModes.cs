using Dotty.Terminal.Graphics;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// Applies the screen-side effects of DECCOLM. The adapter performs this before requesting host geometry so a
    /// denied or asynchronous resize cannot defer the clear or erase subsequent output.
    /// </summary>
    internal void SetColumnMode(bool clearScreen)
    {
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _leftMargin = 0;
        _rightMargin = Columns - 1;

        if (clearScreen)
        {
            for (int row = 0; row < Rows; row++)
                ActiveBuffer.ClearRow(row);

            // DECCOLM clears visible cells only; unlike ED 2, leave the history ring and its scroll count intact.
            if (_screens.ActiveImages is { HasPlacements: true } images)
            {
                KittyGraphicsCommand.Raw raw = KittyGraphicsCommand.Raw.CreateDefault();
                raw.A = (byte)KittyAction.Delete;
                raw.D = (byte)'v';
                raw.Present |= 1UL << KittyGraphicsCommand.BitIndex('d');
                KittyGraphicsCommand command = new(in raw);
                if (images.ApplyDelete(in command, CursorRow, CursorCol, Rows) > 0)
                    BumpGraphicsGeneration();
            }
        }

        HomeCursor();
        _cursor.SetWrapPending(false);
        MarkAllRowsDirty();
    }
}
