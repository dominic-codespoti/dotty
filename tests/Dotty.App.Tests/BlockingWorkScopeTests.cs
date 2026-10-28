using System.Threading;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// <see cref="TerminalBuffer.EnterBlockingWork"/> lets the PTY consumer thread drop <see cref="TerminalBuffer.SyncRoot"/>
/// during heavy work (so the render thread can capture snapshots) and take it back afterwards.
/// </summary>
public sealed class BlockingWorkScopeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static bool OtherThreadCanEnter(object syncRoot, int timeoutMs = 50)
    {
        bool entered = false;
        var thread = new Thread(() =>
        {
            entered = Monitor.TryEnter(syncRoot, timeoutMs);
            if (entered)
                Monitor.Exit(syncRoot);
        });
        thread.Start();
        Assert.True(thread.Join(Generous));
        return entered;
    }

    [Fact]
    public void InsideScope_OtherThreadCanTakeLock_AndAfterDisposeOwnerHoldsItAgain()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        using var insideScope = new ManualResetEventSlim(false);
        using var leaveScope = new ManualResetEventSlim(false);
        using var afterScope = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        bool heldBefore = false, heldInside = true, heldAfter = false;
        Exception? failure = null;

        var owner = new Thread(() =>
        {
            try
            {
                lock (buffer.SyncRoot)
                {
                    heldBefore = Monitor.IsEntered(buffer.SyncRoot);
                    using (buffer.EnterBlockingWork())
                    {
                        heldInside = Monitor.IsEntered(buffer.SyncRoot);
                        insideScope.Set();
                        leaveScope.Wait(Generous);
                    }

                    heldAfter = Monitor.IsEntered(buffer.SyncRoot);
                    afterScope.Set();
                    finish.Wait(Generous);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                insideScope.Set();
                afterScope.Set();
            }
        });
        owner.Start();

        Assert.True(insideScope.Wait(Generous));
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot), "lock must be free while the scope is open");
        leaveScope.Set();

        Assert.True(afterScope.Wait(Generous));
        Assert.False(OtherThreadCanEnter(buffer.SyncRoot), "lock must be re-acquired when the scope ends");
        finish.Set();
        Assert.True(owner.Join(Generous));

        Assert.Null(failure);
        Assert.True(heldBefore);
        Assert.False(heldInside);
        Assert.True(heldAfter);
        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
    }

    [Fact]
    public void WhenLockIsNotHeld_ScopeIsNoOp()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);

        using (buffer.EnterBlockingWork())
        {
            Assert.False(Monitor.IsEntered(buffer.SyncRoot));
        }

        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot));
    }

    [Fact]
    public void WhenLockIsHeldByAnotherThread_ScopeDoesNotTouchIt()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            lock (buffer.SyncRoot)
            {
                held.Set();
                release.Wait(Generous);
            }
        });
        holder.Start();
        Assert.True(held.Wait(Generous));

        // This thread does not own the monitor, so it must neither release nor acquire it.
        using (buffer.EnterBlockingWork())
        {
        }

        Assert.False(OtherThreadCanEnter(buffer.SyncRoot, timeoutMs: 20));
        release.Set();
        Assert.True(holder.Join(Generous));
    }

    [Fact]
    public void ExceptionInsideScope_StillReacquiresSoOuterLockUnwindsBalanced()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        bool heldAtCatch = true;

        try
        {
            lock (buffer.SyncRoot)
            {
                using var scope = buffer.EnterBlockingWork();
                throw new InvalidOperationException("boom");
            }
        }
        catch (InvalidOperationException)
        {
            heldAtCatch = Monitor.IsEntered(buffer.SyncRoot);
        }

        // Dispose re-entered, then the lock statement exited: balanced, nothing leaked in either direction.
        Assert.False(heldAtCatch);
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot));
    }

    [Fact]
    public void DisposeTwice_ReacquiresOnlyOnce()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        Monitor.Enter(buffer.SyncRoot);
        try
        {
            var scope = buffer.EnterBlockingWork();
            Assert.True(scope.LockReleased);
            scope.Dispose();
            scope.Dispose();
            Assert.False(scope.LockReleased);
        }
        finally
        {
            Monitor.Exit(buffer.SyncRoot);
        }

        // A double re-entry would leave the monitor held after the single Exit above.
        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot));
    }

    [Fact]
    public void RecursionDepthTwo_ReleasesOnlyOneLevelAndRestoresIt()
    {
        // Documented invariant: callers hold the lock exactly once. With deeper recursion only one level is
        // dropped (the lock stays held), and the original depth is restored on dispose.
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        Monitor.Enter(buffer.SyncRoot);
        Monitor.Enter(buffer.SyncRoot);
        try
        {
            using (buffer.EnterBlockingWork())
            {
                Assert.True(Monitor.IsEntered(buffer.SyncRoot));
                Assert.False(OtherThreadCanEnter(buffer.SyncRoot, timeoutMs: 20));
            }
        }
        finally
        {
            Monitor.Exit(buffer.SyncRoot);
            Assert.True(Monitor.IsEntered(buffer.SyncRoot));
            Monitor.Exit(buffer.SyncRoot);
        }

        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
    }

    [Fact]
    public void BufferDisposedWhileUnlocked_IsVisibleAfterScope()
    {
        var buffer = new TerminalBuffer(rows: 4, columns: 10);
        bool disposedInside, disposedAfter;
        lock (buffer.SyncRoot)
        {
            Assert.False(buffer.IsDisposed);
            using (buffer.EnterBlockingWork())
            {
                var disposer = new Thread(buffer.Dispose);
                disposer.Start();
                Assert.True(disposer.Join(Generous), "Dispose must not need SyncRoot while the scope is open");
                disposedInside = buffer.IsDisposed;
            }

            disposedAfter = buffer.IsDisposed;
        }

        Assert.True(disposedInside);
        Assert.True(disposedAfter);
    }

    // ------------------------------------------------ ReleaseSyncRootForBlockingWork / ReacquireSyncRoot (graphics transfer)

    [Fact]
    public void ReleaseHelper_HeldOnce_ReleasesForOthersAndReacquireRestoresIt()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        lock (buffer.SyncRoot)
        {
            Assert.True(buffer.ReleaseSyncRootForBlockingWork());
            Assert.False(Monitor.IsEntered(buffer.SyncRoot));
            Assert.True(OtherThreadCanEnter(buffer.SyncRoot), "lock must be free while released");

            buffer.ReacquireSyncRoot();
            Assert.True(Monitor.IsEntered(buffer.SyncRoot));
            Assert.False(OtherThreadCanEnter(buffer.SyncRoot, timeoutMs: 20), "lock must be held again");
        }

        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot), "exactly one level: the owner's lock statement balanced it");
    }

    [Fact]
    public void ReleaseHelper_NotHeld_DoesNothingAndReturnsFalse()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        Assert.False(buffer.ReleaseSyncRootForBlockingWork());
        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot));
    }

    [Fact]
    public void ReleaseHelper_HeldByAnotherThread_DoesNotTouchIt()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var owner = new Thread(() =>
        {
            lock (buffer.SyncRoot)
            {
                held.Set();
                release.Wait(Generous);
            }
        });
        owner.Start();
        Assert.True(held.Wait(Generous));

        Assert.False(buffer.ReleaseSyncRootForBlockingWork());
        Assert.False(OtherThreadCanEnter(buffer.SyncRoot, timeoutMs: 20), "the other thread still owns the lock");

        release.Set();
        Assert.True(owner.Join(Generous));
    }

    [Fact]
    public void ReleaseHelper_RecursiveHold_ReleasesNothingAndKeepsEveryLevel()
    {
        // Releasing one level of a recursive hold would not free the lock and would break the outer critical
        // section's expectations: the helper reports "nothing released" and the caller must not Reacquire.
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        Monitor.Enter(buffer.SyncRoot);
        Monitor.Enter(buffer.SyncRoot);
        try
        {
            Assert.False(buffer.ReleaseSyncRootForBlockingWork());
            Assert.True(Monitor.IsEntered(buffer.SyncRoot));
            Assert.False(OtherThreadCanEnter(buffer.SyncRoot, timeoutMs: 20));
        }
        finally
        {
            Monitor.Exit(buffer.SyncRoot);
            Assert.True(Monitor.IsEntered(buffer.SyncRoot), "both levels must still have been held");
            Monitor.Exit(buffer.SyncRoot);
        }

        Assert.False(Monitor.IsEntered(buffer.SyncRoot));
        Assert.True(OtherThreadCanEnter(buffer.SyncRoot));
    }

    [Fact]
    public void ReleaseHelper_ExceptionBetweenReleaseAndReacquire_FinallyRestoresTheLock()
    {
        using var buffer = new TerminalBuffer(rows: 4, columns: 10);
        lock (buffer.SyncRoot)
        {
            bool released = false;
            Assert.Throws<InvalidOperationException>(() =>
            {
                try
                {
                    released = buffer.ReleaseSyncRootForBlockingWork();
                    throw new InvalidOperationException("decode failed");
                }
                finally
                {
                    if (released)
                        buffer.ReacquireSyncRoot();
                }
            });
            Assert.True(released);
            Assert.True(Monitor.IsEntered(buffer.SyncRoot));
        }

        Assert.True(OtherThreadCanEnter(buffer.SyncRoot));
    }
}
