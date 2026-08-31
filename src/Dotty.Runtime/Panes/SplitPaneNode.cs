using System;

namespace Dotty.Runtime.Panes;

public sealed class SplitPaneNode : PaneNode
{
    private PaneNode _first = null!;
    private PaneNode _second = null!;
    private float _splitRatio = 0.5f;

    public SplitDirection Direction { get; set; }

    public float SplitRatio
    {
        get => _splitRatio;
        set => _splitRatio = Math.Clamp(value, 0.01f, 0.99f);
    }

    public PaneNode First
    {
        get => _first;
        set
        {
            _first = value ?? throw new ArgumentNullException(nameof(value));
            _first.Parent = this;
        }
    }

    public PaneNode Second
    {
        get => _second;
        set
        {
            _second = value ?? throw new ArgumentNullException(nameof(value));
            _second.Parent = this;
        }
    }

    public PaneRect DividerBounds { get; internal set; }

    public SplitPaneNode(SplitDirection direction, PaneNode first, PaneNode second, float splitRatio = 0.5f)
    {
        Direction = direction;
        SplitRatio = splitRatio;
        First = first;
        Second = second;
    }

    public void ReplaceChild(PaneNode oldChild, PaneNode newChild)
    {
        if (ReferenceEquals(_first, oldChild))
        {
            First = newChild;
        }
        else if (ReferenceEquals(_second, oldChild))
        {
            Second = newChild;
        }
        else
        {
            throw new InvalidOperationException("Specified node is not a direct child of this split node.");
        }
    }
}
