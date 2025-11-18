using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dotty.Core;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed partial class UnixPty : IPseudoTerminal, IDisposable
{
    public Stream Input  => (_stream ??= new UnixPtyStream(_masterFd));
    public Stream Output => (_stream ??= new UnixPtyStream(_masterFd));

    private readonly int _masterFd;
    private readonly int _childPid;
    private UnixPtyStream? _stream;
    private bool _disposed;

    private UnixPty(int masterFd, int childPid)
    {
        _masterFd = masterFd;
        _childPid = childPid;
        _stream = null;  // Delay initialization
        _disposed = false;
    }

    // Return the slave device name (e.g. /dev/pts/N) if available for diagnostics
    public string? SlaveName()
    {
        try
        {
            var p = UnixPtyNativePtsName(_masterFd);
            if (p == IntPtr.Zero) return null;
            return Marshal.PtrToStringAnsi(p);
        }
        catch
        {
            return null;
        }
    }

    // Wrapper around native ptsname (kept decoupled for easier testing)
    private static IntPtr UnixPtyNativePtsName(int fd) => ptsname(fd);

    public static UnixPty Start(string shell, string workingDirectory, int cols, int rows, string? command, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var ws = new WinSize
        {
            Rows = (ushort)rows,
            Cols = (ushort)cols,
            XPixel = 0,
            YPixel = 0,
        };

        var controlCharacters = new Dictionary<TermSpecialControlCharacter, sbyte>
        {
            { TermSpecialControlCharacter.VEOF, 4 },
            { TermSpecialControlCharacter.VEOL, -1 },
            { TermSpecialControlCharacter.VEOL2, -1 },
            { TermSpecialControlCharacter.VERASE, 0x7f },
            { TermSpecialControlCharacter.VWERASE, 23 },
            { TermSpecialControlCharacter.VKILL, 21 },
            { TermSpecialControlCharacter.VREPRINT, 18 },
            { TermSpecialControlCharacter.VINTR, 3 },
            { TermSpecialControlCharacter.VQUIT, 0x1c },
            { TermSpecialControlCharacter.VSUSP, 26 },
            { TermSpecialControlCharacter.VSTART, 17 },
            { TermSpecialControlCharacter.VSTOP, 19 },
            { TermSpecialControlCharacter.VLNEXT, 22 },
            { TermSpecialControlCharacter.VDISCARD, 15 },
            { TermSpecialControlCharacter.VMIN, 1 },
            { TermSpecialControlCharacter.VTIME, 0 },
        };

        var term = new Termios(
                inputFlag: TermInputFlag.ICRNL | TermInputFlag.IXON | TermInputFlag.IXANY | TermInputFlag.IMAXBEL | TermInputFlag.BRKINT | TermInputFlag.IUTF8,
                outputFlag: TermOuptutFlag.OPOST | TermOuptutFlag.ONLCR,
                controlFlag: TermConrolFlag.CREAD | TermConrolFlag.CS8 | TermConrolFlag.HUPCL,
                localFlag: TermLocalFlag.ICANON | TermLocalFlag.ISIG | TermLocalFlag.IEXTEN | TermLocalFlag.ECHO | TermLocalFlag.ECHOE | TermLocalFlag.ECHOK | TermLocalFlag.ECHOKE | TermLocalFlag.ECHOCTL,
                speed: TermSpeed.B38400,
                controlCharacters: controlCharacters);

        int controller = 0;
        var pid = forkpty(ref controller, null, ref term, ref ws);

        if (pid == -1)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"forkpty failed with error {err}");
        }

        if (pid == 0)
        {
            if (environment != null)
            {
                foreach (var kv in environment)
                {
                    Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                }
            }

            if (chdir(workingDirectory) != 0)
            {
                Environment.Exit(Marshal.GetLastWin32Error());
            }

            var shellName = Path.GetFileName(shell);
            string[] args;
            if (string.IsNullOrEmpty(command))
            {
                args = new[] { shellName, "-i" };
            }
            else
            {
                args = new[] { shellName, "-c", command };
            }

            _ = execv(shell, args);
            
            // Should not reach here
            Environment.Exit(127);
        }

        return new UnixPty(controller, pid);
    }

    public int WaitForExit()
    {
        int status = 0;
        while (true)
        {
            var result = waitpid(_childPid, ref status, 0);
            if (result == -1)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"waitpid failed with error {err}");
            }

            if (result == _childPid)
            {
                break;
            }
        }

        return (status >> 8) & 0xff;
    }

    public void Resize(int cols, int rows)
    {
        var ws = new WinSize
        {
            Rows = (ushort)rows,
            Cols = (ushort)cols,
            XPixel = 0,
            YPixel = 0,
        };

        _ = ioctl(_masterFd, TIOCSWINSZ, ref ws);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stream?.Dispose();
        
        // Close the master file descriptor
        if (_masterFd > 0)
        {
            _ = close(_masterFd);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

