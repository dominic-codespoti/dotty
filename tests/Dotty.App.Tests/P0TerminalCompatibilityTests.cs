using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Pty;
using Dotty.Runtime.Input;
using Dotty.Runtime.Sessions;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using SilkKey = Silk.NET.Input.Key;
using Xunit;

namespace Dotty.App.Tests;

public sealed class P0TerminalCompatibilityTests
{
    [Fact]
    public void ApplicationCursorMode_IsObservableOnAdapter()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = adapter };

        parser.Feed("\x1b[?1h"u8);
        Assert.True(adapter.ApplicationCursorKeysEnabled);

        parser.Feed("\x1b[?1l"u8);
        Assert.False(adapter.ApplicationCursorKeysEnabled);
    }

    [Fact]
    public void FullReset_ClearsKeyboardModesAndRestoresNormalCursorEncoding()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = adapter };

        parser.Feed("\x1b[?1h"u8);
        parser.Feed("\x1b="u8);
        parser.Feed("\x1b[?1u"u8);

        parser.Feed("\u001bc"u8);

        Assert.False(adapter.ApplicationCursorKeysEnabled);
        Assert.False(adapter.KeypadApplicationMode);
        Assert.Equal(0, adapter.KittyKeyboardMode);

        var bytes = SilkKeyMapperTestEncoding.Encode(
            SilkKey.Up,
            ctrl: false,
            shift: false,
            alt: false,
            keypadAppMode: adapter.KeypadApplicationMode,
            kittyMode: adapter.KittyKeyboardMode,
            super: false,
            applicationCursorKeys: adapter.ApplicationCursorKeysEnabled);

        Assert.Equal("\x1b[A", Encoding.ASCII.GetString(bytes!));
    }

    [Fact]
    public void KittyMode_SetAndQuery_UsesExactReply()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());

        parser.Feed("\x1b[?1u"u8);
        parser.Feed("\x1b[?u"u8);

        Assert.Equal(1, adapter.KittyKeyboardMode);
        Assert.Equal(new[] { "\x1b[1u" }, replies);
    }

    [Fact]
    public void KittyMode_PropagatesToSpecialKeyMapper()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = adapter };
        parser.Feed("\x1b[?1u"u8);

        var bytes = SilkKeyMapperTestEncoding.Encode(
            SilkKey.Up,
            ctrl: false,
            shift: false,
            alt: false,
            keypadAppMode: false,
            kittyMode: adapter.KittyKeyboardMode,
            super: false,
            applicationCursorKeys: adapter.ApplicationCursorKeysEnabled);

        Assert.Equal("\x1b[A", Encoding.ASCII.GetString(bytes!));
    }

    [Fact]
    public void SuperModifier_UsesMetaModifierAndUnknownKeyIsUnsupported()
    {
        var bytes = SilkKeyMapperTestEncoding.Encode(
            SilkKey.Up,
            ctrl: false,
            shift: false,
            alt: false,
            keypadAppMode: false,
            kittyMode: 0,
            super: true,
            applicationCursorKeys: false);

        Assert.Equal("\x1b[1;9A", Encoding.ASCII.GetString(bytes!));
        Assert.Null(SilkKeyMapperTestEncoding.Encode(
            SilkKey.Unknown,
            ctrl: false,
            shift: false,
            alt: false,
            keypadAppMode: false));
    }

    [Theory]
    [InlineData(false, "a\nb")]
    [InlineData(true, "\x1b[200~a\nb\x1b[201~")]
    public void BracketedPaste_PreservesExactUtf8Payload(bool enabled, string expected)
    {
        var bytes = BracketedPasteEncoder.Encode("a\nb", enabled);

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }


    [Theory]
    [InlineData(false, "a\nb")]
    [InlineData(true, "\x1b[200~a\nb\x1b[201~")]
    public void ClipboardPasteRouter_UsesActiveAdapterMode(bool enabled, string expected)
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        adapter.OnSetBracketedPasteMode(enabled);

        var bytes = ClipboardPasteRouter.Encode("a\nb", adapter);

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }
    [Fact]
    public void BracketedPaste_EmptyTextIsNoOp()
    {
        Assert.Empty(BracketedPasteEncoder.Encode(string.Empty, bracketed: true));
    }

    [Fact]
    public void FocusReports_AreSerializedAndRespectAdapterMode()
    {

        var pty = new CapturingPty();
        using var session = new TerminalSession(2, 8, () => pty);
        session.Start();
        session.Parser.Feed("\x1b[?1004h"u8);

        session.SendFocusReport(focused: true);
        session.SendFocusReport(focused: false);
        Assert.True(SpinWait.SpinUntil(() => pty.InputCount >= 6, TimeSpan.FromSeconds(5)));

        Assert.Equal("\x1b[I\x1b[O", Encoding.ASCII.GetString(pty.InputBytes));
    }

    [Fact]
    public void StartWithOptionsUsesInjectedPtyFactory()
    {

        var pty = new CapturingPty();
        using var session = new TerminalSession(2, 8, () => pty);
        session.StartWithOptions(shell: "/bin/sh");

        Assert.True(pty.IsRunning);
    }

    [Fact]
    public void ProcessExit_PropagatesOnce_AndDisposalIsSafe()
    {
        var pty = new CapturingPty();
        using var session = new TerminalSession(2, 8, () => pty);
        var exitCodes = new List<int>();
        session.ProcessExited += exitCodes.Add;

        session.Start();
        pty.RaiseExit(23);
        pty.RaiseExit(99);

        Assert.True(SpinWait.SpinUntil(() => exitCodes.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { 23 }, exitCodes);
        session.Dispose();
        pty.RaiseExit(7);
        Assert.Equal(new[] { 23 }, exitCodes);
    }

    [Fact]
    public async Task ProcessExit_WaitsForFinalOutputToBeParsed()
    {
        var output = new CapturingPty.BlockingOutputStream(Encoding.ASCII.GetBytes("final output"));
        var pty = new CapturingPty(output);
        using var session = new TerminalSession(2, 20, () => pty);
        var exited = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool finalOutputVisibleAtExit = false;
        session.ProcessExited += code =>
        {
            finalOutputVisibleAtExit = session.Adapter.Buffer.GetCell(0, 0).Rune == 'f';
            exited.TrySetResult(code);
        };

        session.Start();
        pty.RaiseExit(23);

        var cancellationToken = TestContext.Current.CancellationToken;
        var completed = await Task.WhenAny(
            exited.Task,
            Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
        Assert.NotSame(exited.Task, completed);
        output.Release();

        Assert.Equal(23, await exited.Task.WaitAsync(cancellationToken));
        Assert.True(finalOutputVisibleAtExit);
        Assert.Equal('f', (char)session.Adapter.Buffer.GetCell(0, 0).Rune);
    }

    [Fact]
    public void FocusReports_DisabledDisconnectedAndDisposedAreNoOps()
    {
        var disconnectedPty = new CapturingPty();
        using (var disconnected = new TerminalSession(2, 8, () => disconnectedPty))
        {
            disconnected.Parser.Feed("\x1b[?1004h"u8);
            disconnected.SendFocusReport(true);
        }
        Assert.Empty(disconnectedPty.InputBytes);

        var pty = new CapturingPty();
        var session = new TerminalSession(2, 8, () => pty);
        session.Start();
        session.SendFocusReport(true);
        session.Parser.Feed("\x1b[?1004h"u8);
        session.SendFocusReport(true);
        Assert.True(SpinWait.SpinUntil(() => pty.InputCount >= 3, TimeSpan.FromSeconds(5)));
        session.Dispose();
        session.SendFocusReport(false);
        Thread.Sleep(20);

        Assert.Equal("\x1b[I", Encoding.ASCII.GetString(pty.InputBytes));
    }

    [Fact]
    public void WindowPresentationGate_TracksSynchronizedModeDirectly()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new Dotty.Terminal.Parser.BasicAnsiParser { Handler = adapter };

        Assert.True(WindowPresentationGate.ShouldPresent(null));
        Assert.True(WindowPresentationGate.ShouldPresent(adapter));

        parser.Feed("\x1b[?2026h"u8);
        Assert.False(WindowPresentationGate.ShouldPresent(adapter));

        parser.Feed("\x1b[?2026l"u8);
        Assert.True(WindowPresentationGate.ShouldPresent(adapter));
    }

    private sealed class CapturingPty : IPty
    {
        public bool IsRunning { get; private set; }
        public int ProcessId => 1;
        public Stream OutputStream { get; }
        private CapturingStream Input { get; } = new();
        public int InputCount => Input.CapturedCount;
        public byte[] InputBytes => Input.CapturedBytes;
        Stream? IPty.InputStream => Input;

        public CapturingPty(Stream? outputStream = null)
        {
            OutputStream = outputStream ?? new MemoryStream();
        }
        private event EventHandler<int>? _processExited;
        public event EventHandler<int>? ProcessExited
        {
            add => _processExited += value;
            remove => _processExited -= value;
        }

        public void RaiseExit(int exitCode) => _processExited?.Invoke(this, exitCode);

        public void Start(string? shell = null, int columns = 80, int rows = 24,
            string? workingDirectory = null,
            IDictionary<string, string>? environmentVariables = null) => IsRunning = true;

        public void Resize(int columns, int rows) { }
        public void Kill(bool force = false) => IsRunning = false;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void Dispose() => IsRunning = false;

        public sealed class BlockingOutputStream : Stream
        {
            private readonly byte[] _payload;
            private int _offset;
            private bool _released;
            private TaskCompletionSource<int>? _pendingRead;
            private readonly object _sync = new();

            public BlockingOutputStream(byte[] payload)
            {
                _payload = payload;
            }

            public void Release()
            {
                TaskCompletionSource<int>? pending;
                lock (_sync)
                {
                    _released = true;
                    pending = _pendingRead;
                    _pendingRead = null;
                }
                pending?.TrySetResult(0);
            }

            public override Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                lock (_sync)
                {
                    if (_offset < _payload.Length)
                    {
                        var length = Math.Min(count, _payload.Length - _offset);
                        Array.Copy(_payload, _offset, buffer, offset, length);
                        _offset += length;
                        return Task.FromResult(length);
                    }

                    if (_released)
                        return Task.FromResult(0);

                    var pending = new TaskCompletionSource<int>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingRead = pending;
                    if (cancellationToken.CanBeCanceled)
                    {
                        cancellationToken.Register(
                            static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
                            pending);
                    }
                    return pending.Task;
                }
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _payload.Length;
            public override long Position { get => _offset; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class CapturingStream : MemoryStream
        {
            public List<byte> Bytes { get; } = new();
            public int CapturedCount
            {
                get { lock (Bytes) return Bytes.Count; }
            }

            public byte[] CapturedBytes
            {
                get { lock (Bytes) return Bytes.ToArray(); }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (Bytes)
                    Bytes.AddRange(buffer.AsSpan(offset, count).ToArray());
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
