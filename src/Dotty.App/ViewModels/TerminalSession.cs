using System;
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
    public int TargetFps { get; set; } = 144;

    public TerminalSession(int rows = 24, int columns = 80)
    {
        Parser = new BasicAnsiParser();
        Adapter = new TerminalAdapter(rows: rows, columns: columns);
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
        var channel = Channel.CreateBounded<(byte[] Data, int Length)>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Reader: reads from PTY, writes to channel (never blocks on processing)
        _ = Task.Run(async () =>
        {
            byte[] buffer = new byte[131072];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead;
                    try { bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken); }
                    catch { break; }
                    if (bytesRead <= 0) break;

                    byte[] chunk = ArrayPool<byte>.Shared.Rent(bytesRead);
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                    await channel.Writer.WriteAsync((chunk, bytesRead), cancellationToken);
                }
            }
            catch { }
            channel.Writer.Complete();
        }, cancellationToken);

        // Consumer: reads from channel, processes sequentially.
        // SyncRoot is acquired so the renderer never reads a partially-updated buffer state.
        // FlushRender is called immediately after each chunk so the render timer
        // never catches an intermediate state between related operations (e.g. scroll+write).
        _ = Task.Run(async () =>
        {
            int chunkCount = 0;
            try
            {
                await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    var (chunk, length) = entry;
                    try
                    {
                        lock (Adapter.Buffer.SyncRoot)
                        {
                            Parser.Feed(chunk.AsSpan(0, length));
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

        // Dedicated render timer: polls at 60 FPS, decoupled from data processing.
        // Flushes pending renders at a fixed cadence regardless of chunk arrival rate.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(16, cancellationToken);
                    Adapter.FlushRender();
                }
            }
            catch { }
            try { Adapter.FlushRender(); } catch { }
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
