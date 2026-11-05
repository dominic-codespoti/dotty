using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class SavedPrivateModesTests
{
    private const string Esc = "\x1b";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Setup()
    {
        var adapter = new TerminalAdapter(rows: 8, columns: 12);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void AutoWrap_SaveAndRestoreSupportsBothDirectionsAndRepeatedRestore()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?7s{Esc}[?7l{Esc}[?7r{Esc}[?7l{Esc}[?7r");
            Assert.True(adapter.Buffer.AutoWrap);

            Feed(parser, $"{Esc}[?7l{Esc}[?7s{Esc}[?7h{Esc}[?7r");
            Assert.False(adapter.Buffer.AutoWrap);
            Feed(parser, $"{Esc}[?7h{Esc}[?7r");
            Assert.False(adapter.Buffer.AutoWrap);
        }
    }

    [Fact]
    public void SaveSlotsAreIndependentAndSavingOneOverwritesOnlyItsSlot()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?7l{Esc}[?7s{Esc}[?5h{Esc}[?5s{Esc}[?7h{Esc}[?5l");
            Feed(parser, $"{Esc}[?7r{Esc}[?5r");
            Assert.False(adapter.Buffer.AutoWrap);
            Assert.True(adapter.ReverseVideo);

            Feed(parser, $"{Esc}[?7h{Esc}[?7s{Esc}[?7l{Esc}[?7r");
            Assert.True(adapter.Buffer.AutoWrap);
        }
    }

    [Fact]
    public void UnsavedModesRestoreTheirZeroInitializedSlot()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?7r");
            Assert.False(adapter.Buffer.AutoWrap);
            Feed(parser, $"{Esc}[?1004h{Esc}[?1004r");
            Assert.False(adapter.FocusReportingEnabled);
        }
    }

    [Fact]
    public void MouseTrackingAndEncodingModesUseXtermSharedSlots()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?1000h{Esc}[?9s{Esc}[?1002h{Esc}[?1000r");
            Assert.Equal(TerminalAdapter.MouseMode.Normal, adapter.CurrentMouseMode);

            Feed(parser, $"{Esc}[?1006h{Esc}[?1005s{Esc}[?1016h{Esc}[?1006r");
            Assert.Equal(TerminalAdapter.MouseEncoding.SGR, adapter.CurrentMouseEncoding);
        }
    }

    [Fact]
    public void AlternateBufferModesShareOneCacheAndRestoreWithoutCursorActions()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[2;3H{Esc}7{Esc}[?47h{Esc}[4;5H{Esc}[?1047s");
            Feed(parser, $"{Esc}[?47l{Esc}[6;7H{Esc}[?1049r");
            Assert.True(adapter.Buffer.IsAlternateScreenActive);
            // Restoring the shared screen slot changes only the active buffer:
            // it neither restores an old alternate cursor nor saves the main cursor.
            Assert.Equal((5, 6), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
            Feed(parser, $"{Esc}[?47l{Esc}8");
            Assert.Equal((1, 2), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
        }
    }

    [Fact]
    public void OriginAndLeftRightMarginModesRestoreThroughTheirBehavioralSetters()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?6h{Esc}[?6s{Esc}[?6l{Esc}[?6r");
            Assert.True(adapter.Buffer.OriginMode);
            Assert.Equal(0, adapter.Buffer.CursorRow);

            Feed(parser, $"{Esc}[?69h{Esc}[?69s{Esc}[?69l{Esc}[?69r");
            Assert.True(adapter.Buffer.LeftRightMarginMode);
        }
    }
    [Fact]
    public void SavedBehavioralModesRestoreTheirLiveState()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?4h{Esc}[?4s{Esc}[?4l{Esc}[?4r");
            Feed(parser, $"{Esc}[?18h{Esc}[?18s{Esc}[?18l{Esc}[?18r");
            Feed(parser, $"{Esc}[?19h{Esc}[?19s{Esc}[?19l{Esc}[?19r");
            Feed(parser, $"{Esc}[?35l{Esc}[?35s{Esc}[?35h{Esc}[?35r");
            Feed(parser, $"{Esc}[?42h{Esc}[?42s{Esc}[?42l{Esc}[?42r");

            Assert.True(adapter.SmoothScrollEnabled);
            Assert.True(adapter.PrintFormFeedEnabled);
            Assert.True(adapter.PrintExtentFullPage);
            Assert.False(adapter.FontShiftModeEnabled);
            Assert.True(adapter.NationalReplacementEnabled);
        }
    }


    [Theory]
    [InlineData("c")]
    [InlineData("[!p")]
    public void RisAndDecstrPreserveXtermSavedModeCacheAndItsCursorEffect(string reset)
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?45h{Esc}[?45s{Esc}{reset}{Esc}[?45l{Esc}[?45rabcdefghijklmnopqrstuvwx{Esc}[2;1H{Esc}[D");
            Assert.True(adapter.Buffer.ReverseWraparound);
            Assert.Equal((0, 11), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
        }
    }


    [Fact]
    public void ModeStateCsiProcessesParametersPastEightEntries()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?7h{Esc}[?1;2;3;4;5;6;8;9;7s{Esc}[?7l{Esc}[?7r");
            Assert.True(adapter.Buffer.AutoWrap);
        }
    }

    [Theory]
    [InlineData("7:1")]
    [InlineData("7;?25")]
    [InlineData(">7")]
    public void InvalidModeStateCsiDoesNotPartiallySaveEarlierParameters(string parameters)
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?7l{Esc}[?7s{Esc}[?7h{Esc}[?{parameters}s{Esc}[?7r");
            Assert.False(adapter.Buffer.AutoWrap);
        }
    }

    [Fact]
    public void Mode1048SaveRestoreInvokesCursorActionsWithoutAValueSlot()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[2;3H{Esc}[?1048s{Esc}[6;7H{Esc}[?1048r");
            Assert.Equal((1, 2), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
        }
    }

    [Fact]
    public void UnsupportedAndActionOnlyModesDoNotChangePersistentSavedState()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[?7h{Esc}[?7s{Esc}[?1048s{Esc}[?999999999999999999999s{Esc}[?7l{Esc}[?1048r{Esc}[?999999999999999999999r{Esc}[?7r");
            Assert.True(adapter.Buffer.AutoWrap);
        }
    }

    [Fact]
    public void ScorC_CsiSavesCursorWithoutOverwritingMarginsAndCsiSDefaultsMargins()
    {
        var (adapter, parser) = Setup();
        using (adapter)
        {
            Feed(parser, $"{Esc}[2;3H{Esc}[s{Esc}[?69h{Esc}[1;10s{Esc}[5;6H{Esc}[s{Esc}[4;5H{Esc}[u");
            Assert.Equal((1, 2), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
            Assert.True(adapter.Buffer.LeftRightMarginMode);
            Assert.Equal((0, 11), (adapter.Buffer.WriteLeftMargin, adapter.Buffer.GetWriteRightMargin(0)));
        }
    }
}
