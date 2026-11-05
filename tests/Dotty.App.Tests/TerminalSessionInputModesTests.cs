using System;
using System.Linq;
using System.Threading;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalSessionInputModesTests
{
    [Fact]
    public void SrmResetEchoesSerializedUserInputThroughTerminalParser()
    {
        var pty = new SilentPty();
        using var session = new TerminalSession(2, 12, () => pty);
        session.Start();
        session.Parser.Feed("\x1b[12h"u8);
        Assert.False(session.Adapter.LocalEchoEnabled);

        session.WriteUserInput("hidden"u8);
        Assert.Equal(string.Empty, session.Adapter.Buffer.GetRowText(0).TrimEnd('\0', ' '));

        session.Parser.Feed("\x1b[12l"u8);
        Assert.True(session.Adapter.LocalEchoEnabled);
        session.WriteUserInput("hello"u8);

        Assert.Equal("hello", session.Adapter.Buffer.GetRowText(0).TrimEnd('\0', ' '));
        Assert.True(SpinWait.SpinUntil(() =>
            pty.WrittenBytes().SequenceEqual("hiddenhello"u8.ToArray()), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void KamBlocksUserInputButNotProtocolReplies()
    {
        var pty = new SilentPty();
        using var session = new TerminalSession(2, 12, () => pty);
        session.Start();
        session.Parser.Feed("\x1b[2h"u8);

        session.WriteUserInput("typed"u8);
        session.WriteInput("\x1b[1;1R"u8);

        Assert.True(SpinWait.SpinUntil(() => pty.WrittenBytes().Length == 6, TimeSpan.FromSeconds(5)));
        Assert.Equal("\x1b[1;1R", System.Text.Encoding.ASCII.GetString(pty.WrittenBytes()));
        Assert.Equal(string.Empty, session.Adapter.Buffer.GetRowText(0).TrimEnd('\0', ' '));
    }
    [Fact]
    public void KamTransitionDuringInputLockPreventsStaleEchoAndWrite()
    {
        var pty = new SilentPty();
        using var session = new TerminalSession(2, 12, () => pty);
        session.Start();
        session.Parser.Feed("\x1b[12l"u8);

        using var entered = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            entered.Set();
            session.WriteUserInput("blocked"u8);
        });
        var buffer = session.Adapter.Buffer;
        lock (buffer.SyncRoot)
        {
            worker.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            session.Parser.Feed("\x1b[2h\x1b[12h"u8);
        }

        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.False(SpinWait.SpinUntil(() => pty.WrittenBytes().Length != 0, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(string.Empty, buffer.GetRowText(0).TrimEnd('\0', ' '));
    }
}
