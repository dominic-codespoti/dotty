using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Dotty.Core;

public sealed class UnixPtyStream : Stream
{
    private readonly int _fd;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x800;

    public UnixPtyStream(int fd)
    {
        _fd = fd;
        SetNonBlocking();
    }

    private void SetNonBlocking()
    {
        try
        {
            int flags = fcntl(_fd, F_GETFL, 0);
            if (flags >= 0)
            {
                flags |= O_NONBLOCK;
                int result = fcntl(_fd, F_SETFL, flags);
                if (result < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    // Ignore - proceed with blocking mode
                }
            }
        }
        catch
        {
            // Ignore errors, proceed with blocking if set fails
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentException();

        // Copy from offset to a temp buffer starting at 0
        var temp = new byte[count];
        var result = read(_fd, temp, count);
        if (result < 0)
        {
            // On Unix/Linux, check errno
            int errno = Marshal.GetLastPInvokeError();
            if (errno == 0)
            {
                errno = Marshal.GetLastWin32Error();
            }
            
            // EAGAIN/EWOULDBLOCK - no data available, this is OK
            if (errno == 11 || errno == 35)
            {
                return 0;
            }
            
            throw new IOException($"read failed with errno {errno}");
        }
        if (result > 0)
        {
            Array.Copy(temp, 0, buffer, offset, result);
        }
        return result;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentException();

        // Copy from offset to a temp buffer starting at 0
        var temp = new byte[count];
        Array.Copy(buffer, offset, temp, 0, count);
        var result = write(_fd, temp, count);
        if (result < 0)
        {
            var err = Marshal.GetLastWin32Error();
            throw new IOException($"write failed with errno {err}");
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
