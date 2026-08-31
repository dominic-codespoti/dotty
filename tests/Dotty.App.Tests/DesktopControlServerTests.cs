using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DesktopControlServerTests
{
    [Fact]
    public async Task Server_UsesLoopbackAndReturnsCommandResponse()
    {
        using var server = new DesktopControlServer(0, command => Task.FromResult(command == "PING" ? "PONG" : "ERROR"));
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.Port, TestContext.Current.CancellationToken);
        await using NetworkStream stream = client.GetStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var reader = new StreamReader(stream);

        await writer.WriteLineAsync("PING");

        Assert.Equal("PONG", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }
}
