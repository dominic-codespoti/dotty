using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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
    private readonly Func<IPty> _ptyFactory;
    private readonly bool _checkPtySupport;
    private IPty? _pty;
    private CancellationTokenSource? _readCancellation;
    private readonly SemaphoreSlim _ptyInputWriteLock = new(1, 1);
    private readonly object _ptyInputQueueLock = new();
    private Task _ptyInputTail = Task.CompletedTask;
    private bool _disposed;
    private bool _hasReceivedInitialResize = false;
    private int _initialCols = 0;
    private int _initialRows = 0;
    private bool _isStarted = false;

    public ITerminalParser Parser { get; }
    public TerminalAdapter Adapter { get; }
    public bool IsStarted => _isStarted;

    public event Action<byte[]>? RawInputReceived;
    public event Action<string>? ClipboardWriteRequested;
    public event Action<string>? TitleChanged;
    public event Action? RenderScheduled;

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
        if (_isStarted) return;
        _isStarted = true;

        try
        {
            if (_checkPtySupport && !PtyFactory.IsSupported)
                throw new PtyException(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");

            _pty = _ptyFactory();
            _pty.ProcessExited += OnPtyProcessExited;

            var shell = Environment.GetEnvironmentVariable("DOTTY_SHELL")
                        ?? Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(shell)) shell = null;

            var startCols = Adapter.Buffer?.Columns ?? 80;
            var startRows = Adapter.Buffer?.Rows ?? 24;
            _initialCols = startCols;
            _initialRows = startRows;
            _hasReceivedInitialResize = false;

            _pty.Start(shell: shell, columns: startCols, rows: startRows);

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
        if (_isStarted) return;
        _isStarted = true;

        try
        {
            if (_checkPtySupport && !PtyFactory.IsSupported)
                throw new PtyException(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");

            _pty = _ptyFactory();
            _pty.ProcessExited += OnPtyProcessExited;
            shell ??= Environment.GetEnvironmentVariable("DOTTY_SHELL")
                      ?? Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrWhiteSpace(shell)) shell = null;

            var startCols = Adapter.Buffer?.Columns ?? 80;
            var startRows = Adapter.Buffer?.Rows ?? 24;
            _initialCols = startCols;
            _initialRows = startRows;
            _hasReceivedInitialResize = false;

            _pty.Start(shell: shell, columns: startCols, rows: startRows,
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

    private void ResetFailedStart()
    {
        _isStarted = false;
        try { _readCancellation?.Cancel(); } catch { }
        try { if (_pty != null) _pty.ProcessExited -= OnPtyProcessExited; } catch { }
        try { _pty?.Dispose(); } catch { }
        try { _readCancellation?.Dispose(); } catch { }
        _pty = null;
        _readCancellation = null;
    }

    public void WriteInput(byte[] data)
    {
        if (_disposed || data == null || _pty?.InputStream == null) return;
        QueuePtyInputWrite(data);
    }

    public void SendFocusReport(bool focused)
    {
        if (_disposed || !Adapter.FocusReportingEnabled || _pty?.InputStream == null)
            return;

        QueuePtyInputWrite(Encoding.ASCII.GetBytes(focused ? "\x1b[I" : "\x1b[O"));
    }

    private void OnAdapterReplyRequested(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return;
        QueuePtyInputWrite(Encoding.ASCII.GetBytes(reply));
    }

    private void QueuePtyInputWrite(byte[] data)
    {
        if (data.Length == 0) return;

        lock (_ptyInputQueueLock)
        {
            var previous = _ptyInputTail;
            _ptyInputTail = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                await WritePtyInputAsync(data).ConfigureAwait(false);
            });
        }
    }

    private async Task WritePtyInputAsync(byte[] data)
    {
        var input = _pty?.InputStream;
        if (input == null) return;
        try
        {
            await _ptyInputWriteLock.WaitAsync().ConfigureAwait(false);
            await input.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { _ptyInputWriteLock.Release(); } catch { }
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

    private void OnPtyProcessExited(object? sender, int exitCode) { }

    private void StartPtyPipeline(CancellationToken cancellationToken)
    {
        if (_pty?.OutputStream == null) return;

        var reader = _pty.OutputStream;
        // Depth 32 (4 MB): decouples kernel PTY delivery from parse. At depth 4
        // the producer stalls whenever parse falls behind, serializing the
        // stages — measured 118ms/500K-line flood of non-overlapped parse.
        var channel = Channel.CreateBounded<(byte[] Data, int Length)>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Reader: reads from PTY, writes to channel (never blocks on processing).
        // Uses ArrayPool directly to avoid the intermediate read-buffer copy.
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] chunk = ArrayPool<byte>.Shared.Rent(131072);
                    bool handedOff = false;
                    try
                    {
                        int bytesRead = await reader.ReadAsync(chunk, 0, chunk.Length, cancellationToken);
                        if (bytesRead <= 0)
                        {
                            ArrayPool<byte>.Shared.Return(chunk);
                            break;
                        }

                        await channel.Writer.WriteAsync((chunk, bytesRead), cancellationToken);
                        handedOff = true;
                    }
                    catch
                    {
                        if (!handedOff) ArrayPool<byte>.Shared.Return(chunk);
                        break;
                    }
                }
            }
            catch { }
            channel.Writer.Complete();
        }, cancellationToken);

        // Consumer: reads from channel, processes sequentially.
        // SyncRoot is acquired so the renderer never reads a partially-updated buffer state.
        // FlushRender is called after each chunk so the view's presentation gate
        // never presents an intermediate state between related operations (e.g. scroll+write).
        _ = Task.Run(async () =>
        {
            // Writer lock-hold bound: feed the chunk in sub-chunks, releasing
            // SyncRoot between them so the renderer's bounded TryEnter can win
            // the lock during a sustained burst. The parser tolerates partial
            // sequences (it accumulates leftovers), so splits are safe. When
            // the renderer has signaled ReaderWaiting, yield between sub-chunks
            // to hand it a scheduling window instead of re-acquiring instantly.
            const int SubChunkSize = 8192;
            try
            {
                await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    var (chunk, length) = entry;
                    try
                    {
                        var rawInputReceived = RawInputReceived;
                        if (rawInputReceived != null)
                            rawInputReceived(chunk.AsSpan(0, length).ToArray());
                        var buffer = Adapter.Buffer;
                        int offset = 0;
                        while (offset < length)
                        {
                            int subLen = Math.Min(SubChunkSize, length - offset);
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
                            if (offset < length && buffer.ReaderWaiting)
                            {
                                // Reader-priority handoff. A single Yield lets
                                // this thread barge straight back into the
                                // Monitor (pthread mutexes are not FIFO and the
                                // renderer is parked in a bounded TryEnter) —
                                // with the deep channel keeping a chunk always
                                // queued, that barging can starve the renderer.
                                // Hold off until the renderer clears the flag
                                // or the bounded spin elapses.
                                int handoffSpins = 0;
                                while (buffer.ReaderWaiting && handoffSpins++ < 64)
                                    Thread.Yield();
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk);
                    }
                    try { Adapter.FlushRender(); } catch { }
                }
            }
            catch { }
            finally
            {
                try { await readerTask.ConfigureAwait(false); } catch { }
                while (channel.Reader.TryRead(out var pending))
                    ArrayPool<byte>.Shared.Return(pending.Data);
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readCancellation?.Cancel();
        try { Adapter.ReplyRequested -= OnAdapterReplyRequested; } catch { }
        try { if (_pty != null) _pty.ProcessExited -= OnPtyProcessExited; } catch { }
        try { _pty?.Dispose(); } catch { }
        try { _readCancellation?.Dispose(); } catch { }
        try { _ptyInputWriteLock.Dispose(); } catch { }
    }
}
