using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Pty;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

[CollectionDefinition(nameof(SessionAllocationCollection), DisableParallelization = true)]
public sealed class SessionAllocationCollection { }

[Collection(nameof(SessionAllocationCollection))]
public sealed class SessionAllocationTests
{
    [Fact]
    public void SmallInputWrites_AllocateNothingAfterWarmupAndPreserveOrder()
    {
        var pty = new LoopbackPty();
        using var session = new TerminalSession(2, 80, () => pty);
        session.BeginAllocationProbe();
        session.Start();

        byte[] input = [0x61, 0x62, 0x63, 0x0A];
        const int warmupWrites = 32;
        const int measuredWrites = 64;
        for (int i = 0; i < warmupWrites; i++)
            session.WriteInput(input);
        WaitForFlushCount(pty.Input, warmupWrites);
        WaitForInputWriterMeasuredWrites(session, warmupWrites);
        session.BeginAllocationProbe();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measuredWrites; i++)
            session.WriteInput(input);
        long callerAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        WaitForFlushCount(pty.Input, warmupWrites + measuredWrites);
        WaitForInputWriterMeasuredWrites(session, measuredWrites);
        Assert.Equal(measuredWrites, session.PtyInputWriterMeasuredWrites);
        session.EndAllocationProbe();
        Assert.Equal(0L, callerAllocated);
        Assert.Equal(0L, session.PtyInputWriterAllocatedBytes);

        byte[] captured = pty.Input.CopyWrittenBytes();
        Assert.Equal((warmupWrites + measuredWrites) * input.Length, captured.Length);
        for (int i = 0; i < captured.Length; i++)
            Assert.Equal(input[i % input.Length], captured[i]);

        byte[] paste = new byte[100_000];
        for (int i = 0; i < paste.Length; i++)
            paste[i] = (byte)(i % 251);
        session.WriteInput(paste);
        WaitForFlushCount(pty.Input, warmupWrites + measuredWrites + 1);

        captured = pty.Input.CopyWrittenBytes();
        Assert.Equal((warmupWrites + measuredWrites) * input.Length + paste.Length, captured.Length);
        for (int i = 0; i < paste.Length; i++)
            Assert.Equal(paste[i], captured[(warmupWrites + measuredWrites) * input.Length + i]);
    }

    [Fact]
    public void OutputPipeline_DeliversSteadyStateChunkWithoutManagedAllocations()
    {
        var pty = new LoopbackPty();
        using var session = new TerminalSession(2, 80, () => pty);
        var renderSignal = new RenderSignal();
        session.RenderScheduled += renderSignal.OnRenderScheduled;
        session.BeginAllocationProbe();
        session.Start();

        pty.Output.Publish("a"u8);
        WaitForCell(session, 'a');
        WaitForOutputIdle(session);
        WaitForRenderCount(renderSignal, 1);
        WaitForOutputAllocationSamples(session);
        session.BeginAllocationProbe();

        int previousRenderCount = renderSignal.Count;
        pty.Output.Publish("\rb"u8);
        WaitForCell(session, 'b');
        WaitForOutputIdle(session);
        WaitForRenderCount(renderSignal, previousRenderCount + 1);
        WaitForOutputAllocationSamples(session);
        session.EndAllocationProbe();

        Assert.Equal(1, session.PtyOutputReaderMeasuredChunks);
        Assert.Equal(1, session.PtyOutputConsumerMeasuredChunks);
        long readerAllocated = session.PtyOutputReaderAllocatedBytes;
        long consumerAllocated = session.PtyOutputConsumerAllocatedBytes;
        Assert.True(readerAllocated == 0,
            $"The output reader allocated {readerAllocated} bytes; the consumer allocated {consumerAllocated} bytes.");
        Assert.True(consumerAllocated == 0,
            $"The output consumer allocated {consumerAllocated} bytes; the reader allocated {readerAllocated} bytes.");
    }
    [Fact]
    public void CursorPositionReplyAllocatesNothingAfterWarmup()
    {
        var pty = new LoopbackPty();
        using var session = new TerminalSession(2, 80, () => pty);
        session.BeginAllocationProbe();
        session.Start();

        const int warmupReplies = 8;
        for (int i = 0; i < warmupReplies; i++)
        {
            pty.Output.Publish("\x1b[6n"u8);
            WaitForFlushCount(pty.Input, i + 1);
            WaitForOutputIdle(session);
        }
        WaitForInputWriterMeasuredWrites(session, warmupReplies);

        session.BeginAllocationProbe();
        pty.Output.Publish("\x1b[6n"u8);
        WaitForFlushCount(pty.Input, warmupReplies + 1);
        WaitForOutputIdle(session);
        WaitForOutputAllocationSamples(session);
        WaitForInputWriterMeasuredWrites(session, 1);
        session.EndAllocationProbe();

        Assert.Equal(0L, session.PtyOutputConsumerAllocatedBytes);
        Assert.Equal(0L, session.PtyOutputReaderAllocatedBytes);
        Assert.Equal(0L, session.PtyInputWriterAllocatedBytes);
        byte[] written = pty.Input.CopyWrittenBytes();
        Assert.True(written.AsSpan().EndsWith("\x1b[1;1R"u8));
    }


    private static void WaitForInputWriterMeasuredWrites(TerminalSession session, int expected)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (session.PtyInputWriterMeasuredWrites < expected)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The PTY input writer did not finish measured writes.");
            Thread.Yield();
        }
    }

    private static void WaitForOutputAllocationSamples(TerminalSession session)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (session.PtyOutputReaderMeasuredChunks < 1 || session.PtyOutputConsumerMeasuredChunks < 1)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The PTY output threads did not finish measured chunks.");
            Thread.Yield();
        }
    }

    private static void WaitForFlushCount(CountingInputStream input, int expected)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (input.FlushCount < expected)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The PTY input writer did not drain its queue.");
            Thread.Yield();
        }
    }

    private static void WaitForCell(TerminalSession session, char expected)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (session.Adapter.Buffer.GetCell(0, 0).Rune != expected)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("PTY output was not parsed before the timeout.");
            Thread.Yield();
        }
    }

    private static void WaitForOutputIdle(TerminalSession session)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (session.OutputBacklogged)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The PTY output consumer did not become idle.");
            Thread.Yield();
        }
    }

    private static void WaitForRenderCount(RenderSignal signal, int expected)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (signal.Count < expected)
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("The adapter did not schedule its completed output render.");
            Thread.Yield();
        }
    }

    private sealed class RenderSignal
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public void OnRenderScheduled() => Interlocked.Increment(ref _count);
    }

    private sealed class LoopbackPty : IPty
    {
        private readonly CountingInputStream _input = new();

        public bool IsRunning { get; private set; }
        public int ProcessId => 1;
        public Stream InputStream => _input;
        public Stream OutputStream { get; } = new LoopbackOutputStream();
        public CountingInputStream Input => _input;
        public LoopbackOutputStream Output => (LoopbackOutputStream)OutputStream;
        public event EventHandler<int>? ProcessExited
        {
            add { }
            remove { }
        }

        public void Start(
            string? shell = null,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            IDictionary<string, string>? environmentVariables = null) => IsRunning = true;

        public void Resize(int columns, int rows) { }
        public void Kill(bool force = false) => IsRunning = false;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public void Dispose()
        {
            IsRunning = false;
            _input.Dispose();
            Output.Dispose();
        }
    }

    private sealed class CountingInputStream : Stream
    {
        private readonly byte[] _written = new byte[262144];
        private int _writtenCount;
        private int _flushCount;

        public int FlushCount => Volatile.Read(ref _flushCount);

        public byte[] CopyWrittenBytes()
        {
            var copy = new byte[Volatile.Read(ref _writtenCount)];
            _written.AsSpan(0, copy.Length).CopyTo(copy);
            return copy;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => Interlocked.Increment(ref _flushCount);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int offset = _writtenCount;
            buffer.CopyTo(_written.AsSpan(offset));
            Volatile.Write(ref _writtenCount, offset + buffer.Length);
        }
    }

    private sealed class LoopbackOutputStream : Stream
    {
        private readonly byte[] _pending = new byte[4096];
        private int _length;
        private int _offset;
        private bool _disposed;

        public void Publish(ReadOnlySpan<byte> bytes)
        {
            lock (_pending)
            {
                if (_offset != _length)
                    throw new InvalidOperationException("The previous output chunk has not been consumed.");
                bytes.CopyTo(_pending);
                _length = bytes.Length;
                _offset = 0;
                Monitor.PulseAll(_pending);
            }
        }

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
            while (true)
            {
                lock (_pending)
                {
                    if (_disposed)
                        return 0;
                    if (_offset < _length)
                    {
                        int length = Math.Min(count, _length - _offset);
                        _pending.AsSpan(_offset, length).CopyTo(buffer.AsSpan(offset, length));
                        _offset += length;
                        return length;
                    }
                    Monitor.Wait(_pending);
                }
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            lock (_pending)
            {
                _disposed = true;
                Monitor.PulseAll(_pending);
            }
            base.Dispose(disposing);
        }
    }
}
