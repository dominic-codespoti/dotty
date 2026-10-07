using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Pty;

namespace Dotty.App.Tests;

/// <summary>
/// Inert <see cref="IPty"/> for tests that need a started terminal session but
/// not a child process. It emits no output and never exits on its own, so the
/// platform PTY's startup behaviour (ConPTY clears the screen and resets modes
/// when it starts, for example) cannot race with state a test injects through
/// the parser. Pass <see cref="Create"/> to <c>new TerminalTabManager(...)</c>.
/// Everything the session writes to the child is recorded and available from
/// <see cref="WrittenBytes"/>.
/// </summary>
internal sealed class SilentPty : IPty
{
    private readonly BlockingEmptyStream _output = new();
    private readonly RecordingStream _input = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IPty Create() => new SilentPty();

    public bool IsRunning { get; private set; }

    public int ProcessId => 1;

    public Stream? OutputStream => _output;

    public Stream? InputStream => _input;

    public event EventHandler<int>? ProcessExited
    {
        add { }
        remove { }
    }

    public byte[] WrittenBytes() => _input.ToArray();

    public void Start(
        string? shell = null,
        int columns = 80,
        int rows = 24,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        IReadOnlyList<string>? command = null,
        bool shellIsExecutable = false) => IsRunning = true;

    public void Resize(int columns, int rows)
    {
    }

    public void Kill(bool force = false)
    {
        IsRunning = false;
        _output.Dispose();
        _exit.TrySetResult(0);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    public void Dispose() => Kill(force: true);

    /// <summary>Reads block until the stream is disposed, then report end of stream.</summary>
    private sealed class BlockingEmptyStream : Stream
    {
        private readonly ManualResetEventSlim _closed = new(false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _closed.Wait();
            return 0;
        }

        public override int Read(Span<byte> buffer)
        {
            _closed.Wait();
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _closed.Set();
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly object _gate = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray()
        {
            lock (_gate)
                return _buffer.ToArray();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
                _buffer.Write(buffer);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
