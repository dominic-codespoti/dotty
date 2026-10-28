using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Seeded mutation fuzzing of the whole graphics path (parser, controller, transfer, store, placements) through a
/// real <see cref="TerminalAdapter"/>. Valid streams are mutated (bit flips, truncation, duplicated / removed /
/// replaced slices, injected control bytes, arbitrary feed slicing); whatever the input, nothing may throw, the
/// stores must stay consistent, the reply stream must stay well-formed, the memory counters must return to their
/// baseline, and the parser must recover so that plain text prints again. Deterministic: fixed seeds only.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyMutationFuzzTests
{
    private const int IterationsPerSeed = 500;

    // Bit flips stay below bit 7 (no C1 controls / broken UTF-8); the injected bytes are protocol-relevant.
    private static readonly byte[] InjectAlphabet = Encoding.ASCII.GetBytes("\u001b\u0018\u001a\u0007;,=\\_Gabcdfimopqrstvxyz0123456789-ACIPQXYZ");

    private sealed class Term : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(8, 40);
        public BasicAnsiParser Parser { get; }
        public List<string> Replies { get; } = new();

        public Term()
        {
            // Shared memory and temp-file media stay disabled so no mutated stream can open a real shm object or
            // delete a file (t=t is the only deleting transmission); both corpus entries below exercise the refusal.
            // File reading never modifies anything.
            var media = KittyMediaFlags.Direct | KittyMediaFlags.File;
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true, Media = media, MaxPlacementsPerScreen = 64 }, new FakeImageDecoder());
            Adapter.ReplyRequested += reply => Replies.Add(reply.ToString());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public void Feed(byte[] bytes) => Parser.Feed(bytes);

        public void Dispose() => Adapter.Dispose();
    }

    // ---------------------------------------------------------------------------------------------- corpus

    private static byte[] Apc(string control, string payload = "") => ApcBytes(control, payload);

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static List<(string Name, byte[] Bytes, bool AllOk)> BuildCorpus()
    {
        byte[] six = { 255, 0, 0, 0, 255, 0 };
        byte[] four = { 200, 100, 50, 128 };
        byte[] png = TestPng.Build(2, 1, new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 });
        byte[] zlib = TestPng.Zlib(new byte[] { 1, 2, 3 });
        string sixB64 = B64(six);
        string transmit = "a=T,i=1,s=2,v=1,f=24,c=2,r=1";
        var corpus = new List<(string, byte[], bool)>
        {
            ("raw24", Apc(transmit, sixB64), true),
            ("raw32", Apc("a=t,i=2,s=1,v=1,f=32", B64(four)), true),
            ("png", Apc("a=T,i=3,f=100,c=2,r=1", B64(png)), true),
            ("zlib", Apc("a=t,i=4,s=1,v=1,f=24,o=z", B64(zlib)), true),
            ("chunked", Cat(Apc("a=t,i=5,s=2,v=1,f=24,m=1", sixB64.Substring(0, 4)), Apc("m=0", sixB64.Substring(4))), true),
            ("chunked3", Cat(Apc("a=t,i=5,s=2,v=1,f=24,m=1", sixB64.Substring(0, 4)), Apc("m=1", sixB64.Substring(4, 2)), Apc("m=0", sixB64.Substring(6))), true),
            ("number", Apc("a=t,I=3,s=1,v=1,f=24", B64(new byte[] { 1, 2, 3 })), true),
            ("put", Cat(Apc("a=t,i=6,s=1,v=1,f=24", B64(new byte[] { 1, 2, 3 })), Apc("a=p,i=6,p=1,c=2,r=1"), Apc("a=p,i=6,p=2,x=0,y=0,w=1,h=1,z=-1,C=1")), true),
            ("query", Apc("a=q,i=7,s=1,v=1,f=24", B64(new byte[] { 1, 2, 3 })), true),
            ("virtual", Cat(Apc("a=T,i=8,s=1,v=1,f=24,U=1,c=2,r=2", B64(new byte[] { 1, 2, 3 }))), true),
            ("file", Apc("a=t,i=9,s=1,v=1,f=24,t=f", B64(Encoding.UTF8.GetBytes("/nonexistent/dotty-fuzz.bin"))), false),
            ("shm-refused", Apc("a=t,i=10,s=1,v=1,f=24,t=s", B64(Encoding.UTF8.GetBytes("dotty-fuzz-shm"))), false),
            ("tempfile-refused", Apc("a=t,i=11,s=1,v=1,f=24,t=t", B64(Encoding.UTF8.GetBytes("/nonexistent/tty-graphics-protocol-dotty-fuzz.bin"))), false),
        };
        foreach (char d in "aAcCiIpPqQxXyYzZnN")
        {
            corpus.Add(("delete-" + d, Cat(
                Apc(transmit, sixB64),
                Apc("a=p,i=1,p=1,c=1,r=1"),
                Apc("a=d,d=" + d + ",i=1,p=1,x=1,y=1,z=0,I=1")), true));
        }
        return corpus;
    }

    // ------------------------------------------------------------------------------------------ mutations

    private static byte[] Mutate(byte[] source, Random rng)
    {
        var data = source.ToList();
        switch (rng.Next(6))
        {
            case 0:
                for (int n = rng.Next(1, 5); n > 0 && data.Count > 0; n--)
                    data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(7));
                break;
            case 1:
                data = data.Take(rng.Next(data.Count + 1)).ToList();       // truncation at a random offset
                break;
            case 2:
                if (data.Count > 0)
                {
                    int start = rng.Next(data.Count);
                    int length = rng.Next(1, Math.Min(41, data.Count - start + 1));
                    data.InsertRange(rng.Next(data.Count + 1), data.GetRange(start, length));   // duplicated slice (chunks included)
                }
                break;
            case 3:
                for (int n = rng.Next(1, 7); n > 0; n--)
                    data.Insert(rng.Next(data.Count + 1), InjectAlphabet[rng.Next(InjectAlphabet.Length)]);
                break;
            case 4:
                if (data.Count > 0)
                {
                    int start = rng.Next(data.Count);
                    data.RemoveRange(start, rng.Next(1, Math.Min(41, data.Count - start + 1)));
                }
                break;
            default:
                if (data.Count > 0)
                {
                    int start = rng.Next(data.Count);
                    int length = rng.Next(1, Math.Min(9, data.Count - start + 1));
                    for (int i = 0; i < length; i++)
                        data[start + i] = InjectAlphabet[rng.Next(InjectAlphabet.Length)];
                }
                break;
        }
        return data.ToArray();
    }

    private static void FeedSliced(Term t, byte[] bytes, Random rng)
    {
        int pieces = rng.Next(1, 5);
        var cuts = new SortedSet<int> { 0, bytes.Length };
        for (int i = 1; i < pieces; i++)
            cuts.Add(rng.Next(bytes.Length + 1));
        var list = cuts.ToList();
        for (int i = 0; i + 1 < list.Count; i++)
            t.Feed(bytes.AsSpan(list[i], list[i + 1] - list[i]).ToArray());
    }

    private static void AssertHealthyAndRecover(Term t, string label)
    {
        // 1. Store consistency and counters, before anything cleans up.
        t.Adapter.Buffer.MainImageStore?.ValidateInvariants();
        t.Adapter.Buffer.AlternateImageStore?.ValidateInvariants();
        var snapshot = GraphicsMemoryMetrics.GetSnapshot();
        Assert.True(snapshot.LiveImagePixelBytes >= 0 && snapshot.LiveImageCount >= 0 && snapshot.PendingTransferBytes >= 0 && snapshot.LivePlacementCount >= 0,
            label + ": a graphics counter went negative");

        // 2. Every graphics reply is a well-formed escape sequence.
        foreach (string reply in t.Replies)
        {
            if (!reply.StartsWith("\u001b_G", StringComparison.Ordinal))
                continue;
            Assert.True(reply.EndsWith("\u001b\\", StringComparison.Ordinal) && reply.Contains(';'), label + ": malformed reply " + reply);
            Assert.True(reply.Length <= KittyResponse.MaxLength, label + ": reply too long");
        }
        t.Replies.Clear();

        // 3. The parser recovers: cancel whatever is open, hard reset, and plain text prints again.
        t.Feed(Encoding.ASCII.GetBytes("\u0018\u0018\u001b\\\u001bc\u001b[1;1HOK"));
        string row = t.Adapter.Buffer.GetRowText(0);
        Assert.True(row.StartsWith("OK", StringComparison.Ordinal), label + ": parser did not recover, row 0 = '" + row.TrimEnd() + "'");
        t.Replies.Clear();
    }

    private static void FeedGuarded(Term t, byte[] bytes, Random? rng, string label)
    {
        try
        {
            if (rng == null)
                t.Feed(bytes);
            else
                FeedSliced(t, bytes, rng);
        }
        catch (Exception ex)
        {
            Assert.Fail(label + ": an exception escaped Feed: " + ex);
        }
    }

    // ---------------------------------------------------------------------------------------------- tests

    [Fact]
    public void TheUnmutatedCorpusIsAcceptedAndAnswered()
    {
        using var t = new Term();
        foreach (var (name, bytes, allOk) in BuildCorpus())
        {
            FeedGuarded(t, bytes, null, name);
            Assert.True(t.Replies.Count >= 1, name + ": no reply");
            if (allOk)
                Assert.All(t.Replies, r => Assert.EndsWith(";OK\u001b\\", r));
            AssertHealthyAndRecover(t, name);
        }
    }

    [Fact]
    public void ARoundTripOfTheRawCorpusEntryIsExact()
    {
        using var t = new Term();
        FeedGuarded(t, Apc("a=t,i=1,s=2,v=1,f=24", B64(new byte[] { 255, 0, 0, 0, 255, 0 })), null, "raw");
        Assert.Equal(new[] { Ok("i=1") }, t.Replies);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, t.Adapter.Buffer.ActiveImageStore.FindByClientId(1)!.Root.AsSpan().ToArray());
    }

    [Fact]
    public void TruncationAtEveryOffsetOfEveryCorpusEntryIsSurvived()
    {
        var baseline = new GraphicsBaseline();
        long placementsBefore = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        using (var t = new Term())
        {
            foreach (var (name, bytes, _) in BuildCorpus())
            {
                for (int cut = 0; cut <= bytes.Length; cut++)
                {
                    FeedGuarded(t, bytes.AsSpan(0, cut).ToArray(), null, name + " cut at " + cut);
                    AssertHealthyAndRecover(t, name + " cut at " + cut);
                }
            }
        }
        baseline.AssertRestored();
        Assert.Equal(placementsBefore, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MutatedStreamsNeverBreakTheTerminal(int seed)
    {
        var baseline = new GraphicsBaseline();
        long placementsBefore = GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount;
        var corpus = BuildCorpus();
        var rng = new Random(seed * 7919 + 13);
        using (var t = new Term())
        {
            for (int i = 0; i < IterationsPerSeed; i++)
            {
                var stream = new List<byte>();
                for (int parts = rng.Next(1, 4); parts > 0; parts--)
                    stream.AddRange(corpus[rng.Next(corpus.Count)].Bytes);
                byte[] mutated = Mutate(stream.ToArray(), rng);

                string label = "seed " + seed + " iteration " + i;
                FeedGuarded(t, mutated, rng, label);
                AssertHealthyAndRecover(t, label);
            }
        }
        baseline.AssertRestored();
        Assert.Equal(placementsBefore, GraphicsMemoryMetrics.GetSnapshot().LivePlacementCount);
    }

    [Fact]
    public void TheSameSeedProducesTheSameMutations()
    {
        var corpus = BuildCorpus();
        byte[] source = corpus[0].Bytes;
        var a = new Random(42);
        var b = new Random(42);
        for (int i = 0; i < 50; i++)
            Assert.Equal(Mutate(source, a), Mutate(source, b));
    }

    [Fact]
    public void MutationsActuallyChangeTheStreamAndCoverEveryKind()
    {
        byte[] source = BuildCorpus()[0].Bytes;
        var rng = new Random(5);
        int changed = 0, shorter = 0, longer = 0;
        for (int i = 0; i < 300; i++)
        {
            byte[] m = Mutate(source, rng);
            if (!m.AsSpan().SequenceEqual(source)) changed++;
            if (m.Length < source.Length) shorter++;
            if (m.Length > source.Length) longer++;
        }
        Assert.True(changed > 200, "most mutations change the input: " + changed);
        Assert.True(shorter > 20 && longer > 20, $"shorter={shorter} longer={longer}");
    }
}
