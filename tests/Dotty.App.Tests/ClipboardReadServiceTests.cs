using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Clipboard;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Tabs;
using Dotty.Silk;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ClipboardReadServiceTests : IDisposable
{
    private const string Esc = "\u001b";
    private const string St = "\u001b\\";

    private sealed class FakeEnvironment : IClipboardReadEnvironment
    {
        public readonly List<TerminalTab> Tabs = new();
        public readonly HashSet<TerminalTab> ActiveTabs = new();
        public readonly List<(TerminalSession Session, byte[] Reply)> Replies = new();
        public readonly List<ContextMenuModel> Prompts = new();
        public bool Available = true;
        public bool Focused = true;
        public bool MenuVisible;
        public bool HookAllows = true;
        public string? Clipboard = "clip";
        public bool PrimarySupported;
        public string? Primary = "prim";
        public int PrimaryReads;
        public readonly List<ClipboardSelection> HookSelections = new();
        public int ClipboardReads;
        public int HookCalls;
        public int Wakes;

        public bool IsAvailable => Available;
        public bool IsWindowFocused => Focused;
        public bool IsMenuVisible => MenuVisible;

        public bool TryResolveOwner(TerminalSession session, out TerminalTab tab, out LeafPane pane)
        {
            foreach (TerminalTab candidate in Tabs)
            {
                if (!ReferenceEquals(candidate.Session, session))
                    continue;
                tab = candidate;
                pane = candidate.ActivePane;
                return true;
            }

            tab = null!;
            pane = null!;
            return false;
        }

        public bool IsActiveTab(TerminalTab tab) => ActiveTabs.Contains(tab);

        public bool AllowRead(LeafPane pane, TerminalTab tab, ClipboardSelection selection)
        {
            HookCalls++;
            HookSelections.Add(selection);
            return HookAllows;
        }

        public bool SupportsPrimarySelection => PrimarySupported;

        public string? ReadPrimary()
        {
            PrimaryReads++;
            return Primary;
        }

        public string? ReadClipboard()
        {
            ClipboardReads++;
            return Clipboard;
        }

        public void WriteReply(TerminalSession session, byte[] reply) => Replies.Add((session, reply));

        public void ShowPrompt(ContextMenuModel prompt)
        {
            Prompts.Add(prompt);
            MenuVisible = true;
        }

        public void Wake() => Wakes++;
    }

    private readonly FakeEnvironment _env = new();
    private readonly ClipboardUserConfig _config = new();
    private readonly List<TerminalTab> _allTabs = new();
    private readonly ClipboardReadService _service;
    private long _now = 1_000;

    public ClipboardReadServiceTests()
    {
        _service = new ClipboardReadService(_env, () => _config, () => _now);
    }

    public void Dispose()
    {
        foreach (TerminalTab tab in _allTabs)
            tab.Dispose();
    }

    private TerminalTab AddTab(string title = "shell", bool active = true)
    {
        var tab = new TerminalTab(title);
        _allTabs.Add(tab);
        _env.Tabs.Add(tab);
        if (active)
            _env.ActiveTabs.Add(tab);
        return tab;
    }

    private static string Text(byte[] reply) => Encoding.ASCII.GetString(reply);

    private static string ExpectedReply(string clipboardText) =>
        Esc + "]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(clipboardText)) + St;

    private void Request(TerminalTab tab, ClipboardSelection selection = ClipboardSelection.Clipboard) =>
        _service.OnRequested(tab.Session, new ClipboardReadRequest(selection));

    [Fact]
    public void DenyByDefault_IgnoresRequestsWithoutQueueingOrWaking()
    {
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Equal(0, _service.Queue.Count);
        Assert.Equal(0, _env.Wakes);
        Assert.Equal(0, _env.ClipboardReads);
        Assert.Equal(0, _env.HookCalls);
        Assert.Empty(_env.Replies);
        Assert.Empty(_env.Prompts);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("DENY")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("true")]
    [InlineData(null)]
    public void DenyAndUnknownModes_FailClosed(string? mode)
    {
        _config.Osc52Read = mode!;
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Equal(0, _service.Queue.Count);
        Assert.Equal(0, _env.ClipboardReads);
        Assert.Empty(_env.Replies);
        Assert.Empty(_env.Prompts);
    }

    [Fact]
    public void Allow_RepliesImmediatelyWithoutPrompt()
    {
        _config.Osc52Read = "allow";
        _env.Clipboard = "h\u00e9llo \u2713";
        TerminalTab tab = AddTab();

        Request(tab);
        Assert.Equal(1, _env.Wakes);
        _service.Drain();

        Assert.Empty(_env.Prompts);
        (TerminalSession session, byte[] reply) = Assert.Single(_env.Replies);
        Assert.Same(tab.Session, session);
        Assert.Equal(ExpectedReply("h\u00e9llo \u2713"), Text(reply));
        Assert.Equal(0, _service.Queue.Count);
    }

    [Fact]
    public void Ask_ShowsPromptWithSanitizedSnapshotAndSendsNothingUntilAllowed()
    {
        _config.Osc52Read = "ask";
        _env.Clipboard = "line one\u001b]0;x\u0007\nline two";
        TerminalTab tab = AddTab("build");

        Request(tab);
        _service.Drain();

        Assert.Empty(_env.Replies);
        ContextMenuModel prompt = Assert.Single(_env.Prompts);
        Assert.Equal("Tab \"build\" wants to read the clipboard", prompt.Items[0].Label);
        Assert.Equal("Deny", prompt.Items[prompt.HoveredIndex].Label);
        Assert.All(prompt.Items, item => Assert.False(item.Label.Any(c => c < 0x20 || c == 0x7F)));
        Assert.Contains(prompt.Items, item => item.Label == "line one\\x1B]0;x\\x07");
        Assert.Contains(prompt.Items, item => item.Label == "line two");
    }

    [Fact]
    public void Ask_AllowSendsTheSnapshotThatWasPreviewedNotTheCurrentClipboard()
    {
        _config.Osc52Read = "ask";
        _env.Clipboard = "first";
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();
        ContextMenuModel prompt = Assert.Single(_env.Prompts);
        _env.Clipboard = "second";

        Assert.True(prompt.MoveFocus(1));
        Assert.Equal("Allow", prompt.Items[prompt.HoveredIndex].Label);
        Assert.True(prompt.ExecuteFocused());

        (TerminalSession session, byte[] reply) = Assert.Single(_env.Replies);
        Assert.Same(tab.Session, session);
        Assert.Equal(ExpectedReply("first"), Text(reply));
        Assert.Equal(1, _env.ClipboardReads);
    }

    [Fact]
    public void Ask_DenyEscapeAndReplacementSendNothing()
    {
        _config.Osc52Read = "ask";
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();
        ContextMenuModel denied = _env.Prompts[0];
        Assert.True(denied.ExecuteFocused());

        _env.MenuVisible = false;
        _service.Drain();
        Request(tab);
        _service.Drain();
        ContextMenuModel escaped = _env.Prompts[1];
        escaped.Close();

        _env.MenuVisible = false;
        Request(tab);
        _service.Drain();
        ContextMenuModel replaced = _env.Prompts[2];
        replaced.Close();

        Assert.Empty(_env.Replies);
        Assert.Equal(3, _env.Prompts.Count);
    }

    [Fact]
    public void Ask_SecondRequestStaysQueuedWhileAPromptIsOpenThenIsServed()
    {
        _config.Osc52Read = "ask";
        TerminalTab first = AddTab("one");
        TerminalTab second = AddTab("two");

        Request(first);
        Request(second);
        _service.Drain();

        Assert.Single(_env.Prompts);
        Assert.Equal(1, _service.Queue.Count);
        _service.Drain();
        Assert.Single(_env.Prompts);

        // The user dismisses the first prompt; the next drain shows the second.
        _env.Prompts[0].Close();
        _env.MenuVisible = false;
        _service.Drain();

        Assert.Equal(2, _env.Prompts.Count);
        Assert.Equal("Tab \"two\" wants to read the clipboard", _env.Prompts[1].Items[0].Label);
        Assert.Equal(0, _service.Queue.Count);
        Assert.Empty(_env.Replies);
    }

    [Fact]
    public void Allow_IgnoresAnOpenMenuBecauseItNeedsNoUi()
    {
        _config.Osc52Read = "allow";
        _env.MenuVisible = true;
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Single(_env.Replies);
    }

    [Fact]
    public void OverLimitClipboard_IsDroppedSilently()
    {
        _config.Osc52Read = "allow";
        _config.Osc52ReadLimitBytes = 5;
        _env.Clipboard = "hello!";
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Empty(_env.Replies);
        Assert.Empty(_env.Prompts);
        Assert.Equal(0, _service.Queue.Count);
    }

    [Fact]
    public void LimitCountsUtf8BytesNotCharacters()
    {
        _config.Osc52Read = "allow";
        _config.Osc52ReadLimitBytes = 5;
        _env.Clipboard = "\u00e9\u00e9\u00e9";
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();
        Assert.Empty(_env.Replies);

        _env.Clipboard = "\u00e9\u00e9";
        Request(tab);
        _service.Drain();
        Assert.Single(_env.Replies);
    }

    [Fact]
    public void ClipboardExactlyAtTheLimit_IsServed()
    {
        _config.Osc52Read = "allow";
        _config.Osc52ReadLimitBytes = 5;
        _env.Clipboard = "hello";
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Equal(ExpectedReply("hello"), Text(Assert.Single(_env.Replies).Reply));
    }

    [Fact]
    public void OverLimitClipboard_NeverReachesAPrompt()
    {
        _config.Osc52Read = "ask";
        _config.Osc52ReadLimitBytes = 3;
        _env.Clipboard = "secret";
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Empty(_env.Prompts);
        Assert.Empty(_env.Replies);
    }

    [Fact]
    public void UnavailableClipboard_SendsNoReply()
    {
        _config.Osc52Read = "allow";
        _env.Clipboard = null;
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Equal(1, _env.ClipboardReads);
        Assert.Empty(_env.Replies);
        Assert.Empty(_env.Prompts);
        Assert.Equal(0, _service.Queue.Count);
    }

    [Fact]
    public void GenuinelyEmptyClipboard_RepliesWithAnEmptyPayload()
    {
        _config.Osc52Read = "allow";
        _env.Clipboard = string.Empty;
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        Assert.Equal(Esc + "]52;c;" + St, Text(Assert.Single(_env.Replies).Reply));
    }

    [Fact]
    public void Ask_EmptyClipboardStillPromptsAndAllowRepliesEmpty()
    {
        _config.Osc52Read = "ask";
        _env.Clipboard = string.Empty;
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();

        ContextMenuModel prompt = Assert.Single(_env.Prompts);
        Assert.Contains(prompt.Items, item => item.Label == "(clipboard is empty)");
        Assert.True(prompt.MoveFocus(1));
        Assert.True(prompt.ExecuteFocused());
        Assert.Equal(Esc + "]52;c;" + St, Text(Assert.Single(_env.Replies).Reply));
    }

    [Fact]
    public void UnfocusedWindow_WaitsUntilFocusReturns()
    {
        _config.Osc52Read = "allow";
        _env.Focused = false;
        TerminalTab tab = AddTab();

        Request(tab);
        _service.Drain();
        Assert.Empty(_env.Replies);
        Assert.Equal(0, _env.ClipboardReads);
        Assert.Equal(1, _service.Queue.Count);

        _env.Focused = true;
        _service.Drain();
        Assert.Single(_env.Replies);
    }

    [Fact]
    public void InactiveTab_WaitsForActivationAndExpiresAfterThirtySeconds()
    {
        _config.Osc52Read = "allow";
        TerminalTab background = AddTab("bg", active: false);
        TerminalTab foreground = AddTab("fg");

        Request(background);
        _service.Drain();
        Assert.Empty(_env.Replies);
        Assert.Equal(1, _service.Queue.Count);

        _env.ActiveTabs.Clear();
        _env.ActiveTabs.Add(background);
        _service.Drain();
        (TerminalSession served, _) = Assert.Single(_env.Replies);
        Assert.Same(background.Session, served);

        // A request that never gets its tab shown is dropped silently.
        _env.Replies.Clear();
        _env.ActiveTabs.Clear();
        _env.ActiveTabs.Add(foreground);
        Request(background);
        _now += ClipboardReadQueue.ExpiryMs + 1;
        _env.ActiveTabs.Clear();
        _env.ActiveTabs.Add(background);
        _service.Drain();
        Assert.Empty(_env.Replies);
        Assert.Equal(0, _service.Queue.Count);
    }

    [Fact]
    public void InactiveTabRequest_DoesNotBlockTheActiveTabsRequest()
    {
        _config.Osc52Read = "allow";
        TerminalTab background = AddTab("bg", active: false);
        TerminalTab foreground = AddTab("fg");

        Request(background);
        Request(foreground);
        _service.Drain();

        (TerminalSession served, _) = Assert.Single(_env.Replies);
        Assert.Same(foreground.Session, served);
        Assert.Equal(1, _service.Queue.Count);
    }

    [Fact]
    public void LuaVeto_BlocksBothModesBeforeTouchingTheClipboard()
    {
        _env.HookAllows = false;
        foreach (string mode in new[] { "allow", "ask" })
        {
            _config.Osc52Read = mode;
            TerminalTab tab = AddTab();

            Request(tab);
            _service.Drain();
        }

        Assert.Equal(2, _env.HookCalls);
        Assert.Equal(0, _env.ClipboardReads);
        Assert.Empty(_env.Replies);
        Assert.Empty(_env.Prompts);
    }

    [Fact]
    public void PrimaryOnlyRequest_IsDroppedAndClipboardPlusPrimaryIsServedAsClipboard()
    {
        _config.Osc52Read = "allow";
        TerminalTab tab = AddTab();

        Request(tab, ClipboardSelection.Primary);
        _service.Drain();
        Assert.Equal(0, _service.Queue.Count);
        Assert.Empty(_env.Replies);

        Request(tab, ClipboardSelection.Clipboard | ClipboardSelection.Primary);
        _service.Drain();
        Assert.Equal(ExpectedReply("clip"), Text(Assert.Single(_env.Replies).Reply));
    }

    [Fact]
    public void PrimaryRequest_OnSystemPrimaryPlatformIsServedWithPrimaryReply()
    {
        _config.Osc52Read = "allow";
        _env.PrimarySupported = true;
        TerminalTab tab = AddTab();

        Request(tab, ClipboardSelection.Primary);
        _service.Drain();

        Assert.Equal(Esc + "]52;p;" + Convert.ToBase64String(Encoding.UTF8.GetBytes("prim")) + St,
            Text(Assert.Single(_env.Replies).Reply));
        Assert.Equal(0, _env.ClipboardReads);
        Assert.Equal(1, _env.PrimaryReads);
        Assert.Equal(ClipboardSelection.Primary, Assert.Single(_env.HookSelections));
    }

    [Fact]
    public void ClipboardPlusPrimary_OnSystemPrimaryPlatformStillServesClipboard()
    {
        _config.Osc52Read = "allow";
        _env.PrimarySupported = true;
        TerminalTab tab = AddTab();

        Request(tab, ClipboardSelection.Clipboard | ClipboardSelection.Primary);
        _service.Drain();

        Assert.Equal(ExpectedReply("clip"), Text(Assert.Single(_env.Replies).Reply));
        Assert.Equal(0, _env.PrimaryReads);
    }

    [Fact]
    public void PrimaryRequest_WithNoPrimaryText_SendsNothing()
    {
        _config.Osc52Read = "allow";
        _env.PrimarySupported = true;
        _env.Primary = null;
        TerminalTab tab = AddTab();

        Request(tab, ClipboardSelection.Primary);
        _service.Drain();

        Assert.Empty(_env.Replies);
    }

    [Fact]
    public void TighteningPolicyWhileQueued_DropsPendingRequests()
    {
        _config.Osc52Read = "ask";
        _env.Focused = false;
        TerminalTab tab = AddTab();
        Request(tab);
        Assert.Equal(1, _service.Queue.Count);

        _config.Osc52Read = "deny";
        _env.Focused = true;
        _service.Drain();

        Assert.Equal(0, _service.Queue.Count);
        Assert.Equal(0, _env.ClipboardReads);
        Assert.Empty(_env.Replies);
        Assert.Empty(_env.Prompts);
    }

    [Fact]
    public void RemovedSession_LosesItsPendingRequest()
    {
        _config.Osc52Read = "allow";
        _env.Focused = false;
        TerminalTab tab = AddTab();
        Request(tab);

        _service.RemoveSession(tab.Session);
        _env.Focused = true;
        _service.Drain();

        Assert.Equal(0, _service.Queue.Count);
        Assert.Empty(_env.Replies);
    }

    [Fact]
    public void SessionThatLeftTheWindow_IsNeverAnswered()
    {
        _config.Osc52Read = "allow";
        _env.Focused = false;
        TerminalTab tab = AddTab();
        Request(tab);

        _env.Tabs.Clear();
        _env.Focused = true;
        _service.Drain();

        Assert.Empty(_env.Replies);
        Assert.Equal(0, _env.ClipboardReads);
    }

    [Fact]
    public void ClosedWindow_IgnoresRequestsAndStopsDraining()
    {
        _config.Osc52Read = "allow";
        TerminalTab tab = AddTab();
        _env.Focused = false;
        Request(tab);
        Assert.Equal(1, _service.Queue.Count);

        _env.Available = false;
        _env.Focused = true;
        _service.Drain();
        Assert.Empty(_env.Replies);

        Request(AddTab());
        Assert.Equal(1, _service.Queue.Count);

        _service.Clear();
        Assert.Equal(0, _service.Queue.Count);
    }

    [Fact]
    public void OneRequestPerSession_FloodIsCollapsed()
    {
        _config.Osc52Read = "allow";
        TerminalTab tab = AddTab();

        for (int i = 0; i < 20; i++)
            Request(tab);
        _service.Drain();

        Assert.Single(_env.Replies);
        Assert.Equal(1, _env.Wakes);
    }

    [Fact]
    public void ParserQuery_FlowsThroughSessionEventIntoTheService()
    {
        _config.Osc52Read = "allow";
        TerminalTab tab = AddTab();
        tab.Session.ClipboardReadRequested += request => _service.OnRequested(tab.Session, request);

        tab.Session.Parser.Feed(Encoding.UTF8.GetBytes("\u001b]52;c;?\u0007"));
        _service.Drain();

        Assert.Equal(ExpectedReply("clip"), Text(Assert.Single(_env.Replies).Reply));
    }

    [Fact]
    public void ParserQueries_WithPrimaryOnlyOrUnderDenyProduceNoReply()
    {
        TerminalTab tab = AddTab();
        tab.Session.ClipboardReadRequested += request => _service.OnRequested(tab.Session, request);

        // Default policy is deny.
        tab.Session.Parser.Feed(Encoding.UTF8.GetBytes("\u001b]52;c;?\u001b\\"));
        _service.Drain();
        Assert.Equal(0, _service.Queue.Count);

        // Primary is not served until an X11 helper exists, even when reads are allowed.
        _config.Osc52Read = "allow";
        tab.Session.Parser.Feed(Encoding.UTF8.GetBytes("\u001b]52;p;?\u0007"));
        _service.Drain();

        Assert.Empty(_env.Replies);
        Assert.Equal(0, _env.ClipboardReads);
    }

    private ContextMenuModel ShowAskPrompt(TerminalTab tab)
    {
        _config.Osc52Read = "ask";
        Request(tab);
        _service.Drain();
        ContextMenuModel prompt = Assert.Single(_env.Prompts);
        Assert.True(prompt.MoveFocus(1));
        Assert.Equal("Allow", prompt.Items[prompt.HoveredIndex].Label);
        return prompt;
    }

    [Fact]
    public void Ask_PolicyTightenedToDenyWhilePromptOpen_AllowSendsNothing()
    {
        TerminalTab tab = AddTab();
        ContextMenuModel prompt = ShowAskPrompt(tab);

        _config.Osc52Read = "deny";
        Assert.True(prompt.ExecuteFocused());

        Assert.Empty(_env.Replies);
    }

    [Fact]
    public void Ask_SessionClosedWhilePromptOpen_AllowSendsNothing()
    {
        TerminalTab tab = AddTab();
        ContextMenuModel prompt = ShowAskPrompt(tab);

        _env.Tabs.Clear();
        Assert.True(prompt.ExecuteFocused());

        Assert.Empty(_env.Replies);
    }
}
