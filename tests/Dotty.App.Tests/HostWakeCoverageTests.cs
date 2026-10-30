using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Safety net for the idle wait (<see cref="IdleWait.SafetyCapMs"/>): the UI loop sleeps until a deadline unless
/// something requests a wake, so every way of handing work to the UI thread from another thread must call
/// <c>HostWake.Request</c>. The behavioural tests drive the reachable enqueue APIs from a background thread; the
/// source test pins every host queue producer, including the private ones of <c>DottyWindowHost</c>.
/// </summary>
[Collection(HostWakeCollection.Name)]
public sealed partial class HostWakeCoverageTests : IDisposable
{
    private int _posts;

    public HostWakeCoverageTests()
    {
        HostWake.Shutdown();
        HostWake.Initialize(() => Interlocked.Increment(ref _posts), Environment.CurrentManagedThreadId);
    }

    public void Dispose() => HostWake.Shutdown();

    [Fact]
    public async Task LifecycleEnqueueFromAnotherThreadPostsAWake()
    {
        using var lifecycle = new WindowLifecycleCoordinator();

        bool enqueued = await Task.Run(() => lifecycle.TryEnqueue(static () => { }), TestContext.Current.CancellationToken);

        Assert.True(enqueued);
        Assert.Equal(1, Volatile.Read(ref _posts));
    }

    [Fact]
    public async Task GateInvalidateAndRequeueFromAnotherThreadPostAWake()
    {
        WindowFrameReason previous = WindowPresentationGate.Consume();
        try
        {
            await Task.Run(() => WindowPresentationGate.Invalidate(WindowFrameReason.Content), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref _posts));
            HostWake.Reset();

            await Task.Run(() => WindowPresentationGate.Requeue(WindowFrameReason.ImageUpload), TestContext.Current.CancellationToken);
            Assert.Equal(2, Volatile.Read(ref _posts));
        }
        finally
        {
            WindowPresentationGate.Consume();
            WindowPresentationGate.Requeue(previous);
        }
    }

    // A cross-thread hand-off into a host queue: the private `_pending*` queues of DottyWindowHost and
    // WindowLifecycleCoordinator, the VT resize request slot and the OSC 52 read queue.
    [GeneratedRegex(@"\b(?:_pending\w*|ResizeRequests|_queue)\.(?:Enqueue|TryEnqueue|Post)\(")]
    private static partial Regex ProducerPattern();

    [GeneratedRegex(@"^\s*(?:(?:private|internal|public|protected|static|sealed|override|async|readonly|unsafe)\s+)+[\w<>\[\],.?\s]+\s\w+\s*\(")]
    private static partial Regex MemberDeclarationPattern();

    private static readonly string[] WakeCalls = { "HostWake.Request()", "_environment.Wake()" };

    [Fact]
    public void EveryHostQueueProducerRequestsAWakeInTheSameMember()
    {
        string hostDirectory = HostSourceDirectory();
        var missing = new List<string>();
        int producers = 0;
        foreach (string file in Directory.GetFiles(hostDirectory, "*.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ProducerPattern().IsMatch(lines[i]))
                    continue;

                producers++;
                if (!WakeFollowsInSameMember(lines, i))
                    missing.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.Empty(missing);
        // Process exits, tab titles, window titles, clipboard writes (2), control commands, lifecycle callbacks,
        // VT resize requests, OSC 52 reads. A lower count means the pattern stopped matching, not fewer producers.
        Assert.True(producers >= 9, $"expected at least 9 host queue producers, found {producers}");
    }

    private static bool WakeFollowsInSameMember(string[] lines, int producerLine)
    {
        for (int i = producerLine; i < lines.Length; i++)
        {
            if (i > producerLine && MemberDeclarationPattern().IsMatch(lines[i]))
                return false;
            foreach (string wake in WakeCalls)
            {
                if (lines[i].Contains(wake, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static string HostSourceDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "src", "Dotty", "Host"));
}
