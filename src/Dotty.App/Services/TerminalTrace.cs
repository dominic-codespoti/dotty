using System;
using System.IO;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Services;

public static class TerminalTrace
{
    private static readonly string? s_path;
    private static int s_seq;
    private static readonly object s_lock = new();
    private static DateTime s_lastSnapshot = DateTime.MinValue;

    static TerminalTrace()
    {
        s_path = Environment.GetEnvironmentVariable("DOTTY_TRACE");
        if (s_path != null)
        {
            try { File.WriteAllText(s_path, ""); } catch { s_path = null; }
        }
    }

    public static bool Enabled => s_path != null;

    public static void Snapshot(TerminalBuffer buffer, string reason)
    {
        if (s_path == null) return;
        var now = DateTime.UtcNow;

        var sb = new StringBuilder();
        sb.Append($"SEQ={++s_seq} T={now:HH:mm:ss.fff} REASON={reason} ");
        sb.Append(buffer.GetDebugInfo());
        sb.Append('\n');

        lock (s_lock)
        {
            try { File.AppendAllText(s_path, sb.ToString()); } catch { }
        }
    }

    public static void Event(string description)
    {
        if (s_path == null) return;
        var now = DateTime.UtcNow;
        var line = $"SEQ={++s_seq} T={now:HH:mm:ss.fff} EVENT={description}\n";

        lock (s_lock)
        {
            try { File.AppendAllText(s_path, line); } catch { }
        }
    }
}
