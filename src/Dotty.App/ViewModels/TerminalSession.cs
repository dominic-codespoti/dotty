using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Buffers;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;
using Dotty.Abstractions.Pty;
using Dotty.NativePty;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.App.ViewModels;

/// <summary>
/// Manages a terminal session including PTY lifecycle, I/O handling, and terminal state.
/// </summary>
public class TerminalSession : IDisposable
{
    private struct PtyChunk
    {
        public byte[] Data;
        public int Length;
    }

    private readonly System.Threading.Channels.Channel<PtyChunk> _ptyDataQueue = System.Threading.Channels.Channel.CreateBounded<PtyChunk>(new System.Threading.Channels.BoundedChannelOptions(50) { SingleReader = true, SingleWriter = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });

    private IPty? _pty;
    private CancellationTokenSource? _readCancellation;
    private readonly bool _throughputMode;
    private readonly SemaphoreSlim _ptyInputWriteLock = new(1, 1);
    private bool _hasReceivedInitialResize = false;
    private int _initialCols = 0;
    private int _initialRows = 0;
    private bool _isStarted = false;

    /// <summary>
    /// Gets the terminal parser for processing ANSI escape sequences.
    /// </summary>
    public ITerminalParser Parser { get; }
    
    /// <summary>
    /// Gets the terminal adapter for managing terminal buffer state.
    /// </summary>
    public TerminalAdapter Adapter { get; }
    
    /// <summary>
    /// Gets a value indicating whether the session has been started.
    /// </summary>
    public bool IsStarted => _isStarted;

    /// <summary>
    /// Event raised when raw input data is received from the PTY.
    /// </summary>
    public event Action<byte[]>? RawInputReceived;
    
    /// <summary>
    /// Event raised when a render should be scheduled.
    /// </summary>
    public event Action? RenderScheduled;
    
    /// <summary>
    /// Gets or sets the target frames per second for rendering.
    /// </summary>
    public int TargetFps { get; set; } = 144;

    /// <summary>
    /// Creates a new terminal session with the specified initial size.
    /// </summary>
    /// <param name="rows">Initial number of rows.</param>
    /// <param name="columns">Initial number of columns.</param>
    public TerminalSession(int rows = 24, int columns = 80)
    {
        _throughputMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTTY_BENCH_THROUGHPUT"));
        Parser = new BasicAnsiParser();
        Adapter = new TerminalAdapter(rows: rows, columns: columns);
        Parser.Handler = Adapter;
        Adapter.RenderRequested += _ => RenderScheduled?.Invoke();
        Adapter.ReplyRequested += OnAdapterReplyRequested;
    }

    /// <summary>
    /// Starts the terminal session by creating and starting a PTY with the default shell.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the session is already started.</exception>
    /// <exception cref="PtyException">Thrown if PTY creation fails.</exception>
    public void Start()
    {
        // Prevent double-starting the session
        if (_isStarted) return;
        _isStarted = true;

        // Check if PTY is supported
        if (!PtyFactory.IsSupported)
        {
            var reason = PtyFactory.GetUnsupportedReason();
            throw new PtyException(reason ?? "PTY is not supported on this platform.");
        }

        // Create and start the PTY
        _pty = PtyFactory.Create();
        _pty.ProcessExited += OnPtyProcessExited;
        
        var shell = Environment.GetEnvironmentVariable("DOTTY_SHELL");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = Environment.GetEnvironmentVariable("SHELL");
        }
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = null;
        }
        
        var startCols = Adapter.Buffer?.Columns ?? 80;
        var startRows = Adapter.Buffer?.Rows ?? 24;
        _initialCols = startCols;
        _initialRows = startRows;
        _hasReceivedInitialResize = false;

        _pty.Start(
            shell: shell,
            columns: startCols,
            rows: startRows);

        // Start background readers
        _readCancellation = new CancellationTokenSource();
        StartBackgroundReaders(_readCancellation.Token);
        ProcessPtyQueueAsync(_readCancellation.Token);
    }

    /// <summary>
    /// Starts the terminal session with a specific shell and options.
    /// </summary>
    /// <param name="shell">The shell to start.</param>
    /// <param name="workingDirectory">The working directory for the shell.</param>
    /// <param name="environmentVariables">Additional environment variables.</param>
    public void StartWithOptions(
        string? shell = null,
        string? workingDirectory = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null)
    {
        if (_isStarted) return;
        _isStarted = true;

        if (!PtyFactory.IsSupported)
        {
            var reason = PtyFactory.GetUnsupportedReason();
            throw new PtyException(reason ?? "PTY is not supported on this platform.");
        }

        _pty = PtyFactory.Create();
        _pty.ProcessExited += OnPtyProcessExited;
        
        var startCols = Adapter.Buffer?.Columns ?? 80;
        var startRows = Adapter.Buffer?.Rows ?? 24;
        _initialCols = startCols;
        _initialRows = startRows;
        _hasReceivedInitialResize = false;

        _pty.Start(
            shell: shell,
            columns: startCols,
            rows: startRows,
            workingDirectory: workingDirectory,
            environmentVariables: environmentVariables);

        _readCancellation = new CancellationTokenSource();
        StartBackgroundReaders(_readCancellation.Token);
        ProcessPtyQueueAsync(_readCancellation.Token);
    }

    /// <summary>
    /// Writes input data to the PTY.
    /// </summary>
    /// <param name="data">The data to write.</param>
    public void WriteInput(byte[] data)
    {
        if (data == null || _pty?.InputStream == null) return;

        QueuePtyInputWrite(data);
    }

    private void OnAdapterReplyRequested(string reply)
    {
        if (string.IsNullOrEmpty(reply))
        {
            return;
        }

        QueuePtyInputWrite(Encoding.ASCII.GetBytes(reply));
    }

    private void QueuePtyInputWrite(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        Task.Run(async () =>
        {
            await WritePtyInputAsync(data).ConfigureAwait(false);
        });
    }

    private async Task WritePtyInputAsync(byte[] data)
    {
        var input = _pty?.InputStream;
        if (input == null)
        {
            return;
        }

        try
        {
            await _ptyInputWriteLock.WaitAsync().ConfigureAwait(false);
            await input.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try { _ptyInputWriteLock.Release(); } catch { }
        }
    }

    /// <summary>
    /// Resizes the terminal to the specified dimensions.
    /// </summary>
    /// <param name="cols">New width in columns.</param>
    /// <param name="rows">New height in rows.</param>
    public void Resize(int cols, int rows)
    {
        try { Adapter.ResizeBuffer(rows, cols); } catch { }

        // On first UI-driven resize, send dimensions if they differ from the
        // PTY startup size. This keeps shell cursor math aligned with the actual
        // viewport when the window opens small/squashed.
        if (!_hasReceivedInitialResize)
        {
            _hasReceivedInitialResize = true;

            if (cols == _initialCols && rows == _initialRows)
            {
                return;
            }
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
        // PTY process has exited - could trigger UI notification here
    }

    private void ProcessPtyQueueAsync(CancellationToken token)
    {
        Task.Run(async () =>
        {
            byte[]? batchBuffer = null;
            try
            {
                batchBuffer = ArrayPool<byte>.Shared.Rent(65536);
                int batchLength = 0;
                int cyclesSinceRender = 0;

                void FlushBatch()
                {
                    if (batchLength <= 0) return;
                    try
                    {
                        Parser.Feed(batchBuffer.AsSpan(0, batchLength));
                    }
                    catch { }
                    batchLength = 0;
                }

                while (await _ptyDataQueue.Reader.WaitToReadAsync(token))
                {
                    int drained = 0;
                    while (_ptyDataQueue.Reader.TryRead(out var chunk))
                    {
                        if (chunk.Length >= batchBuffer.Length)
                        {
                            FlushBatch();
                            try
                            {
                                Parser.Feed(chunk.Data.AsSpan(0, chunk.Length));
                            }
                            catch { }
                        }
                        else
                        {
                            if (batchLength + chunk.Length > batchBuffer.Length)
                            {
                                FlushBatch();
                            }

                            chunk.Data.AsSpan(0, chunk.Length).CopyTo(batchBuffer.AsSpan(batchLength));
                            batchLength += chunk.Length;
                        }

                        System.Buffers.ArrayPool<byte>.Shared.Return(chunk.Data);
                        drained++;
                    }

                    FlushBatch();

                    // In throughput benchmark mode, flush render less frequently.
                    cyclesSinceRender++;
                    if (!_throughputMode || drained >= 8 || cyclesSinceRender >= 64)
                    {
                        Adapter.FlushRender();
                        cyclesSinceRender = 0;
                    }
                }
            }
            catch { }
            finally
            {
                if (batchBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(batchBuffer);
                }
            }
        }, token);
    }

    private async Task ReadPtyOutputAsync(CancellationToken cancellationToken)
    {
        if (_pty?.OutputStream == null) return;

        byte[]? batch = null;
        int batchLength = 0;

        try
        {
            var reader = _pty.OutputStream;
            byte[] buffer = new byte[131072];
            batch = ArrayPool<byte>.Shared.Rent(65536);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead > 0)
                    {
                        if (RawInputReceived != null)
                        {
                            RawInputReceived.Invoke(buffer.AsSpan(0, bytesRead).ToArray());
                        }

                        bool smallInteractiveBurst = !_throughputMode && bytesRead < buffer.Length && batchLength < 1024;

                        if (bytesRead >= batch.Length)
                        {
                            if (batchLength > 0)
                            {
                                await _ptyDataQueue.Writer.WriteAsync(new PtyChunk { Data = batch, Length = batchLength }, cancellationToken);
                                batch = ArrayPool<byte>.Shared.Rent(65536);
                                batchLength = 0;
                            }

                            var directCopy = ArrayPool<byte>.Shared.Rent(bytesRead);
                            Array.Copy(buffer, 0, directCopy, 0, bytesRead);
                            await _ptyDataQueue.Writer.WriteAsync(new PtyChunk { Data = directCopy, Length = bytesRead }, cancellationToken);
                        }
                        else
                        {
                            if (batchLength + bytesRead > batch.Length)
                            {
                                await _ptyDataQueue.Writer.WriteAsync(new PtyChunk { Data = batch, Length = batchLength }, cancellationToken);
                                batch = ArrayPool<byte>.Shared.Rent(65536);
                                batchLength = 0;
                            }

                            buffer.AsSpan(0, bytesRead).CopyTo(batch.AsSpan(batchLength));
                            batchLength += bytesRead;

                            if (batchLength >= 32768 || smallInteractiveBurst)
                            {
                                await _ptyDataQueue.Writer.WriteAsync(new PtyChunk { Data = batch, Length = batchLength }, cancellationToken);
                                batch = ArrayPool<byte>.Shared.Rent(65536);
                                batchLength = 0;
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }
        catch { }
        finally
        {
            if (batch != null)
            {
                if (batchLength > 0)
                {
                    try
                    {
                        await _ptyDataQueue.Writer.WriteAsync(new PtyChunk { Data = batch, Length = batchLength }, cancellationToken);
                    }
                    catch { }
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(batch);
                }
            }
        }
    }

    private void StartBackgroundReaders(CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(() => ReadPtyOutputAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Releases all resources used by the terminal session.
    /// </summary>
    public void Dispose()
    {
        _readCancellation?.Cancel();

        try
        {
            Adapter.ReplyRequested -= OnAdapterReplyRequested;
        }
        catch { }

        try
        {
            if (_pty != null)
            {
                _pty.ProcessExited -= OnPtyProcessExited;
            }
        }
        catch { }

        try
        {
            _pty?.Dispose();
        }
        catch { }

        try
        {
            _readCancellation?.Dispose();
        }
        catch { }

        try
        {
            _ptyInputWriteLock.Dispose();
        }
        catch { }
    }
}
