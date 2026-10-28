using System;
using System.Linq;
using System.Text;
using System.Threading;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Prompt;
using Dotty.Runtime.Sessions;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ClipboardPasteRouterTests : IDisposable
{
    private sealed class FakeClipboard : ITerminalClipboard
    {
        public string? Text { get; set; }
        public string? GetText() => Text;
        public void SetText(string text) => Text = text;
        public bool HasText => !string.IsNullOrEmpty(Text);
    }

    private readonly SilentPty _pty = new();
    private readonly TerminalSession _session;
    private readonly LeafPane _pane;
    private readonly FakeClipboard _clipboard = new();
    private readonly ClipboardUserConfig _config = new();
    private ContextMenuModel? _prompt;
    private int _promptCount;
    private bool _alive = true;
    private readonly ClipboardPasteRouter _router;

    public ClipboardPasteRouterTests()
    {
        _session = new TerminalSession(4, 20, () => _pty);
        _pane = new LeafPane(_session);
        _session.Start();
        _router = new ClipboardPasteRouter(
            _clipboard,
            () => _config,
            prompt => { _prompt = prompt; _promptCount++; },
            _ => _alive);
    }

    public void Dispose() => _pane.Dispose();

    private int _drains;

    /// <summary>Writes a sentinel and waits for it so earlier (ordered) writes are known to have landed.</summary>
    private string Drain()
    {
        int expected = ++_drains;
        _session.WriteInput("Z"u8);
        Assert.True(SpinWait.SpinUntil(() => _pty.WrittenBytes().Count(b => b == (byte)'Z') >= expected,
            TimeSpan.FromSeconds(5)));
        return Encoding.UTF8.GetString(_pty.WrittenBytes()).Replace("Z", string.Empty);
    }

    private void Invoke(string label)
    {
        int index = -1;
        for (int i = 0; i < _prompt!.Items.Count; i++)
        {
            if (_prompt.Items[i].Label == label && !_prompt.Items[i].IsDisabled)
                index = i;
        }

        Assert.True(index >= 0);
        _prompt.HoveredIndex = index;
        Assert.True(_prompt.ExecuteFocused());
    }

    [Fact]
    public void SafeText_IsWrittenWithoutPrompt()
    {
        _clipboard.Text = "ls -la";
        _router.Paste(_pane);

        Assert.Equal("ls -la", Drain());
        Assert.Equal(0, _promptCount);
    }

    [Fact]
    public void Multiline_Unbracketed_PromptsAndWritesNothingUntilPaste()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);

        Assert.Equal(1, _promptCount);
        Assert.Equal("Cancel", _prompt!.Items[_prompt.HoveredIndex].Label);
        Assert.Equal("Paste", _prompt.Items[^2].Label);
        Assert.Equal("Cancel", _prompt.Items[^1].Label);
        Assert.Equal(string.Empty, Drain());

        Invoke("Paste");
        Assert.Equal("a\rb", Drain());
        Assert.False(_prompt.ExecuteFocused());
    }

    [Fact]
    public void Cancel_WritesNothing()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);

        Invoke("Cancel");

        Assert.Equal(string.Empty, Drain());
    }

    [Fact]
    public void Dismiss_WritesNothing()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);

        _prompt!.Close();

        Assert.Equal(string.Empty, Drain());
    }

    [Fact]
    public void ConfirmPasteOff_WritesImmediatelyAndStillSanitises()
    {
        _config.ConfirmPaste = false;
        _clipboard.Text = "a\nb\x1b";
        _router.Paste(_pane);

        Assert.Equal(0, _promptCount);
        Assert.Equal("a\rb ", Drain());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BracketedMultiline_PromptsOnlyWhenNotDeclaredSafe(bool safe, bool expectPrompt)
    {
        _config.BracketedPasteIsSafe = safe;
        _session.Adapter.OnSetBracketedPasteMode(true);
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);

        Assert.Equal(expectPrompt ? 1 : 0, _promptCount);
        string written = Drain();
        Assert.Equal(expectPrompt ? string.Empty : "\x1b[200~a\nb\x1b[201~", written);
    }

    [Fact]
    public void BracketedEndMarker_AlwaysPrompts_AndWritesSingleEndMarker()
    {
        _config.BracketedPasteIsSafe = true;
        _session.Adapter.OnSetBracketedPasteMode(true);
        _clipboard.Text = "a\x1b[201~touch x";
        _router.Paste(_pane);

        Assert.Equal(1, _promptCount);
        Assert.Equal(string.Empty, Drain());

        Invoke("Paste");
        string written = Drain();
        Assert.Equal(written.IndexOf("\x1b[201~", StringComparison.Ordinal), written.LastIndexOf("\x1b[201~", StringComparison.Ordinal));
        Assert.EndsWith("\x1b[201~", written);
    }

    [Fact]
    public void ModeEnabledWhilePromptOpen_ReasonsShrink_PasteProceedsBracketed()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);

        _session.Adapter.OnSetBracketedPasteMode(true);
        Invoke("Paste");

        Assert.Equal("\x1b[200~a\nb\x1b[201~", Drain());
    }

    [Fact]
    public void ModeDisabledWhilePromptOpen_NewRiskNotShown_DropsPaste()
    {
        _config.BracketedPasteIsSafe = true;
        _session.Adapter.OnSetBracketedPasteMode(true);
        _clipboard.Text = "a\n\x1b[201~b";
        _router.Paste(_pane);

        // Shown reasons exclude Newlines (bracketed + safe); after RIS-like reset they would apply.
        _session.Adapter.OnSetBracketedPasteMode(false);
        Invoke("Paste");

        Assert.Equal(string.Empty, Drain());
    }

    [Fact]
    public void ClipboardChangedAfterPrompt_PastesPreviewedText()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);
        _clipboard.Text = "evil\n";

        Invoke("Paste");

        Assert.Equal("a\rb", Drain());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyClipboard_DoesNothing(string? text)
    {
        _clipboard.Text = text;
        _router.Paste(_pane);

        Assert.Equal(0, _promptCount);
        Assert.Equal(string.Empty, Drain());
    }

    [Fact]
    public void PaneGoneBeforeConfirm_WritesNothing()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);
        _alive = false;

        Invoke("Paste");

        Assert.Equal(string.Empty, Drain());
    }

    [Fact]
    public void SessionDisposedBeforeConfirm_DoesNotThrow()
    {
        _clipboard.Text = "a\nb";
        _router.Paste(_pane);
        _session.Dispose();

        Invoke("Paste");

        Assert.Empty(_pty.WrittenBytes());
    }

    [Fact]
    public void HugeClipboard_PromptStaysWithinLimits()
    {
        _clipboard.Text = string.Join('\n', Enumerable.Repeat(new string('y', 500), 2000)) + "\n";
        _router.Paste(_pane);

        ContextMenuModel prompt = _prompt!;
        ContextMenuItem[] body = prompt.Items.Where(i => i.Id.StartsWith(HostPrompt.LineIdPrefix)).ToArray();
        Assert.InRange(body.Length, 1, HostPrompt.MaxLines);
        Assert.All(prompt.Items, i => Assert.True(i.Label.Length <= HostPrompt.MaxLineChars));
    }
}
