using System.Text;
using System.Text.Json;
using System.Threading;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class MouseOptionsTests
{
    [Fact]
    public void Defaults_PreserveCurrentBehavior()
    {
        var mouse = new DottyUserConfig().Mouse;

        Assert.Equal("none", mouse.CopyOnSelect);
        Assert.Equal("clipboard-paste", mouse.MiddleClickAction);
        Assert.Equal(CopyOnSelectMode.None, MouseOptions.ParseCopyOnSelect(mouse.CopyOnSelect));
        Assert.Equal(MiddleClickAction.ClipboardPaste, MouseOptions.ParseMiddleClick(mouse.MiddleClickAction));
    }

    [Fact]
    public void Json_RoundTripsAndMissingOrNullGroupUsesDefaults()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"mouse\":{\"copyOnSelect\":\"both\",\"middleClickAction\":\"primary-paste\"}}")!;
        Assert.Equal(CopyOnSelectMode.Both, MouseOptions.ParseCopyOnSelect(config.Mouse.CopyOnSelect));
        Assert.Equal(MiddleClickAction.PrimaryPaste, MouseOptions.ParseMiddleClick(config.Mouse.MiddleClickAction));

        Assert.Equal("none", JsonSerializer.Deserialize<DottyUserConfig>("{}")!.Mouse.CopyOnSelect);
    }

    [Theory]
    [InlineData("none", CopyOnSelectMode.None)]
    [InlineData("PRIMARY", CopyOnSelectMode.Primary)]
    [InlineData("  clipboard ", CopyOnSelectMode.Clipboard)]
    [InlineData("Both", CopyOnSelectMode.Both)]
    [InlineData("false", CopyOnSelectMode.None)]
    [InlineData("", CopyOnSelectMode.None)]
    [InlineData(null, CopyOnSelectMode.None)]
    [InlineData("sometimes", CopyOnSelectMode.None)]
    public void ParseCopyOnSelect_IsForgiving(string? value, CopyOnSelectMode expected) =>
        Assert.Equal(expected, MouseOptions.ParseCopyOnSelect(value, isLinux: true));

    [Fact]
    public void ParseCopyOnSelect_LegacyTrueDependsOnPlatform()
    {
        Assert.Equal(CopyOnSelectMode.Primary, MouseOptions.ParseCopyOnSelect("true", isLinux: true));
        Assert.Equal(CopyOnSelectMode.Clipboard, MouseOptions.ParseCopyOnSelect("TRUE", isLinux: false));
    }

    [Theory]
    [InlineData("primary-paste", MiddleClickAction.PrimaryPaste)]
    [InlineData("Primary_Paste", MiddleClickAction.PrimaryPaste)]
    [InlineData("clipboard-paste", MiddleClickAction.ClipboardPaste)]
    [InlineData("IGNORE", MiddleClickAction.Ignore)]
    [InlineData("bogus", MiddleClickAction.ClipboardPaste)]
    [InlineData(null, MiddleClickAction.ClipboardPaste)]
    public void ParseMiddleClick_IsForgiving(string? value, MiddleClickAction expected) =>
        Assert.Equal(expected, MouseOptions.ParseMiddleClick(value));

    [Fact]
    public void WheelAndBypassDefaults_MatchDecisionK4()
    {
        var mouse = new DottyUserConfig().Mouse;

        Assert.Equal(MouseInputSettings.Default, MouseOptions.ResolveInput(mouse));
        Assert.Equal(3.0, MouseInputSettings.Default.ScrollMultiplier);
        Assert.True(MouseInputSettings.Default.AlternateScroll);
        Assert.Equal(MouseReportBypassModifier.Shift, MouseInputSettings.Default.ReportBypassModifier);
    }

    [Fact]
    public void WheelKeys_RoundTripThroughJson()
    {
        DottyUserConfig config = JsonSerializer.Deserialize<DottyUserConfig>(
            "{\"mouse\":{\"scrollMultiplier\":1.5,\"alternateScroll\":\"off\",\"reportBypassModifier\":\"alt\"}}")!;

        Assert.Equal(
            new MouseInputSettings(1.5, false, MouseReportBypassModifier.Alt),
            MouseOptions.ResolveInput(config.Mouse));
    }

    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.01)]
    [InlineData(-4.0, 0.01)]
    [InlineData(1000.0, 100.0)]
    [InlineData(double.NaN, 3.0)]
    [InlineData(double.PositiveInfinity, 3.0)]
    public void ClampScrollMultiplier_BoundsAndRejectsNonFinite(double value, double expected) =>
        Assert.Equal(expected, MouseOptions.ClampScrollMultiplier(value));

    [Theory]
    [InlineData("auto", true)]
    [InlineData(" OFF ", false)]
    [InlineData("off", false)]
    [InlineData("bogus", true)]
    [InlineData(null, true)]
    public void ParseAlternateScroll_IsForgiving(string? value, bool expected) =>
        Assert.Equal(expected, MouseOptions.ParseAlternateScroll(value));

    [Theory]
    [InlineData("shift", MouseReportBypassModifier.Shift)]
    [InlineData("CTRL", MouseReportBypassModifier.Ctrl)]
    [InlineData("control", MouseReportBypassModifier.Ctrl)]
    [InlineData(" alt", MouseReportBypassModifier.Alt)]
    [InlineData("Super", MouseReportBypassModifier.Super)]
    [InlineData("none", MouseReportBypassModifier.None)]
    [InlineData("meta", MouseReportBypassModifier.Shift)]
    [InlineData(null, MouseReportBypassModifier.Shift)]
    public void ParseReportBypassModifier_IsForgiving(string? value, MouseReportBypassModifier expected) =>
        Assert.Equal(expected, MouseOptions.ParseReportBypassModifier(value));

    private sealed class FakeClipboard : ITerminalClipboard
    {
        public string? Text { get; set; }
        public int Sets { get; private set; }
        public string? GetText() => Text;
        public void SetText(string text) { Text = text; Sets++; }
        public bool HasText => !string.IsNullOrEmpty(Text);
    }

    [Theory]
    [InlineData(CopyOnSelectMode.None, false, false)]
    [InlineData(CopyOnSelectMode.Primary, true, false)]
    [InlineData(CopyOnSelectMode.Clipboard, false, true)]
    [InlineData(CopyOnSelectMode.Both, true, true)]
    public void CopyOnSelect_WritesOnlyChosenTargets(CopyOnSelectMode mode, bool primaryWritten, bool clipboardWritten)
    {
        var primary = new InProcessPrimarySelection();
        var clipboard = new FakeClipboard();

        CopyOnSelect.Apply(mode, "text", primary, clipboard);

        Assert.Equal(primaryWritten ? "text" : null, primary.Get());
        Assert.Equal(clipboardWritten ? 1 : 0, clipboard.Sets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CopyOnSelect_EmptyTextWritesNothing(string? text)
    {
        var primary = new InProcessPrimarySelection();
        var clipboard = new FakeClipboard();

        CopyOnSelect.Apply(CopyOnSelectMode.Both, text, primary, clipboard);

        Assert.Null(primary.Get());
        Assert.Equal(0, clipboard.Sets);
    }

    [Fact]
    public void CopyOnSelect_NullTargetsAreSkipped() =>
        CopyOnSelect.Apply(CopyOnSelectMode.Both, "x", null, null);

    [Fact]
    public void InProcessPrimarySelection_StoresLatestAndIsNotSystem()
    {
        var primary = new InProcessPrimarySelection();
        Assert.False(primary.IsSystemSelection);
        Assert.False(primary.SupportsCutBuffer0);
        Assert.Null(primary.Get());
        Assert.Null(primary.GetCutBuffer0());

        primary.Set("one");
        primary.Set("two");
        Assert.Equal("two", primary.Get());
        Assert.False(primary.SetCutBuffer0("unsupported"));
    }

    [Fact]
    public void PrimaryPaste_GoesThroughConfirmingRouter()
    {
        var primary = new InProcessPrimarySelection();
        primary.Set("a\nb");
        var pty = new SilentPty();
        var session = new TerminalSession(4, 20, () => pty);
        using var pane = new LeafPane(session);
        session.Start();
        ContextMenuModel? prompt = null;
        var config = new ClipboardUserConfig();
        var router = new ClipboardPasteRouter(
            new PrimarySelectionClipboard(primary),
            () => config,
            p => prompt = p,
            _ => true);

        router.Paste(pane);

        Assert.NotNull(prompt);
        Assert.Empty(pty.WrittenBytes());
    }

    [Fact]
    public void PrimaryPaste_SafeTextIsWrittenAndEmptyPrimaryPastesNothing()
    {
        var primary = new InProcessPrimarySelection();
        var pty = new SilentPty();
        var session = new TerminalSession(4, 20, () => pty);
        using var pane = new LeafPane(session);
        session.Start();
        var config = new ClipboardUserConfig();
        var router = new ClipboardPasteRouter(new PrimarySelectionClipboard(primary), () => config, _ => { }, _ => true);

        router.Paste(pane);
        Assert.Empty(pty.WrittenBytes());

        primary.Set("ls");
        router.Paste(pane);
        Assert.True(SpinWait.SpinUntil(
            () => Encoding.UTF8.GetString(pty.WrittenBytes()) == "ls", System.TimeSpan.FromSeconds(5)));
    }
}
