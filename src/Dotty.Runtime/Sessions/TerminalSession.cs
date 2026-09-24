using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Parser;
using Dotty.Abstractions.Pty;
using Dotty.NativePty;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Runtime.Sessions;

public class TerminalSession : IDisposable
{
    private const int InitialPtyOutputChunkCount = 2;
    private const int PtyOutputChunkCount = 32;
    private const int PtyOutputChunkSize = 128 * 1024;

    private readonly Func<IPty> _ptyFactory;
    private readonly bool _checkPtySupport;
    private readonly object _lifecycleLock = new();
    private readonly object _ptyInputQueueLock = new();
    private readonly object _ptyOutputQueueLock = new();
    private byte[][] _ptyOutputChunks = CreateOutputChunks();
    private byte[][] _ptyOutputChunkScratch = new byte[PtyOutputChunkCount][];
    private int[] _ptyOutputLengths = new int[PtyOutputChunkCount];
    private int[] _ptyOutputLengthScratch = new int[PtyOutputChunkCount];
    private readonly ManualResetEventSlim _ptyOutputAvailable = new(false);
    private readonly ManualResetEventSlim _ptyOutputSpaceAvailable = new(true);
    private byte[] _ptyInputBuffer = new byte[4096];
    private int[] _ptyInputLengths = new int[128];
    private readonly ManualResetEventSlim _ptyInputAvailable = new(false);
    private IPty? _pty;
    private CancellationTokenSource? _readCancellation;
    private Thread? _ptyInputWriterThread;
    private Thread? _ptyOutputReaderThread;
    private Thread? _ptyOutputConsumerThread;
    private Stream? _ptyOutputReader;
    private CancellationToken _ptyOutputCancellationToken;
    private int _ptyInputReadOffset;
    private int _ptyInputWriteOffset;
    private int _ptyInputQueuedBytes;
    private int _ptyInputLengthRead;
    private int _ptyInputLengthWrite;
    private int _ptyInputLengthCount;
    private int _inputWaitEventDisposed;
    private int _outputWaitEventsDisposed;
    private bool _ptyInputWriterStopping;
    private int _ptyOutputReadIndex;
    private int _ptyOutputWriteIndex;
    private int _ptyOutputCount;
    private int _ptyOutputCapacity = InitialPtyOutputChunkCount;
    private bool _ptyOutputReaderCompleted;
    private bool _disposed;
    private bool _suppressProcessExit;
    private int _processExitRaised;
    private bool _hasReceivedInitialResize = false;
    private int _initialCols = 0;
    private int _initialRows = 0;
    private int _isStarted;
    private int _pendingOutputChunks;
    // Test-only checkpoints keep allocation measurements local to the PTY worker
    // threads instead of observing unrelated process-wide activity.
    private int _allocationProbeEnabled;
    private long _ptyInputWriterAllocatedBytes;
    private int _ptyInputWriterMeasuredWrites;
    private long _ptyOutputReaderAllocatedBytes;
    private int _ptyOutputReaderMeasuredChunks;
    private long _ptyOutputConsumerAllocatedBytes;
    private int _ptyOutputConsumerMeasuredChunks;
    private Task _ptyPipelineCompletion = Task.CompletedTask;
    private TaskCompletionSource<object?>? _ptyPipelineCompletionSource;

    internal void BeginAllocationProbe()
    {
        Interlocked.Exchange(ref _ptyInputWriterAllocatedBytes, 0);
        Volatile.Write(ref _ptyInputWriterMeasuredWrites, 0);
        Interlocked.Exchange(ref _ptyOutputReaderAllocatedBytes, 0);
        Volatile.Write(ref _ptyOutputReaderMeasuredChunks, 0);
        Interlocked.Exchange(ref _ptyOutputConsumerAllocatedBytes, 0);
        Volatile.Write(ref _ptyOutputConsumerMeasuredChunks, 0);
        Volatile.Write(ref _allocationProbeEnabled, 1);
    }

    internal void EndAllocationProbe() => Volatile.Write(ref _allocationProbeEnabled, 0);

    internal long PtyInputWriterAllocatedBytes => Interlocked.Read(ref _ptyInputWriterAllocatedBytes);
    internal int PtyInputWriterMeasuredWrites => Volatile.Read(ref _ptyInputWriterMeasuredWrites);
    internal long PtyOutputReaderAllocatedBytes => Interlocked.Read(ref _ptyOutputReaderAllocatedBytes);
    internal int PtyOutputReaderMeasuredChunks => Volatile.Read(ref _ptyOutputReaderMeasuredChunks);
    internal long PtyOutputConsumerAllocatedBytes => Interlocked.Read(ref _ptyOutputConsumerAllocatedBytes);
    internal int PtyOutputConsumerMeasuredChunks => Volatile.Read(ref _ptyOutputConsumerMeasuredChunks);

    private static byte[][] CreateOutputChunks()
    {
        var chunks = new byte[PtyOutputChunkCount][];
        for (int i = 0; i < InitialPtyOutputChunkCount; i++)
            chunks[i] = new byte[PtyOutputChunkSize];
        return chunks;
    }

    private void GrowPtyOutputRing()
    {
        int oldCapacity = _ptyOutputCapacity;
        int newCapacity = Math.Min(oldCapacity * 2, PtyOutputChunkCount);
        Array.Clear(_ptyOutputChunkScratch, 0, _ptyOutputChunkScratch.Length);
        Array.Clear(_ptyOutputLengthScratch, 0, _ptyOutputLengthScratch.Length);

        for (int i = 0; i < _ptyOutputCount; i++)
        {
            int oldIndex = _ptyOutputReadIndex + i;
            if (oldIndex >= oldCapacity)
                oldIndex -= oldCapacity;
            _ptyOutputChunkScratch[i] = _ptyOutputChunks[oldIndex];
            _ptyOutputLengthScratch[i] = _ptyOutputLengths[oldIndex];
        }
        for (int i = oldCapacity; i < newCapacity; i++)
            _ptyOutputChunkScratch[i] = new byte[PtyOutputChunkSize];

        (_ptyOutputChunks, _ptyOutputChunkScratch) = (_ptyOutputChunkScratch, _ptyOutputChunks);
        (_ptyOutputLengths, _ptyOutputLengthScratch) = (_ptyOutputLengthScratch, _ptyOutputLengths);
        Array.Clear(_ptyOutputChunkScratch, 0, _ptyOutputChunkScratch.Length);
        Array.Clear(_ptyOutputLengthScratch, 0, _ptyOutputLengthScratch.Length);
        _ptyOutputCapacity = newCapacity;
        _ptyOutputReadIndex = 0;
        _ptyOutputWriteIndex = _ptyOutputCount;
    }

    public ITerminalParser Parser { get; }
    public TerminalAdapter Adapter { get; }
    public bool IsStarted => Volatile.Read(ref _isStarted) != 0;
    public bool OutputBacklogged => Volatile.Read(ref _pendingOutputChunks) != 0;

    public event Action<byte[]>? RawInputReceived;
    public event Action<string>? ClipboardWriteRequested;
    public event Action<string>? TitleChanged;
    public event Action? RenderScheduled;
    public event Action<int>? ProcessExited;


    private TimeSpan _refreshInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Measured display frame cadence (4-33 ms clamp). The historical render
    /// poll timer that consumed this was removed in the demand-driven
    /// scheduling cutover; the value now serves as a diagnostic display-cadence
    /// signal updated by the presentation gate.
    /// </summary>
    public TimeSpan RefreshInterval
    {
        get => _refreshInterval;
        set
        {
            if (value < TimeSpan.FromMilliseconds(4))
                value = TimeSpan.FromMilliseconds(4);
            else if (value > TimeSpan.FromMilliseconds(33))
                value = TimeSpan.FromMilliseconds(33);
            _refreshInterval = value;
        }
    }

    public TerminalSession(int rows = 24, int columns = 80)
        : this(rows, columns, PtyFactory.Create, checkPtySupport: true)
    {
    }

    internal TerminalSession(int rows, int columns, Func<IPty> ptyFactory)
        : this(rows, columns, ptyFactory, checkPtySupport: false)
    {
    }

    private TerminalSession(
        int rows,
        int columns,
        Func<IPty> ptyFactory,
        bool checkPtySupport)
    {
        _ptyFactory = ptyFactory ?? throw new ArgumentNullException(nameof(ptyFactory));
        _checkPtySupport = checkPtySupport;
        Parser = new BasicAnsiParser();
        // The arena is allocated at construction; honor the configured scrollback
        // depth here so the default (5k) actually applies instead of the
        // TerminalBuffer fallback (10k).
        var scrollbackLines = DottyDefaults.ScrollbackLines;
        if (scrollbackLines <= 0)
            scrollbackLines = 5000;
        Adapter = new TerminalAdapter(rows: rows, columns: columns, scrollbackCapacity: scrollbackLines);
        Parser.Handler = Adapter;
        Adapter.RenderRequested += _ => RenderScheduled?.Invoke();
        Adapter.ReplyRequested += OnAdapterReplyRequested;
        Adapter.ClipboardWriteRequested += text => ClipboardWriteRequested?.Invoke(text);
        Adapter.TitleChanged += title => TitleChanged?.Invoke(title);
    }

    public void Start()
    {
        if (!BeginStart()) return;
        try
        {
            if (_checkPtySupport && !PtyFactory.IsSupported)
                throw new PtyException(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");
            _pty = _ptyFactory();
            _pty.ProcessExited += OnPtyProcessExited;
            var shell = Environment.GetEnvironmentVariable("DOTTY_SHELL")
                        ?? Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(shell)) shell = null;
            _initialCols = Adapter.Buffer?.Columns ?? 80;
            _initialRows = Adapter.Buffer?.Rows ?? 24;
            _hasReceivedInitialResize = false;
            _pty.Start(shell: shell, columns: _initialCols, rows: _initialRows);
            _readCancellation = new CancellationTokenSource();
            StartPtyPipeline(_readCancellation.Token);
        }
        catch
        {
            ResetFailedStart();
            throw;
        }
    }

    public void StartWithOptions(
        string? shell = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null)
    {
        if (!BeginStart()) return;
        try
        {
            if (_checkPtySupport && !PtyFactory.IsSupported)
                throw new PtyException(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");
            _pty = _ptyFactory();
            _pty.ProcessExited += OnPtyProcessExited;
            shell ??= Environment.GetEnvironmentVariable("DOTTY_SHELL")
                      ?? Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(shell)) shell = null;
            _initialCols = Adapter.Buffer?.Columns ?? 80;
            _initialRows = Adapter.Buffer?.Rows ?? 24;
            _hasReceivedInitialResize = false;
            _pty.Start(shell: shell, columns: _initialCols, rows: _initialRows,
                       workingDirectory: workingDirectory, environmentVariables: environmentVariables);
            _readCancellation = new CancellationTokenSource();
            StartPtyPipeline(_readCancellation.Token);
        }
        catch
        {
            ResetFailedStart();
            throw;
        }
    }

    private bool BeginStart()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TerminalSession));
            if (Volatile.Read(ref _isStarted) != 0) return false;
            Volatile.Write(ref _isStarted, 1);
            _suppressProcessExit = false;
            Volatile.Write(ref _processExitRaised, 0);
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ptyPipelineCompletionSource = completion;
            _ptyPipelineCompletion = completion.Task;
            return true;
        }
    }

    private void ResetFailedStart()
    {
        TaskCompletionSource<object?>? completion;
        lock (_lifecycleLock)
        {
            Volatile.Write(ref _isStarted, 0);
            _suppressProcessExit = true;
            Volatile.Write(ref _processExitRaised, 1);
            completion = _ptyPipelineCompletionSource;
        }

        try { _readCancellation?.Cancel(); } catch { }
        lock (_ptyInputQueueLock)
        {
            _ptyInputWriterStopping = true;
            _ptyInputAvailable.Set();
        }
        _ptyOutputSpaceAvailable.Set();
        _ptyOutputAvailable.Set();
        try { if (_pty != null) _pty.ProcessExited -= OnPtyProcessExited; } catch { }
        try { _pty?.Dispose(); } catch { }
        JoinThread(_ptyInputWriterThread);
        JoinThread(_ptyOutputReaderThread, WorkerShutdownTimeoutMs);
        JoinThread(_ptyOutputConsumerThread, WorkerShutdownTimeoutMs);
        try { _readCancellation?.Dispose(); } catch { }

        lock (_ptyInputQueueLock)
        {
            _ptyInputWriterThread = null;
            _ptyInputLengthCount = 0;
            _ptyInputQueuedBytes = 0;
            _ptyInputLengthRead = 0;
            _ptyInputLengthWrite = 0;
            _ptyInputReadOffset = 0;
            _ptyInputWriteOffset = 0;
        }
        _ptyOutputReaderThread = null;
        _ptyOutputConsumerThread = null;
        _ptyOutputReader = null;
        _ptyOutputCount = 0;
        _ptyOutputReadIndex = 0;
        _ptyOutputWriteIndex = 0;
        _ptyOutputReaderCompleted = false;
        Interlocked.Exchange(ref _pendingOutputChunks, 0);
        lock (_lifecycleLock)
        {
            _ptyPipelineCompletionSource = null;
            _ptyPipelineCompletion = Task.CompletedTask;
        }
        completion?.TrySetResult(null);
        _pty = null;
        _readCancellation = null;
    }

    // Bounds joins on PTY workers during shutdown. A reader blocked on a stream
    // that never reaches EOF must not hang Dispose; it is a background thread.
    private const int WorkerShutdownTimeoutMs = 5000;

    private static bool JoinThread(Thread? thread, int timeoutMs = Timeout.Infinite)
    {
        if (thread == null
            || thread == Thread.CurrentThread
            || (thread.ThreadState & ThreadState.Unstarted) != 0)
            return true;
        return thread.Join(timeoutMs);
    }

    private void DisposeInputWaitEvent()
    {
        if (Interlocked.Exchange(ref _inputWaitEventDisposed, 1) == 0)
            _ptyInputAvailable.Dispose();
    }

    private void DisposeOutputWaitEvents()
    {
        if (Interlocked.Exchange(ref _outputWaitEventsDisposed, 1) == 0)
        {
            _ptyOutputAvailable.Dispose();
            _ptyOutputSpaceAvailable.Dispose();
        }
    }

    public void WriteInput(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty || _pty?.InputStream == null) return;
        QueuePtyInputWrite(data);
    }

    public void SendFocusReport(bool focused)
    {
        if (_disposed || !Adapter.FocusReportingEnabled || _pty?.InputStream == null)
            return;

        if (focused)
            QueuePtyInputWrite("\x1b[I"u8);
        else
            QueuePtyInputWrite("\x1b[O"u8);
    }

    private void OnAdapterReplyRequested(ReadOnlySpan<char> reply)
    {
        if (!reply.IsEmpty)
            QueuePtyInputAsciiWrite(reply);
    }

    private void QueuePtyInputWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        lock (_ptyInputQueueLock)
        {
            if (_disposed || _ptyInputWriterStopping || _pty?.InputStream == null)
                return;

            EnsurePtyInputCapacity(data.Length);
            EnsurePtyInputLengthCapacity();

            int wasEmpty = _ptyInputLengthCount;
            int writeOffset = _ptyInputWriteOffset;
            int firstLength = Math.Min(data.Length, _ptyInputBuffer.Length - writeOffset);
            data[..firstLength].CopyTo(_ptyInputBuffer.AsSpan(writeOffset, firstLength));
            data[firstLength..].CopyTo(_ptyInputBuffer.AsSpan(0, data.Length - firstLength));
            EnqueuePtyInputLength(data.Length, wasEmpty == 0);
        }
    }

    private void QueuePtyInputAsciiWrite(ReadOnlySpan<char> text)
    {
        lock (_ptyInputQueueLock)
        {
            if (_disposed || _ptyInputWriterStopping || _pty?.InputStream == null)
                return;

            EnsurePtyInputCapacity(text.Length);
            EnsurePtyInputLengthCapacity();

            int wasEmpty = _ptyInputLengthCount;
            int writeOffset = _ptyInputWriteOffset;
            for (int i = 0; i < text.Length; i++)
            {
                char value = text[i];
                _ptyInputBuffer[writeOffset] = value <= 0x7F ? (byte)value : (byte)'?';
                if (++writeOffset == _ptyInputBuffer.Length)
                    writeOffset = 0;
            }
            EnqueuePtyInputLength(text.Length, wasEmpty == 0);
        }
    }

    private void EnqueuePtyInputLength(int length, bool wasEmpty)
    {
        _ptyInputLengths[_ptyInputLengthWrite] = length;
        if (++_ptyInputLengthWrite == _ptyInputLengths.Length)
            _ptyInputLengthWrite = 0;
        _ptyInputLengthCount++;
        _ptyInputQueuedBytes += length;
        _ptyInputWriteOffset += length;
        if (_ptyInputWriteOffset >= _ptyInputBuffer.Length)
            _ptyInputWriteOffset %= _ptyInputBuffer.Length;
        if (wasEmpty)
            _ptyInputAvailable.Set();
    }

    private void EnsurePtyInputCapacity(int additionalBytes)
    {
        int required = checked(_ptyInputQueuedBytes + additionalBytes);
        if (required <= _ptyInputBuffer.Length)
            return;

        int capacity = _ptyInputBuffer.Length;
        while (capacity < required)
            capacity = capacity <= int.MaxValue / 2 ? capacity * 2 : required;

        var expanded = new byte[capacity];
        int firstLength = Math.Min(_ptyInputQueuedBytes, _ptyInputBuffer.Length - _ptyInputReadOffset);
        _ptyInputBuffer.AsSpan(_ptyInputReadOffset, firstLength).CopyTo(expanded);
        _ptyInputBuffer.AsSpan(0, _ptyInputQueuedBytes - firstLength)
            .CopyTo(expanded.AsSpan(firstLength));
        _ptyInputBuffer = expanded;
        _ptyInputReadOffset = 0;
        _ptyInputWriteOffset = _ptyInputQueuedBytes;
    }

    private void EnsurePtyInputLengthCapacity()
    {
        if (_ptyInputLengthCount < _ptyInputLengths.Length)
            return;

        var expanded = new int[checked(_ptyInputLengths.Length * 2)];
        int firstLength = Math.Min(_ptyInputLengthCount, _ptyInputLengths.Length - _ptyInputLengthRead);
        Array.Copy(_ptyInputLengths, _ptyInputLengthRead, expanded, 0, firstLength);
        Array.Copy(_ptyInputLengths, 0, expanded, firstLength, _ptyInputLengthCount - firstLength);
        _ptyInputLengths = expanded;
        _ptyInputLengthRead = 0;
        _ptyInputLengthWrite = _ptyInputLengthCount;
    }

    private void StartPtyInputWriter()
    {
        Thread writer;
        lock (_ptyInputQueueLock)
        {
            if (_ptyInputWriterThread != null)
                return;
            _ptyInputWriterStopping = false;
            writer = new Thread(PtyInputWriterLoop) { IsBackground = true };
            _ptyInputWriterThread = writer;
        }
        writer.Start();
    }

    private void PtyInputWriterLoop()
    {
        bool measureWrite = false;
        long allocatedBeforeWrite = 0;
        while (true)
        {
            if (!measureWrite)
            {
                measureWrite = Volatile.Read(ref _allocationProbeEnabled) != 0;
                if (measureWrite)
                    allocatedBeforeWrite = GC.GetAllocatedBytesForCurrentThread();
            }

            byte[] buffer;
            int readOffset;
            int length;
            lock (_ptyInputQueueLock)
            {
                if (_ptyInputWriterStopping)
                {
                    if (_disposed)
                        DisposeInputWaitEvent();
                    return;
                }
                if (_ptyInputLengthCount == 0)
                {
                    _ptyInputAvailable.Reset();
                    length = 0;
                    buffer = _ptyInputBuffer;
                    readOffset = 0;
                }
                else
                {
                    length = _ptyInputLengths[_ptyInputLengthRead];
                    buffer = _ptyInputBuffer;
                    readOffset = _ptyInputReadOffset;
                }
            }

            if (length == 0)
            {
                _ptyInputAvailable.Wait();
                continue;
            }

            try
            {
                var input = _pty?.InputStream;
                if (input == null)
                    throw new ObjectDisposedException(nameof(IPty.InputStream));
                int firstLength = Math.Min(length, buffer.Length - readOffset);
                input.Write(buffer.AsSpan(readOffset, firstLength));
                if (firstLength < length)
                    input.Write(buffer.AsSpan(0, length - firstLength));
                input.Flush();
            }
            catch
            {
                lock (_ptyInputQueueLock)
                {
                    _ptyInputWriterStopping = true;
                    _ptyInputLengthCount = 0;
                    _ptyInputQueuedBytes = 0;
                    _ptyInputLengthRead = _ptyInputLengthWrite;
                    _ptyInputReadOffset = _ptyInputWriteOffset;
                    _ptyInputAvailable.Reset();
                }
                if (_disposed)
                    DisposeInputWaitEvent();
                return;
            }

            lock (_ptyInputQueueLock)
            {
                _ptyInputQueuedBytes -= length;
                _ptyInputReadOffset += length;
                if (_ptyInputReadOffset >= _ptyInputBuffer.Length)
                    _ptyInputReadOffset %= _ptyInputBuffer.Length;
                if (++_ptyInputLengthRead == _ptyInputLengths.Length)
                    _ptyInputLengthRead = 0;
                _ptyInputLengthCount--;
                if (_ptyInputLengthCount == 0)
                    _ptyInputAvailable.Reset();
            }

            if (measureWrite)
            {
                Interlocked.Add(
                    ref _ptyInputWriterAllocatedBytes,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeWrite);
                Interlocked.Increment(ref _ptyInputWriterMeasuredWrites);
                measureWrite = false;
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        try
        {
            lock (Adapter.Buffer.SyncRoot)
            {
                Adapter.ResizeBuffer(rows, cols);
            }
        }
        catch { }

        if (!_hasReceivedInitialResize)
        {
            _hasReceivedInitialResize = true;
            if (cols == _initialCols && rows == _initialRows) return;
        }

        if (cols != _initialCols || rows != _initialRows)
        {
            try
            {
                _pty?.Resize(cols, rows);
                _initialCols = cols;
                _initialRows = rows;
            }
            catch { }
        }
    }

    private void OnPtyProcessExited(object? sender, int exitCode)
    {
        Task pipelineCompletion;
        lock (_lifecycleLock)
        {
            if (_disposed || _suppressProcessExit || Volatile.Read(ref _isStarted) == 0)
                return;
            if (Interlocked.Exchange(ref _processExitRaised, 1) != 0)
                return;
            pipelineCompletion = _ptyPipelineCompletion;
        }

        _ = DeliverProcessExitAfterPipelineAsync(pipelineCompletion, exitCode);
    }

    private async Task DeliverProcessExitAfterPipelineAsync(Task pipelineCompletion, int exitCode)
    {
        try { await pipelineCompletion.ConfigureAwait(false); } catch { }

        Action<int>? processExited;
        lock (_lifecycleLock)
        {
            if (_disposed || _suppressProcessExit)
                return;
            processExited = ProcessExited;
        }

        try { processExited?.Invoke(exitCode); } catch { }
    }

    private void StartPtyPipeline(CancellationToken cancellationToken)
    {
        var completion = _ptyPipelineCompletionSource;
        if (completion == null) return;
        // Prime each lazy wait path on the starting thread, not on the workers.
        _ptyInputAvailable.Wait(1);
        _ptyOutputAvailable.Wait(1);
        _ptyOutputSpaceAvailable.Reset();
        _ptyOutputSpaceAvailable.Wait(1);
        _ptyOutputSpaceAvailable.Set();

        StartPtyInputWriter();
        var reader = _pty?.OutputStream;
        if (reader == null)
        {
            completion.TrySetResult(null);
            return;
        }
        // Start with two 128 KiB chunks (256 KiB/session) and double only when
        // the reader meets backlog, up to the existing 32-chunk burst budget.
        // Reused chunks avoid cross-thread ArrayPool returns; wakeups occur only
        // at empty/full transitions so queued output is drained in batches.

        lock (_ptyOutputQueueLock)
        {
            _ptyOutputReadIndex = 0;
            _ptyOutputWriteIndex = 0;
            _ptyOutputCount = 0;
            _ptyOutputReaderCompleted = false;
            _ptyOutputAvailable.Reset();
            _ptyOutputSpaceAvailable.Set();
        }

        _ptyOutputReader = reader;
        _ptyOutputCancellationToken = cancellationToken;
        _ptyOutputConsumerThread = new Thread(ConsumePtyOutput) { IsBackground = true };
        _ptyOutputReaderThread = new Thread(ReadPtyOutput) { IsBackground = true };
        _ptyOutputConsumerThread.Start();
        _ptyOutputReaderThread.Start();
        _ptyPipelineCompletion = completion.Task;
    }

    private void ReadPtyOutput()
    {
        bool measureRead = false;
        long allocatedBeforeRead = 0;
        try
        {
            while (true)
            {
                if (_ptyOutputCancellationToken.IsCancellationRequested)
                {
                    DrainPtyOutputUntilClosed();
                    break;
                }

                if (!measureRead)
                {
                    measureRead = Volatile.Read(ref _allocationProbeEnabled) != 0;
                    if (measureRead)
                        allocatedBeforeRead = GC.GetAllocatedBytesForCurrentThread();
                }

                byte[]? chunk = null;
                lock (_ptyOutputQueueLock)
                {
                    if (_ptyOutputCount == _ptyOutputCapacity
                        && _ptyOutputCapacity < PtyOutputChunkCount)
                        GrowPtyOutputRing();
                    if (_ptyOutputCount < _ptyOutputCapacity)
                        chunk = _ptyOutputChunks[_ptyOutputWriteIndex];
                    else
                        _ptyOutputSpaceAvailable.Reset();
                }

                if (chunk == null)
                {
                    _ptyOutputSpaceAvailable.Wait();
                    continue;
                }

                int bytesRead = _ptyOutputReader!.Read(chunk, 0, chunk.Length);
                if (bytesRead <= 0)
                    break;
                if (_ptyOutputCancellationToken.IsCancellationRequested)
                {
                    DrainPtyOutputUntilClosed();
                    break;
                }

                lock (_ptyOutputQueueLock)
                {
                    bool wasEmpty = _ptyOutputCount == 0;
                    _ptyOutputLengths[_ptyOutputWriteIndex] = bytesRead;
                    if (++_ptyOutputWriteIndex == _ptyOutputCapacity)
                        _ptyOutputWriteIndex = 0;
                    _ptyOutputCount++;
                    Interlocked.Increment(ref _pendingOutputChunks);
                    if (wasEmpty)
                        _ptyOutputAvailable.Set();
                }

                if (measureRead)
                {
                    Interlocked.Add(
                        ref _ptyOutputReaderAllocatedBytes,
                        GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRead);
                    Interlocked.Increment(ref _ptyOutputReaderMeasuredChunks);
                    measureRead = false;
                }
            }
        }
        catch { }
        finally
        {
            // Shutdown may already have disposed the wait events if this thread
            // outlived its bounded join; never let that escape the thread.
            try
            {
                lock (_ptyOutputQueueLock)
                {
                    _ptyOutputReaderCompleted = true;
                    _ptyOutputAvailable.Set();
                    _ptyOutputSpaceAvailable.Set();
                }
            }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Reads and discards PTY output until the stream ends. ConPTY's
    /// ClosePseudoConsole can block until its final frame has been read, so the
    /// output pipe must keep draining after cancellation until it breaks.
    /// Runs only during shutdown, where allocation is allowed.
    /// </summary>
    private void DrainPtyOutputUntilClosed()
    {
        var discard = new byte[4096];
        try
        {
            while (_ptyOutputReader!.Read(discard, 0, discard.Length) > 0)
            {
            }
        }
        catch
        {
        }
    }

    private void ConsumePtyOutput()
    {
        // Bound each buffer-lock hold so renderer acquisition stays responsive.
        const int FeedChunkSize = 8192;
        bool measureChunk = false;
        long allocatedBeforeChunk = 0;
        try
        {
            while (!_ptyOutputCancellationToken.IsCancellationRequested)
            {
                if (!measureChunk)
                {
                    measureChunk = Volatile.Read(ref _allocationProbeEnabled) != 0;
                    if (measureChunk)
                        allocatedBeforeChunk = GC.GetAllocatedBytesForCurrentThread();
                }
                byte[]? chunk = null;
                int length = 0;
                bool finished = false;
                lock (_ptyOutputQueueLock)
                {
                    if (_ptyOutputCount != 0)
                    {
                        chunk = _ptyOutputChunks[_ptyOutputReadIndex];
                        length = _ptyOutputLengths[_ptyOutputReadIndex];
                    }
                    else if (_ptyOutputReaderCompleted)
                    {
                        finished = true;
                    }
                    else
                    {
                        _ptyOutputAvailable.Reset();
                    }
                }

                if (finished)
                    break;
                if (chunk == null)
                {
                    _ptyOutputAvailable.Wait();
                    continue;
                }
                try
                {
                    var rawInputReceived = RawInputReceived;
                    if (rawInputReceived != null)
                        rawInputReceived(chunk.AsSpan(0, length).ToArray());
                    var buffer = Adapter.Buffer;
                    int offset = 0;
                    while (offset < length)
                    {
                        int subLen = Math.Min(FeedChunkSize, length - offset);
                        bool taken = false;
                        try
                        {
                            Monitor.Enter(buffer.SyncRoot, ref taken);
                            Parser.Feed(chunk.AsSpan(offset, subLen));
                        }
                        finally
                        {
                            if (taken) Monitor.Exit(buffer.SyncRoot);
                        }
                        offset += subLen;

                        // A short handoff prevents this consumer from repeatedly
                        // barging past a waiting renderer.
                        if (offset < length && buffer.ReaderWaiting)
                        {
                            int handoffSpins = 0;
                            while (buffer.ReaderWaiting && handoffSpins++ < 64)
                                Thread.Yield();
                        }
                    }
                }
                catch { }
                finally
                {
                    bool wasFull;
                    lock (_ptyOutputQueueLock)
                    {
                        wasFull = _ptyOutputCount == _ptyOutputCapacity;
                        if (_ptyOutputCount != 0)
                        {
                            if (++_ptyOutputReadIndex == _ptyOutputCapacity)
                                _ptyOutputReadIndex = 0;
                            _ptyOutputCount--;
                        }
                        if (_ptyOutputCount == 0)
                            _ptyOutputAvailable.Reset();
                        if (wasFull)
                            _ptyOutputSpaceAvailable.Set();
                    }
                    Interlocked.Decrement(ref _pendingOutputChunks);
                }
                try { Adapter.FlushRender(); } catch { }
                if (measureChunk)
                {
                    Interlocked.Add(
                        ref _ptyOutputConsumerAllocatedBytes,
                        GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeChunk);
                    Interlocked.Increment(ref _ptyOutputConsumerMeasuredChunks);
                    measureChunk = false;
                }
            }
        }
        catch { }
        finally
        {
            try { _ptyOutputSpaceAvailable.Set(); } catch (ObjectDisposedException) { }
            bool readerExited = JoinThread(_ptyOutputReaderThread, WorkerShutdownTimeoutMs);
            int dropped;
            lock (_ptyOutputQueueLock)
            {
                dropped = _ptyOutputCount;
                _ptyOutputCount = 0;
                _ptyOutputReadIndex = _ptyOutputWriteIndex;
                _ptyOutputAvailable.Reset();
                _ptyOutputSpaceAvailable.Set();
            }
            if (dropped != 0)
                Interlocked.Add(ref _pendingOutputChunks, -dropped);
            _ptyPipelineCompletionSource?.TrySetResult(null);
            if (_disposed && readerExited)
                DisposeOutputWaitEvents();
        }
    }

    public void Dispose()
    {
        Thread currentThread = Thread.CurrentThread;
        bool onInputThread = currentThread == _ptyInputWriterThread;
        bool onOutputThread = currentThread == _ptyOutputReaderThread
            || currentThread == _ptyOutputConsumerThread;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _suppressProcessExit = true;
            Volatile.Write(ref _isStarted, 0);
            Volatile.Write(ref _processExitRaised, 1);
        }

        try { _readCancellation?.Cancel(); } catch { }
        lock (_ptyInputQueueLock)
        {
            _ptyInputWriterStopping = true;
            _ptyInputAvailable.Set();
        }
        _ptyOutputSpaceAvailable.Set();
        _ptyOutputAvailable.Set();
        try { Adapter.ReplyRequested -= OnAdapterReplyRequested; } catch { }
        try { if (_pty != null) _pty.ProcessExited -= OnPtyProcessExited; } catch { }
        try { _pty?.Dispose(); } catch { }
        if (!onInputThread)
            JoinThread(_ptyInputWriterThread);
        bool outputWorkersExited = true;
        if (!onOutputThread)
        {
            // The reader drains until _pty.Dispose() above closes the stream.
            bool readerExited = JoinThread(_ptyOutputReaderThread, WorkerShutdownTimeoutMs);
            bool consumerExited = JoinThread(_ptyOutputConsumerThread, WorkerShutdownTimeoutMs);
            outputWorkersExited = readerExited && consumerExited;
        }
        try { _readCancellation?.Dispose(); } catch { }
        TaskCompletionSource<object?>? completion = null;
        if (!onOutputThread)
        {
            lock (_lifecycleLock)
            {
                completion = _ptyPipelineCompletionSource;
                _ptyPipelineCompletionSource = null;
                _ptyPipelineCompletion = Task.CompletedTask;
            }
        }
        if (!onOutputThread)
            completion?.TrySetResult(null);

        _pty = null;
        _readCancellation = null;
        if (!onInputThread)
            DisposeInputWaitEvent();
        if (!onOutputThread && outputWorkersExited)
            DisposeOutputWaitEvents();
    }
}
