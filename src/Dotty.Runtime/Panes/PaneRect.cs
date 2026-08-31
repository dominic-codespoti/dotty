namespace Dotty.Runtime.Panes;

public readonly record struct PaneRect(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(float x, float y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;

    public bool ContainsInclusive(float x, float y) =>
        x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}
