using System;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// What <see cref="KittyGraphicsController"/> needs from its environment. <c>TerminalAdapter</c> implements it
/// explicitly; tests use a fake. Every member is called on the PTY consumer thread (or, for configuration, with the
/// buffer lock held) and may be called while the buffer lock is held.
/// </summary>
internal interface IKittyGraphicsHost
{
    /// <summary>Cursor row, 0-based, relative to the visible screen.</summary>
    int CursorRow { get; }

    /// <summary>Cursor column, 0-based.</summary>
    int CursorCol { get; }

    int Rows { get; }
    int Columns { get; }

    /// <summary>Width of one cell in pixels (at least 1).</summary>
    int CellPixelWidth { get; }

    /// <summary>Height of one cell in pixels (at least 1).</summary>
    int CellPixelHeight { get; }

    /// <summary>Queues a reply for the application (synchronously, in parse order).</summary>
    void Reply(ReadOnlySpan<char> reply);

    /// <summary>
    /// Releases the buffer lock around a long pure-CPU step (inflate, PNG decode, premultiply) so the renderer can
    /// keep capturing frames. Returns true when the lock was held and has been released; the caller must then call
    /// <see cref="ReacquireSyncRoot"/> exactly once (in a <c>finally</c>). Returns false, releasing nothing, when
    /// the current thread does not hold the lock (tests feeding the parser directly). Only the PTY consumer thread
    /// may call it, holding the lock with a recursion count of one.
    /// </summary>
    bool ReleaseSyncRootForBlockingWork();

    /// <summary>Re-acquires the lock released by <see cref="ReleaseSyncRootForBlockingWork"/>.</summary>
    void ReacquireSyncRoot();

    /// <summary>Visible graphics state changed; the host should schedule a render.</summary>
    void GraphicsChanged();

    /// <summary>Monotonic clock in <see cref="System.Diagnostics.Stopwatch"/>-style ticks (used for LRU access times).</summary>
    long NowTicks { get; }

    /// <summary>True once the owning terminal was disposed; checked after unlocked work before anything is committed.</summary>
    bool IsDisposed { get; }
}
