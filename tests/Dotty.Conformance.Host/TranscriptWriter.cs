using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Dotty.Conformance.Host;

/// <summary>
/// Line-oriented conversation transcript:
/// "&lt;elapsed-ms&gt; &lt;C|R&gt; &lt;text&gt;" where ESC is rendered as &lt;ESC&gt; and other C0/DEL bytes as &lt;XX&gt; hex.
/// C = bytes the child wrote, R = terminal replies sent to the child.
/// </summary>
internal sealed class TranscriptWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private bool _disposed;

    public TranscriptWriter(string path)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void FromChild(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length + 16);
        foreach (byte b in bytes)
            Append(sb, b);
        Write('C', sb);
    }

    public void ToChild(ReadOnlySpan<char> reply)
    {
        var sb = new StringBuilder(reply.Length + 16);
        foreach (char c in reply)
            Append(sb, c);
        Write('R', sb);
    }
    public void ToChild(ReadOnlySpan<byte> reply)
    {
        var sb = new StringBuilder(reply.Length + 16);
        foreach (byte value in reply)
            Append(sb, value);
        Write('R', sb);
    }


    private static void Append(StringBuilder sb, int value)
    {
        if (value == 0x1B)
            sb.Append("<ESC>");
        else if (value == '\n')
            sb.Append("<0A>");
        else if (value < 0x20 || value == 0x7F)
            sb.Append('<').Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append('>');
        else if (value < 0x80)
            sb.Append((char)value);
        else if (value <= 0xFF)
            sb.Append('<').Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append('>');
        else
            sb.Append((char)value);
    }

    private void Write(char direction, StringBuilder text)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _writer.Write(_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            _writer.Write(' ');
            _writer.Write(direction);
            _writer.Write(' ');
            _writer.WriteLine(text.ToString());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
