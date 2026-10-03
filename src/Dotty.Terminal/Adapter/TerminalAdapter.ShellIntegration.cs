using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

public enum ShellCommandState
{
    Idle,
    Running,
    Exited
}

public readonly record struct ShellCommandSnapshot(ShellCommandState State, int ExitCode);

public partial class TerminalAdapter
{
    private string? _currentWorkingDirectory;
    private long _shellCommandSnapshot;

    /// <summary>Local directory reported by shell integration, or null until OSC 7 is received.</summary>
    public string? CurrentWorkingDirectory => Volatile.Read(ref _currentWorkingDirectory);

    /// <summary>Returns a coherent latest-command snapshot without acquiring parser or buffer locks.</summary>
    public ShellCommandSnapshot GetShellCommandSnapshot()
    {
        long packed = Volatile.Read(ref _shellCommandSnapshot);
        return new ShellCommandSnapshot((ShellCommandState)(uint)(packed >> 32), unchecked((int)packed));
    }

    public event Action<string>? CurrentWorkingDirectoryChanged;
    public event Action<ShellCommandSnapshot>? ShellCommandStateChanged;

    /// <summary>Handles only shell integration OSCs; payload excludes the OSC introducer/code.</summary>
    public bool TryHandleShellIntegration(int command, ReadOnlySpan<char> payload)
    {
        if (command == 7)
            return TrySetCurrentWorkingDirectory(payload);
        if (command != 133 || payload.IsEmpty)
            return false;

        switch (payload[0])
        {
            case 'A':
                _buffer.AddPromptMark(PromptKind.Prompt);
                // Keep the prior command result visible until the next command actually starts.
                return true;
            case 'B':
                _buffer.AddPromptMark(PromptKind.Command);
                return true;
            case 'C':
                _buffer.AddPromptMark(PromptKind.Output);
                SetShellCommandState(ShellCommandState.Running, 0);
                return true;
            case 'D':
                _buffer.AddPromptMark(PromptKind.CommandEnd);
                SetShellCommandState(ShellCommandState.Exited, ParseExitCode(payload));
                return true;
            default:
                return false;
        }
    }

    private bool TrySetCurrentWorkingDirectory(ReadOnlySpan<char> payload)
    {
        if (!Uri.TryCreate(payload.ToString(), UriKind.Absolute, out var uri)
            || !uri.IsFile
            || !IsLocalHost(uri.Host))
            return false;

        string path;
        try { path = Uri.UnescapeDataString(uri.AbsolutePath); }
        catch (UriFormatException) { return false; }
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = path[1..];
        if (path.Length == 0) return false;

        string? previous = Volatile.Read(ref _currentWorkingDirectory);
        if (string.Equals(previous, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return true;
        Volatile.Write(ref _currentWorkingDirectory, path);
        CurrentWorkingDirectoryChanged?.Invoke(path);
        return true;
    }

    private static bool IsLocalHost(string host) =>
        string.IsNullOrEmpty(host)
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private static int ParseExitCode(ReadOnlySpan<char> payload)
    {
        int separator = payload.IndexOf(';');
        if (separator < 0) return 0;
        ReadOnlySpan<char> status = payload[(separator + 1)..];
        int next = status.IndexOf(';');
        if (next >= 0) status = status[..next];
        return int.TryParse(status, out int value) ? value : 0;
    }

    private void SetShellCommandState(ShellCommandState state, int exitCode)
    {
        long packed = ((long)(uint)state << 32) | (uint)exitCode;
        long previous = Interlocked.Exchange(ref _shellCommandSnapshot, packed);
        if (previous != packed)
            ShellCommandStateChanged?.Invoke(new ShellCommandSnapshot(state, exitCode));
    }
}
