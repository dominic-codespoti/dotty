using System;
using Dotty.Runtime.Sessions;

namespace Dotty.Runtime.Panes;

public sealed class LeafPane : PaneNode, IDisposable
{
    private bool _isDisposed;

    public string Id { get; }
    public TerminalSession Session { get; }
    public PaneRect Bounds { get; internal set; }
    public int Columns { get; internal set; }
    public int Rows { get; internal set; }

    public LeafPane(TerminalSession session, string? id = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Columns = session.Adapter.Buffer.Columns;
        Rows = session.Adapter.Buffer.Rows;
    }

    public LeafPane(int rows = 24, int columns = 80, string? id = null)
        : this(new TerminalSession(rows: rows, columns: columns), id)
    {
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Session.Dispose();
    }
}
