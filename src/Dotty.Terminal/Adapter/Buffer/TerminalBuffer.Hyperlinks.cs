using System;
using System.Collections.Generic;
using System.Threading;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>Hard cap on distinct OSC 8 URIs per buffer (ids are <see cref="ushort"/>; 0 = none).</summary>
    public const int MaxHyperlinkEntries = ushort.MaxValue;

    /// <summary>Total UTF-16 chars the URI table may retain (8 MiB).</summary>
    public const int MaxHyperlinkChars = 4 * 1024 * 1024;

    // Append-only table: entries [0, _hyperlinkCount) are immutable once published.
    // The writer (PTY thread) publishes a grown array before bumping the count, and
    // readers load the count before the array, so a reader never sees a count that
    // exceeds the array it holds. Snapshots keep (array, count) by reference.
    private string[] _hyperlinkTable = CreateHyperlinkTable();
    private int _hyperlinkCount = 1;
    private int _hyperlinkChars;
    private readonly Dictionary<string, ushort> _hyperlinkLookup = new();

    private static string[] CreateHyperlinkTable()
    {
        var table = new string[16];
        table[0] = string.Empty;
        return table;
    }

    /// <summary>
    /// Returns the id for <paramref name="uri"/>, creating it when new. Returns 0 for an
    /// empty URI and when the table is full (entry cap or character budget): ids are
    /// never reused or wrapped, so existing cells keep resolving to their own URL.
    /// Existing entries are still found after the cap is reached.
    /// </summary>
    public ushort GetOrCreateHyperlinkId(string uri) =>
        GetOrCreateHyperlinkId(uri.AsSpan());

    internal ushort GetOrCreateHyperlinkId(ReadOnlySpan<char> uri)
    {
        if (uri.IsEmpty)
            return 0;

        var lookup = _hyperlinkLookup.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(uri, out ushort id))
            return id;

        int count = _hyperlinkCount;
        if (count > MaxHyperlinkEntries || uri.Length > MaxHyperlinkChars - _hyperlinkChars)
            return 0;

        string value = uri.ToString();
        string[] table = _hyperlinkTable;
        if (count == table.Length)
        {
            var grown = new string[Math.Min(table.Length * 2, MaxHyperlinkEntries + 1)];
            Array.Copy(table, grown, count);
            table = grown;
            Volatile.Write(ref _hyperlinkTable, grown);
        }

        table[count] = value;
        _hyperlinkChars += value.Length;
        ushort idx = (ushort)count;
        _hyperlinkLookup[value] = idx;
        Volatile.Write(ref _hyperlinkCount, count + 1);
        return idx;
    }

    /// <summary>
    /// Gets the hyperlink URL for a given hyperlink ID.
    /// Returns null if the ID is invalid or not found.
    /// </summary>
    public string? GetHyperlinkUrl(ushort hyperlinkId)
    {
        GetHyperlinkTable(out string[] table, out int count);
        return LookupHyperlink(table, count, hyperlinkId);
    }

    /// <summary>Captures the published (array, count) pair; safe from any thread.</summary>
    internal void GetHyperlinkTable(out string[] table, out int count)
    {
        count = Volatile.Read(ref _hyperlinkCount);
        table = Volatile.Read(ref _hyperlinkTable);
    }

    internal static string? LookupHyperlink(string[] table, int count, ushort hyperlinkId)
    {
        if (hyperlinkId == 0 || hyperlinkId >= count || hyperlinkId >= table.Length)
            return null;
        return table[hyperlinkId];
    }
}
