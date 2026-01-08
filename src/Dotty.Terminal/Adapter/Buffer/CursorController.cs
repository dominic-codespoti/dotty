namespace Dotty.Terminal.Adapter;

/// <summary>
/// Manages cursor position and visibility with bounds-aware movement helpers.
/// </summary>
internal sealed class CursorController
{
    public int Row { get; private set; }
    public int Col { get; private set; }
    public bool Visible { get; private set; } = true;

    public void Reset()
    {
        Row = 0;
        Col = 0;
    }

    public void SetVisible(bool visible) => Visible = visible;

    public void SetSize(int rows, int cols)
    {
        Row = Clamp(Row, rows);
        Col = Clamp(Col, cols);
    }

    public void Set(int row, int col, int rows, int cols)
    {
        Row = Clamp(row, rows);
        Col = Clamp(col, cols);
    }

    public void MoveBy(int dRow, int dCol, int rows, int cols)
    {
        Set(Row + dRow, Col + dCol, rows, cols);
    }

    public void CarriageReturn()
    {
        Col = 0;
    }

    public bool LineFeed(int rows)
    {
        Row++;
        if (Row >= rows)
        {
            Row = rows - 1;
            return true; // caller should scroll
        }
        return false;
    }

    public bool EnsureSpace(int width, int rows, int cols)
    {
        if (width <= 0) width = 1;
        if (Col > cols - width)
        {
            Col = 0;
            Row++;
            if (Row >= rows)
            {
                Row = rows - 1;
                return true;
            }
        }
        return false;
    }

    public bool AdvanceCursor(int width, int rows, int cols)
    {
        if (width <= 0) width = 1;
        Col += width;
        bool scrolled = false;
        while (Col >= cols)
        {
            Col -= cols;
            Row++;
            if (Row >= rows)
            {
                Row = rows - 1;
                scrolled = true;
            }
        }
        return scrolled;
    }

    public void MoveBackward(int rows, int cols)
    {
        if (Col > 0)
        {
            Col--;
        }
        else if (Row > 0)
        {
            Row--;
            Col = cols - 1;
        }
        else
        {
            Col = 0;
            Row = 0;
        }
    }

    private static int Clamp(int value, int upperExclusive)
    {
        if (upperExclusive <= 0) return 0;
        if (value < 0) return 0;
        if (value >= upperExclusive) return upperExclusive - 1;
        return value;
    }
}
