using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>DECRQM (<c>CSI ? Ps $ p</c> / <c>CSI Ps $ p</c>): the full status table.</summary>
public sealed class ModeReportTests
{
    private const string Esc = "\x1b";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 10);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    // Sends a DECRQM and returns the single "$y" reply (other replies, such as the
    // immediate 2033 visibility report, are ignored).
    private static string Query(BasicAnsiParser parser, List<string> replies, string query)
    {
        replies.Clear();
        Feed(parser, query);
        return Assert.Single(replies, r => r.EndsWith("$y", StringComparison.Ordinal));
    }

    private static string Private(int mode, int state) => $"{Esc}[?{mode};{state}$y";

    // Default state of every mode that is tracked (set/reset) on a fresh adapter.
    public static TheoryData<int, int> TrackedDefaults => new()
    {
        { 1, 2 }, { 6, 2 }, { 7, 1 }, { 25, 1 }, { 47, 2 }, { 66, 2 }, { 67, 2 },
        { 1000, 2 }, { 1002, 2 }, { 1003, 2 }, { 1004, 2 }, { 1005, 2 }, { 1006, 2 }, { 1015, 2 },
        { 1047, 2 }, { 1049, 2 }, { 2004, 2 }, { 2026, 2 }, { 2033, 2 },
    };

    [Theory]
    [MemberData(nameof(TrackedDefaults))]
    public void TrackedPrivateMode_ReportsDefaultThenSetThenReset(int mode, int defaultState)
    {
        var (_, parser, replies) = Setup();
        Assert.Equal(Private(mode, defaultState), Query(parser, replies, $"{Esc}[?{mode}$p"));

        Feed(parser, $"{Esc}[?{mode}h");
        Assert.Equal(Private(mode, 1), Query(parser, replies, $"{Esc}[?{mode}$p"));

        Feed(parser, $"{Esc}[?{mode}l");
        Assert.Equal(Private(mode, 2), Query(parser, replies, $"{Esc}[?{mode}$p"));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 4)]
    [InlineData(11, 4)]
    [InlineData(12, 4)]
    [InlineData(16, 4)]
    [InlineData(18, 4)]
    [InlineData(19, 4)]
    [InlineData(40, 4)]
    [InlineData(42, 4)]
    [InlineData(45, 4)]
    [InlineData(53, 4)]
    [InlineData(59, 4)]
    [InlineData(60, 4)]
    [InlineData(61, 4)]
    [InlineData(64, 4)]
    [InlineData(68, 4)]
    [InlineData(69, 4)]
    [InlineData(73, 4)]
    [InlineData(80, 4)]
    [InlineData(81, 4)]
    [InlineData(95, 4)]
    [InlineData(1007, 4)]
    [InlineData(1016, 4)]
    [InlineData(1048, 4)]
    [InlineData(2027, 3)]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(96, 0)]
    [InlineData(117, 0)]
    [InlineData(1036, 0)]
    [InlineData(2031, 0)]
    [InlineData(2048, 0)]
    [InlineData(5522, 0)]
    public void FixedPrivateMode_IgnoresSetAndReset(int mode, int state)
    {
        var (_, parser, replies) = Setup();
        Assert.Equal(Private(mode, state), Query(parser, replies, $"{Esc}[?{mode}$p"));
        Feed(parser, $"{Esc}[?{mode}h");
        Assert.Equal(Private(mode, state), Query(parser, replies, $"{Esc}[?{mode}$p"));
        Feed(parser, $"{Esc}[?{mode}l");
        Assert.Equal(Private(mode, state), Query(parser, replies, $"{Esc}[?{mode}$p"));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(6, 0)]
    [InlineData(7, 4)]
    [InlineData(8, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 4)]
    [InlineData(11, 4)]
    [InlineData(12, 3)]
    [InlineData(13, 4)]
    [InlineData(14, 4)]
    [InlineData(15, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 4)]
    [InlineData(18, 4)]
    [InlineData(19, 4)]
    [InlineData(20, 4)]
    [InlineData(21, 0)]
    [InlineData(25, 0)]
    [InlineData(2004, 0)]
    public void AnsiMode_ReportsXtermTable(int mode, int state)
    {
        var (_, parser, replies) = Setup();
        Assert.Equal($"{Esc}[{mode};{state}$y", Query(parser, replies, $"{Esc}[{mode}$p"));
    }

    [Fact]
    public void AnsiMode_SetResetDoNotChangeReply()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, $"{Esc}[4h");
        Assert.Equal($"{Esc}[4;4$y", Query(parser, replies, $"{Esc}[4$p"));
        Feed(parser, $"{Esc}[12l");
        Assert.Equal($"{Esc}[12;3$y", Query(parser, replies, $"{Esc}[12$p"));
    }

    [Fact]
    public void AnsiAndPrivateSpacesAreIndependent()
    {
        var (_, parser, replies) = Setup();
        Assert.Equal(Private(7, 1), Query(parser, replies, $"{Esc}[?7$p"));
        Assert.Equal($"{Esc}[7;4$y", Query(parser, replies, $"{Esc}[7$p"));
    }

    [Fact]
    public void LiveStateIsReported_ForMouseExclusivityAndAlternateScreen()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, $"{Esc}[?1000h{Esc}[?1003h{Esc}[?1006h");
        Assert.Equal(Private(1000, 2), Query(parser, replies, $"{Esc}[?1000$p"));
        Assert.Equal(Private(1003, 1), Query(parser, replies, $"{Esc}[?1003$p"));
        Assert.Equal(Private(1006, 1), Query(parser, replies, $"{Esc}[?1006$p"));
        Assert.Equal(Private(1015, 2), Query(parser, replies, $"{Esc}[?1015$p"));

        Feed(parser, $"{Esc}[?47h");
        Assert.Equal(Private(47, 1), Query(parser, replies, $"{Esc}[?47$p"));
        Assert.Equal(Private(1047, 1), Query(parser, replies, $"{Esc}[?1047$p"));
        Assert.Equal(Private(1049, 1), Query(parser, replies, $"{Esc}[?1049$p"));
        Feed(parser, $"{Esc}[?1047l");
        Assert.Equal(Private(47, 2), Query(parser, replies, $"{Esc}[?47$p"));
    }

    [Fact]
    public void Decset1048_SavesAndRestoresCursor()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, $"{Esc}[2;3H{Esc}[?1048h{Esc}[4;5H{Esc}[?1048l");
        Assert.Equal(1, adapter.Buffer.CursorRow);
        Assert.Equal(2, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void Decset47_EntersAndLeavesAlternateScreen()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, $"{Esc}[?47h");
        Assert.True(adapter.Buffer.IsAlternateScreenActive);
        Feed(parser, $"{Esc}[?47l");
        Assert.False(adapter.Buffer.IsAlternateScreenActive);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(1047)]
    public void Decset47And1047_SwitchScreensWithoutSavingOrRestoringTheCursor(int mode)
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, $"{Esc}[2;3H{Esc}[?{mode}h");
        Assert.True(adapter.Buffer.IsAlternateScreenActive);
        Assert.Equal(1, adapter.Buffer.CursorRow); // the cursor carries over into the alternate screen
        Assert.Equal(2, adapter.Buffer.CursorCol);

        Feed(parser, $"{Esc}[4;5H{Esc}[?{mode}l");
        Assert.False(adapter.Buffer.IsAlternateScreenActive);
        Assert.Equal(3, adapter.Buffer.CursorRow); // not restored to the position before the switch
        Assert.Equal(4, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void Decset47_LeavesTheDecscSlotAlone()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, $"{Esc}[2;3H{Esc}7{Esc}[?47h{Esc}[4;5H{Esc}[?47l");
        Assert.Equal(3, adapter.Buffer.CursorRow);
        Feed(parser, $"{Esc}8");
        Assert.Equal(1, adapter.Buffer.CursorRow);
        Assert.Equal(2, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void Decset1049_SavesAndRestoresTheCursor()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, $"{Esc}[2;3H{Esc}[?1049h{Esc}[4;5H{Esc}[?1049l");
        Assert.False(adapter.Buffer.IsAlternateScreenActive);
        Assert.Equal(1, adapter.Buffer.CursorRow);
        Assert.Equal(2, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void Decset1049_FollowedBy47Reset_DoesNotRestoreTheCursor()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, $"{Esc}[2;3H{Esc}[?1049h{Esc}[4;5H{Esc}[?47l");
        Assert.False(adapter.Buffer.IsAlternateScreenActive);
        Assert.Equal(3, adapter.Buffer.CursorRow);
        Assert.Equal(4, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void Decrqm_ReflectsOtherTrackLiveState()
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetVisibility(false);
        Feed(parser, $"{Esc}[?2033h");
        Assert.Equal(Private(2033, 1), Query(parser, replies, $"{Esc}[?2033$p"));
        Feed(parser, $"{Esc}[?67h{Esc}[?66h");
        Assert.Equal(Private(67, 1), Query(parser, replies, $"{Esc}[?67$p"));
        Assert.Equal(Private(66, 1), Query(parser, replies, $"{Esc}[?66$p"));
        Feed(parser, $"{Esc}={Esc}>");
        Assert.Equal(Private(66, 2), Query(parser, replies, $"{Esc}[?66$p"));
    }

    [Fact]
    public void Decrqm_SplitAcrossFeedsIsAnsweredOnce()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, $"{Esc}[?2");
        Feed(parser, "02");
        Feed(parser, "7$");
        Assert.Empty(replies);
        Feed(parser, "p");
        Assert.Equal(new[] { Private(2027, 3) }, replies);

        replies.Clear();
        foreach (char c in $"{Esc}[12$p")
            Feed(parser, c.ToString());
        Assert.Equal(new[] { $"{Esc}[12;3$y" }, replies);
    }

    [Theory]
    [InlineData("4294967303", "2147483647")] // 2^32 + 7 must not alias DECAWM
    [InlineData("4294967297", "2147483647")] // 2^32 + 1 must not alias DECCKM
    [InlineData("2147483647", "2147483647")]
    [InlineData("9999999999999999999999999", "2147483647")] // 25 digits
    [InlineData("40000", "40000")]
    [InlineData("65537", "65537")] // 16-bit alias of 1
    [InlineData("65543", "65543")] // 16-bit alias of 7
    [InlineData("32768", "32768")]
    public void HugeModeNumbers_EchoSaturatedValueWithState0(string sent, string echoed)
    {
        var (_, parser, replies) = Setup();
        Assert.Equal($"{Esc}[?{echoed};0$y", Query(parser, replies, $"{Esc}[?{sent}$p"));
        Assert.Equal($"{Esc}[{echoed};0$y", Query(parser, replies, $"{Esc}[{sent}$p"));
    }

    [Fact]
    public void MultipleParameterQuery_IsSilent()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, $"{Esc}[?1;7$p{Esc}[1;7$p{Esc}[?$p{Esc}[$p");
        Assert.Empty(replies);
    }
}
