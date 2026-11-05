using System.Text;
using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// OSC string benchmarks (plan 08, WI-1b): changing OSC 0 titles, OSC 52 clipboard writes of
/// 4 KiB / 64 KiB / oversize payloads, OSC 8 hyperlinks and OSC 133 prompt cycles. Complements
/// <c>'Parse OSC: Window Title'</c> (constant title, never reaches <c>ToString</c>).
///
/// Payload sizes are given as base64 characters on the wire. The parser's OSC cap
/// (<c>MaxOscPayloadBytes</c>, 1 MiB at the time of writing) counts every byte after <c>ESC ]</c>, including
/// the <c>52;c;</c> prefix, so the oversize variant is exactly one byte over that cap
/// (<see cref="OversizePayloadChars"/> base64 chars); re-derive that constant if the cap changes.
/// Only <see cref="BasicAnsiParser.Feed"/> and public adapter events are used.
/// </summary>
[BenchmarkCategory("Parser", "Osc")]
public class ParserOscBenchmarks : PerformanceTestBase
{
    private const int TitleCount = 256;
    private const int PromptCyclesPerInvoke = 16;
    private const int HyperlinkOpsPerInvoke = 64;
    private const int FeedChunkSize = 8192; // mirrors TerminalSession's PTY feed slice

    // Total OSC payload "52;c;" (5 bytes) + data = 1 MiB + 1 -> data = 1,048,572 chars (a multiple of 4).
    private const int OversizePayloadChars = 1024 * 1024 + 1 - 5;

    private BasicAnsiParser _parser = null!;
    private TerminalAdapter _adapter = null!;
    private byte[][] _titles = null!;
    private byte[] _osc52Small = null!;
    private byte[] _osc52Large = null!;
    private byte[] _osc52Oversize = null!;
    private byte[] _promptCycle = null!;
    private byte[] _hyperlink = null!;
    private int _titleCursor;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();

        _adapter = new TerminalAdapter(24, 80, scrollbackCapacity: 0);
        _adapter.ClipboardWriteRequested += static _ => { };
        _adapter.TitleChanged += static _ => { };
        _parser = new BasicAnsiParser { Handler = _adapter };

        _titles = new byte[TitleCount][];
        for (int i = 0; i < TitleCount; i++)
            _titles[i] = Encoding.ASCII.GetBytes($"\u001b]0;{BuildTitle(i)}\u0007");

        _osc52Small = BuildOsc52(4096);
        _osc52Large = BuildOsc52(64 * 1024);
        _osc52Oversize = BuildOsc52(OversizePayloadChars);

        _promptCycle = Encoding.ASCII.GetBytes(
            "\u001b]133;A\u0007\u001b]133;B\u0007\u001b]133;C\u0007\u001b]133;D;0\u0007");
        _hyperlink = Encoding.ASCII.GetBytes(
            "\u001b]8;;https://example.test/path?q=1\u0007link text\u001b]8;;\u0007");

        // Prompt marks are capped (5000) by dropping the oldest on every add. Fill to the cap so the timed
        // cycles measure the steady state instead of the 0 -> 5000 ramp.
        for (int i = 0; i < 1300; i++)
            _parser.Feed(_promptCycle);

        // JIT + steady state for every timed path.
        foreach (byte[] title in _titles)
            _parser.Feed(title);
        _parser.Feed(_osc52Small);
        _parser.Feed(_osc52Large);
        _parser.Feed(_osc52Oversize);
        for (int i = 0; i < HyperlinkOpsPerInvoke; i++)
            _parser.Feed(_hyperlink);
    }

    // Exactly 32 bytes; distinct per index so the title changes on every sequence (exercises ToString).
    private static string BuildTitle(int index) => $"Terminal title number {index:D5} xyz!";

    private static byte[] BuildOsc52(int base64Chars)
    {
        if (base64Chars % 4 != 0)
            throw new ArgumentException("Base64 length must be a multiple of 4.", nameof(base64Chars));
        int decodedLength = base64Chars / 4 * 3;
        var raw = new byte[decodedLength];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (byte)('a' + i % 26); // ASCII text; decodes to a valid UTF-8 clipboard string
        string base64 = Convert.ToBase64String(raw);
        return Encoding.ASCII.GetBytes($"\u001b]52;c;{base64}\u0007");
    }

    [Benchmark(Description = "Parse OSC 0: 32B title, changing", OperationsPerInvoke = TitleCount)]
    public void Osc0_Title_Changing()
    {
        // Rotate the start index so consecutive invokes never see the same title twice in a row.
        int start = _titleCursor++ & (TitleCount - 1);
        for (int i = 0; i < TitleCount; i++)
            _parser.Feed(_titles[(start + i) & (TitleCount - 1)]);
    }

    [Benchmark(Description = "Parse OSC 52: 4 KiB payload")]
    public void Osc52_4KiB() => _parser.Feed(_osc52Small);

    [Benchmark(Description = "Parse OSC 52: 64 KiB payload, single Feed")]
    public void Osc52_64KiB_SingleFeed() => _parser.Feed(_osc52Large);

    [Benchmark(Description = "Parse OSC 52: 64 KiB payload, 8 KiB chunks")]
    public void Osc52_64KiB_Chunked()
    {
        ReadOnlySpan<byte> remaining = _osc52Large;
        while (!remaining.IsEmpty)
        {
            int take = Math.Min(FeedChunkSize, remaining.Length);
            _parser.Feed(remaining[..take]);
            remaining = remaining[take..];
        }
    }

    [Benchmark(Description = "Parse OSC 52: 1 MiB + 1 oversize (discarded)")]
    public void Osc52_Oversize_Discarded() => _parser.Feed(_osc52Oversize);

    [Benchmark(Description = "Parse OSC 8: hyperlink around 9B text", OperationsPerInvoke = HyperlinkOpsPerInvoke)]
    public void Osc8_Hyperlink()
    {
        for (int i = 0; i < HyperlinkOpsPerInvoke; i++)
            _parser.Feed(_hyperlink);
    }

    [Benchmark(Description = "Parse OSC 133: A;B;C;D prompt cycle", OperationsPerInvoke = PromptCyclesPerInvoke)]
    public void Osc133_PromptCycle()
    {
        for (int i = 0; i < PromptCyclesPerInvoke; i++)
            _parser.Feed(_promptCycle);
    }
}
