using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Dotty.Silk;

/// <summary>
/// Small loopback-only command transport used by deterministic desktop smoke tests.
/// </summary>
public sealed class DesktopControlServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, Task<string>> _commandHandler;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private int _disposed;

    public DesktopControlServer(int port, Func<string, Task<string>> commandHandler)
    {
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_acceptLoop != null)
            throw new InvalidOperationException("The desktop control server has already started.");

        _listener.Start();
        _acceptLoop = AcceptClientsAsync(_shutdown.Token);
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (var reader = new StreamReader(stream))
        using (var writer = new StreamWriter(stream) { AutoFlush = true })
        {
            string? command = await reader.ReadLineAsync(requestTimeout.Token).ConfigureAwait(false);
            if (command == null)
                return;
            if (command.Length > 1_048_576)
            {
                await writer.WriteLineAsync("ERROR command too long").ConfigureAwait(false);
                return;
            }

            try
            {
                string response = await _commandHandler(command)
                    .WaitAsync(TimeSpan.FromSeconds(10), requestTimeout.Token)
                    .ConfigureAwait(false);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestTimeout.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await writer.WriteLineAsync($"ERROR {exception.Message}").ConfigureAwait(false);
            }
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _listener.Stop();
        _shutdown.Dispose();
    }
}
