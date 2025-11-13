namespace Dotty.Core;

public sealed class UnixPtyFactory : IPseudoTerminalFactory
{
    public IPseudoTerminal Create(string command, string workingDirectory, int cols, int rows)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        return UnixPty.Start(shell, workingDirectory, cols, rows, command);
    }
}

