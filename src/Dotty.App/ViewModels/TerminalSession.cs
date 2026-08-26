using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Buffers;
using System.Threading.Channels;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;
using Dotty.Abstractions.Pty;
using Dotty.NativePty;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Dotty.App.Services;

namespace Dotty.App.ViewModels;

public class TerminalSession : IDisposable
{
    private IPty? _pty;
    private CancellationTokenSource? _readCancellation;
    private readonly SemaphoreSlim _ptyInputWriteLock = new(1, 1);
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
    {
        Parser = new BasicAnsiParser();
        // The arena is allocated at construction; honor the configured scrollback
        // depth here so the default (5k) actually applies instead of the
        // TerminalBuffer fallback (10k). A config change at runtime is applied
        // via Adapter.ResizeBuffer, not by reallocating this arena.
        var scrollbackLines = RuntimeSettings.GetScrollbackLines();
        Adapter = new TerminalAdapter(rows: rows, columns: columns, scrollbackCapacity: scrollbackLines);
        Parser.Handler = Adapter;
        Adapter.RenderRequested += _ => RenderScheduled?.Invoke();
        Adapter.ReplyRequested += OnAdapterReplyRequested;
        Adapter.ClipboardWriteRequested += text => ClipboardWriteRequested?.Invoke(text);
        Adapter.TitleChanged += title => TitleChanged?.Invoke(title);
        if (TerminalTrace.Enabled)
            Adapter.Trace = (reason, buf) => TerminalTrace.Snapshot(buf, reason);
    }

    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;

        Program.BenchTimer?.Stage("session_start");

        if (!PtyFactory.IsSupported)
            throw new PtyException(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");

        _pty = PtyFactory.Create();
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

    public void StartWithOptions(
        string? shell = null,
        string? workingDirectory = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null)
    {
        if (_isStarted) return;
        _isStarted = true;

        if (!PtyFactory.IsSupported)
            throw new PtyException(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");

        _pty = PtyFactory.Create();
        _pty.ProcessExited += OnPtyProcessExited;

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

    public void WriteInput(byte[] data)
    {
        if (data == null || _pty?.InputStream == null) return;
        QueuePtyInputWrite(data);
    }

    private void OnAdapterReplyRequested(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return;
        QueuePtyInputWrite(Encoding.ASCII.GetBytes(reply));
    }

    private void QueuePtyInputWrite(byte[] data)
    {
        if (data.Length == 0) return;
        Task.Run(async () => await WritePtyInputAsync(data).ConfigureAwait(false));
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
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] chunk = ArrayPool<byte>.Shared.Rent(131072);
                    int bytesRead;
                    try { bytesRead = await reader.ReadAsync(chunk, 0, chunk.Length, cancellationToken); }
                    catch { ArrayPool<byte>.Shared.Return(chunk); break; }
                    if (bytesRead <= 0) { ArrayPool<byte>.Shared.Return(chunk); break; }

                    await channel.Writer.WriteAsync((chunk, bytesRead), cancellationToken);
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
            int chunkCount = 0;
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
                    var rawInputReceived = RawInputReceived;
                    if (rawInputReceived != null)
                        rawInputReceived(chunk.AsSpan(0, length).ToArray());
                    try
                    {
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
                                Thread.Yield();
                        }
                    }
                    catch { }
                    ArrayPool<byte>.Shared.Return(chunk);
                    try { Adapter.FlushRender(); } catch { }
                    if (TerminalTrace.Enabled && ++chunkCount % 10 == 0)
                        TerminalTrace.Snapshot(Adapter.Buffer, "periodic");
                }
            }
            catch { }
        }, cancellationToken);
    }

    public void Dispose()
    {
        _readCancellation?.Cancel();
        try { Adapter.ReplyRequested -= OnAdapterReplyRequested; } catch { }
        try { if (_pty != null) _pty.ProcessExited -= OnPtyProcessExited; } catch { }
        try { _pty?.Dispose(); } catch { }
        try { _readCancellation?.Dispose(); } catch { }
        try { _ptyInputWriteLock.Dispose(); } catch { }
    }
}
