namespace Dotty.Core;

public interface IPseudoTerminal : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }

    void Resize(int cols, int rows);
}

public interface IPseudoTerminalFactory
{
    IPseudoTerminal Create(string command, string workingDirectory, int cols, int rows);
}

