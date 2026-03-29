using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Buffers;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.App.ViewModels;

public class TerminalSession : IDisposable
{
    private struct PtyChunk
    {
        public byte[] Data;
        public int Length;
    }

    private readonly System.Threading.Channels.Channel<PtyChunk> _ptyDataQueue = System.Threading.Channels.Channel.CreateBounded<PtyChunk>(new System.Threading.Channels.BoundedChannelOptions(50) { SingleReader = true, SingleWriter = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });

    private Process? _childProcess;
    private Stream? _childOutputStream;
    private Stream? _childErrorStream;
    private Stream? _childInputStream;
    private CancellationTokenSource? _readCancellation;
    private string? _controlSocketPath;
    private Stream? _controlSocketStream;
    private readonly bool _throughputMode;

    public ITerminalParser Parser { get; }
    public TerminalAdapter Adapter { get; }

    public event Action<byte[]>? RawInputReceived;
    public event Action? RenderScheduled;
    public int TargetFps { get; set; } = 144;

    public TerminalSession(int rows = 24, int columns = 80)
    {
        _throughputMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTTY_BENCH_THROUGHPUT"));
        Parser = new BasicAnsiParser();
        Adapter = new TerminalAdapter(rows: rows, columns: columns);
        Parser.Handler = Adapter;
        Adapter.RenderRequested += _ => RenderScheduled?.Invoke();
    }

    public void Start()
    {
        string projectPath = FindPtyTestsProjectPath();
        string? helperExe = null;
        var candidate = Path.Combine(projectPath, "Dotty.NativePty", "bin", "pty-helper");
        if (File.Exists(candidate)) helperExe = Path.GetFullPath(candidate);

        if (string.IsNullOrEmpty(helperExe) || !File.Exists(helperExe))
        {
            throw new Exception("Failed to find helper subprocess executable.");
        }

        var helperShell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
        var psi = new ProcessStartInfo
        {
            FileName = helperExe,
            Arguments = $"\"{helperShell}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var controlPath = Path.Combine(Path.GetTempPath(), $"dotty-control-{Guid.NewGuid():N}.sock");
        psi.Environment["DOTTY_CONTROL_SOCKET"] = controlPath;
        _controlSocketPath = controlPath;

        _childProcess = Process.Start(psi);
        if (_childProcess == null)
        {
            throw new Exception("Failed to start helper subprocess.");
        }

        _childOutputStream = _childProcess.StandardOutput.BaseStream;
        _childErrorStream = _childProcess.StandardError.BaseStream;
        _childInputStream = _childProcess.StandardInput.BaseStream;

        if (!string.IsNullOrEmpty(_controlSocketPath))
        {
            _ = Task.Run(() => ConnectToControlSocketAsync(_controlSocketPath));
        }

        _readCancellation = new CancellationTokenSource();
        StartBackgroundReaders(_readCancellation.Token);
        ProcessPtyQueueAsync(_readCancellation.Token);
    }

    public void WriteInput(byte[] data)
    {
        if (data == null || _childInputStream == null) return;
        
        // Write directly without copying - data is not reused by caller
        Task.Run(async () =>
        {
            try
            {
                await _childInputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await _childInputStream.FlushAsync().ConfigureAwait(false);
            }
            catch { }
        });
    }

    public void Resize(int cols, int rows)
    {
        try { Adapter.ResizeBuffer(rows, cols); } catch { }
        _ = SendResizeMessageAsync(cols, rows);
    }

    private async Task SendResizeMessageAsync(int cols, int rows)
    {
        if (_controlSocketStream == null) return;
        try
        {
            var msg = $"{{\"type\":\"resize\",\"cols\":{cols},\"rows\":{rows}}}\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _controlSocketStream.WriteAsync(bytes, 0, bytes.Length);
            await _controlSocketStream.FlushAsync();
        }
        catch { }
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

    private async Task ReadChildOutputAsync(CancellationToken cancellationToken)
    {
        if (_childOutputStream == null) return;

        byte[]? batch = null;
        int batchLength = 0;

        try
        {
            var reader = _childOutputStream;
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

    private async Task ReadChildErrorAsync(CancellationToken cancellationToken)
    {
        if (_childErrorStream == null) return;

        try
        {
            var reader = _childErrorStream;
            byte[] buffer = new byte[131072];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead <= 0) break;
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }
        catch { }
    }

    private void StartBackgroundReaders(CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(() => ReadChildOutputAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        Task.Factory.StartNew(() => ReadChildErrorAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task ConnectToControlSocketAsync(string path)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            Socket? sock = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var end = new UnixDomainSocketEndPoint(path);
                    await Task.Run(() => sock.Connect(end));
                    break;
                }
                catch
                {
                    try { sock?.Dispose(); } catch { }
                    await Task.Delay(100);
                }
            }

            if (sock == null || !sock.Connected)
            {
                try { sock?.Dispose(); } catch { }
                return;
            }

            _controlSocketStream = new NetworkStream(sock, ownsSocket: true);
            await SendResizeMessageAsync(Adapter.Buffer?.Columns ?? 80, Adapter.Buffer?.Rows ?? 24);
        }
        catch { }
    }

    private string FindPtyTestsProjectPath()
    {
        try
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory ?? ".");
            for (int i = 0; i < 8 && cur != null; i++)
            {
                string candidate1 = Path.Combine(cur.FullName, "src", "Dotty.NativePty");
                string candidate2 = Path.Combine(cur.FullName, "Dotty.NativePty");

                if (Directory.Exists(candidate1)) return Path.GetFullPath(Path.Combine(cur.FullName, "src"));
                if (Directory.Exists(candidate2)) return Path.GetFullPath(cur.FullName);

                cur = cur.Parent;
            }
        }
        catch { }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory ?? ".", "..", "..", "..", "..", "src"));
    }

    public void Dispose()
    {
        _readCancellation?.Cancel();

        try
        {
            if (_childInputStream != null) { try { _childInputStream.Close(); } catch { } }

            if (_childProcess != null)
            {
                if (!_childProcess.HasExited) { try { _childProcess.Kill(); } catch { } }
                try { _childProcess.Dispose(); } catch { }
            }
        }
        catch { }

        try { _controlSocketStream?.Dispose(); } catch { }
        _readCancellation?.Dispose();
    }
}
