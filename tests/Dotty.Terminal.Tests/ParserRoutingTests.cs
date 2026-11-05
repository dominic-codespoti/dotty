using System.Text;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>CSI routing to handler hooks and saturating numeric parsing.</summary>
public sealed class ParserRoutingTests
{
    private static (BasicAnsiParser Parser, BoundedSequenceTests.RecordingHandler Handler) Setup()
    {
        var handler = new BoundedSequenceTests.RecordingHandler();
        return (new BasicAnsiParser { Handler = handler }, handler);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("\u001b[!p")]
    [InlineData("\u001b[5!p")]
    [InlineData("\u001b[1;2!p")]
    public void Decstr_RoutesToSoftReset(string input)
    {
        var (parser, handler) = Setup();
        Feed(parser, input);
        Assert.Equal(1, handler.SoftResetCalls);
    }

    [Fact]
    public void Decstr_SplitAcrossFeeds_DispatchesOnce()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[");
        Feed(parser, "!");
        Assert.Equal(0, handler.SoftResetCalls);
        Feed(parser, "p");
        Assert.Equal(1, handler.SoftResetCalls);
    }

    [Fact]
    public void Decstr_ResetsParserCharset()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b(0\u001b[!pq");
        Assert.Equal(new[] { "q" }, handler.PrintCalls);
    }

    [Fact]
    public void Decstr_RejectsOtherIntermediates()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[?!p\u001b[!$p");
        Assert.Equal(0, handler.SoftResetCalls);
    }

    [Fact]
    public void Dsr_GreaterThanForm_IsIgnored()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[>6n");
        Assert.Empty(handler.DeviceStatusReportCodes);
        Assert.Empty(handler.PrivateDeviceStatusReportCodes);
        Assert.Equal(0, handler.CursorPositionReportCalls);
    }

    [Fact]
    public void Dsr_PlainAndPrivateFormsRoute()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[5n\u001b[6n\u001b[?6n\u001b[?998n\u001b[?15n");
        Assert.Equal(new[] { 5, 6 }, handler.DeviceStatusReportCodes);
        Assert.Equal(1, handler.CursorPositionReportCalls);
        Assert.Equal(new[] { 998, 15 }, handler.PrivateDeviceStatusReportCodes);
    }

    [Theory]
    [InlineData("\u001b[>1t")]
    [InlineData("\u001b[?18t")]
    [InlineData("\u001b[>8;24;80t")]
    public void WindowOperation_PrivateFormsAreIgnored(string input)
    {
        var (parser, handler) = Setup();
        Feed(parser, input);
        Assert.Empty(handler.WindowOperationCalls);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1007)]
    [InlineData(1016)]
    public void PrivateModes_MouseModesRouteToSetMouseMode(int mode)
    {
        var (parser, handler) = Setup();
        Feed(parser, $"\u001b[?{mode}h\u001b[?{mode}l");
        Assert.Equal(new[] { (mode, true), (mode, false) }, handler.PrivateModeCalls);
    }

    [Fact]
    public void PrivateModes_66RoutesToKeypadApplicationMode()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[?66h\u001b[?66l");
        Assert.Equal(new[] { true, false }, handler.KeypadApplicationModeCalls);
    }

    [Fact]
    public void PrivateModes_67RoutesToBackarrowKeyMode()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[?67h\u001b[?67l");
        Assert.Equal(new[] { true, false }, handler.BackarrowKeyModeCalls);
    }

    [Fact]
    public void PrivateModes_2033RoutesToVisibilityReports()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[?2033h\u001b[?2033l");
        Assert.Equal(new[] { true, false }, handler.VisibilityReportCalls);
    }

    [Fact]
    public void PrivateModes_2027IsAcceptedAndIgnored()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[?2027h\u001b[?2027l");
        Assert.Empty(handler.PrivateModeCalls);
        Assert.Empty(handler.KeypadApplicationModeCalls);
        Assert.Empty(handler.BackarrowKeyModeCalls);
        Assert.Empty(handler.VisibilityReportCalls);
    }

    [Fact]
    public void Decrqcra_ForwardsSixFieldsWithZeroDefaults()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[7;1;2;3;4;5*y\u001b[7;;1;1;1;2*y\u001b[*y\u001b[9;9*y");
        Assert.Equal(
            new[] { (7, 1, 2, 3, 4, 5), (7, 0, 1, 1, 1, 2), (0, 0, 0, 0, 0, 0), (9, 9, 0, 0, 0, 0) },
            handler.ChecksumCalls);
    }

    [Fact]
    public void Decrqcra_HugeIdSaturatesInsteadOfWrapping()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[4294967295*y");
        Assert.Equal(new[] { (int.MaxValue, 0, 0, 0, 0, 0) }, handler.ChecksumCalls);
    }

    [Fact]
    public void Decrqcra_NonNumericFieldIsDropped()
    {
        var (parser, handler) = Setup();
        Feed(parser, "\u001b[1;?2*y");
        Assert.Empty(handler.ChecksumCalls);
    }

    [Fact]
    public void Decrqm_HugeModeNumberSaturatesInsteadOfWrapping()
    {
        var (parser, handler) = Setup();
        // 4294967303 == 2^32 + 7: wraps to DECAWM with unchecked arithmetic.
        Feed(parser, "\u001b[?4294967303$p\u001b[4294967303$p");
        Assert.Equal(new[] { (int.MaxValue, true), (int.MaxValue, false) }, handler.RequestModeCalls);
    }

    [Fact]
    public void Osc_HugeCodeDoesNotWrapIntoOsc8()
    {
        var (parser, handler) = Setup();
        // 4294967304 == 2^32 + 8.
        Feed(parser, "\u001b]4294967304;https://example.com\u0007");
        Assert.DoesNotContain(handler.OscCalls, call => call.code == 8);
        Assert.Equal(new[] { int.MaxValue }, handler.OscCalls.ConvertAll(call => call.code));
    }
}
