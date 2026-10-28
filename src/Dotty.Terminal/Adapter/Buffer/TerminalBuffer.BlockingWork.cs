using System.Threading;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// Releases <see cref="SyncRoot"/> for the duration of a long-running step (PNG inflate, premultiply, file reads)
    /// so the render thread can keep capturing snapshots, and re-acquires it afterwards.
    /// </summary>
    /// <remarks>
    /// Usage: <c>using var scope = buffer.EnterBlockingWork();</c>. Invariants the caller must uphold:
    /// <list type="bullet">
    /// <item>Only the PTY consumer thread calls this, and it holds <see cref="SyncRoot"/> with a recursion count of
    /// exactly 1 (the pipeline takes the lock once per parsed chunk). .NET exposes no recursion count, so exactly one
    /// level is released; a deeper recursion would leave the lock held and the render thread blocked.</item>
    /// <item>While the scope is open, parser and controller state must not be touched by any other thread, and
    /// everything read from the buffer before the scope must be re-validated afterwards (the buffer may have been
    /// resized, reset, or disposed — see <see cref="IsDisposed"/>).</item>
    /// <item>When <see cref="SyncRoot"/> is not held by the current thread (tests feeding the parser directly)
    /// the scope is a no-op.</item>
    /// </list>
    /// </remarks>
    internal BlockingWorkScope EnterBlockingWork() => new(SyncRoot);

    /// <summary>
    /// Non-<c>ref struct</c> form of <see cref="EnterBlockingWork"/> for callers that cannot hold a scope in a field
    /// (the graphics transfer is reached through an interface). Releases exactly one level of <see cref="SyncRoot"/>
    /// when the current thread holds it and returns true; the caller must then call <see cref="ReacquireSyncRoot"/>
    /// once, in a <c>finally</c>. Returns false, with the lock held exactly as before, when the lock is not held or
    /// when it is held recursively (releasing one level would not free it, and an outer critical section of the
    /// caller must not be broken). Same invariants as <see cref="EnterBlockingWork"/>.
    /// </summary>
    internal bool ReleaseSyncRootForBlockingWork()
    {
        if (!Monitor.IsEntered(SyncRoot))
            return false;
        Monitor.Exit(SyncRoot);
        if (!Monitor.IsEntered(SyncRoot))
            return true;
        Monitor.Enter(SyncRoot); // still held: it was a recursive hold; restore the level, nothing was released
        return false;
    }

    /// <summary>Re-acquires the lock released by <see cref="ReleaseSyncRootForBlockingWork"/>.</summary>
    internal void ReacquireSyncRoot() => Monitor.Enter(SyncRoot);

    /// <summary>True once <see cref="Dispose"/> has run; checked at commit time after a <see cref="BlockingWorkScope"/>.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Scope returned by <see cref="EnterBlockingWork"/>. <see cref="Dispose"/> re-acquires the monitor if it was released.
    /// </summary>
    internal ref struct BlockingWorkScope
    {
        private readonly object _syncRoot;
        private bool _released;

        internal BlockingWorkScope(object syncRoot)
        {
            _syncRoot = syncRoot;
            _released = false;
            if (Monitor.IsEntered(syncRoot))
            {
                Monitor.Exit(syncRoot);
                _released = true;
            }
        }

        /// <summary>True when the lock was held on entry and is currently released.</summary>
        public readonly bool LockReleased => _released;

        /// <summary>Re-enters the monitor if the scope released it; idempotent.</summary>
        public void Dispose()
        {
            if (!_released)
                return;
            _released = false;
            Monitor.Enter(_syncRoot);
        }
    }
}
