using System;
using System.Text;
using Dotty.Abstractions.Clipboard;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Dotty.Runtime.Tabs;

namespace Dotty.Silk;

/// <summary>Window-side capabilities <see cref="ClipboardReadService"/> needs; implemented by the host.</summary>
internal interface IClipboardReadEnvironment
{
    /// <summary>False once the window is closing or has no keyboard/clipboard backend. Safe to read from any thread.</summary>
    bool IsAvailable { get; }

    /// <summary>True unless the window is known to be unfocused. Main thread.</summary>
    bool IsWindowFocused { get; }

    /// <summary>True while any context menu or prompt is on screen. Main thread.</summary>
    bool IsMenuVisible { get; }

    /// <summary>Finds the tab and pane that currently host <paramref name="session"/>. Main thread.</summary>
    bool TryResolveOwner(TerminalSession session, out TerminalTab tab, out LeafPane pane);

    /// <summary>True when <paramref name="tab"/> is the tab being shown. Main thread.</summary>
    bool IsActiveTab(TerminalTab tab);

    /// <summary>Runs the Lua <c>clipboard_read</c> hook; false vetoes the request. Main thread.</summary>
    bool AllowRead(LeafPane pane, TerminalTab tab);

    /// <summary>Reads the system clipboard. Null means unavailable/busy; an empty string is a genuinely empty clipboard.</summary>
    string? ReadClipboard();

    /// <summary>Sends reply bytes to the session's application.</summary>
    void WriteReply(TerminalSession session, byte[] reply);

    /// <summary>Centers and shows a modal prompt.</summary>
    void ShowPrompt(ContextMenuModel prompt);

    /// <summary>Wakes the main loop so queued requests are drained. Any thread.</summary>
    void Wake();
}

/// <summary>
/// Policy and flow for OSC 52 clipboard reads. <see cref="OnRequested"/> runs on PTY consumer threads and must
/// never block; everything else runs on the UI thread. Denials (policy, Lua veto, user, oversize, unavailable
/// clipboard, expiry) never produce a reply; only a genuinely empty clipboard answers with an empty payload.
/// </summary>
internal sealed class ClipboardReadService
{
    private readonly IClipboardReadEnvironment _environment;
    private readonly Func<ClipboardUserConfig> _config;
    private readonly ClipboardReadQueue _queue;
    private readonly Func<TerminalSession, bool> _isOnActiveTab;

    internal ClipboardReadService(
        IClipboardReadEnvironment environment,
        Func<ClipboardUserConfig> config,
        Func<long> nowMs)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _queue = new ClipboardReadQueue(nowMs);
        _isOnActiveTab = IsOnActiveTab;
    }

    internal ClipboardReadQueue Queue => _queue;

    /// <summary>
    /// Handles an OSC 52 query from <paramref name="session"/>. Only the clipboard selection is served: primary
    /// selection needs an X11 helper that does not exist yet, so primary-only requests are dropped. Under
    /// <c>deny</c> (the default) this returns before touching the queue.
    /// </summary>
    internal void OnRequested(TerminalSession session, ClipboardReadRequest request)
    {
        if ((request.Selection & ClipboardSelection.Clipboard) == 0)
            return;
        if (ClipboardReadModes.Parse(_config().Osc52Read) == ClipboardReadMode.Deny)
            return;
        if (!_environment.IsAvailable)
            return;

        if (_queue.TryEnqueue(session, ClipboardSelection.Clipboard))
            _environment.Wake();
    }

    /// <summary>
    /// Serves queued requests on the UI thread. Requests wait while the window is unfocused, while their tab is
    /// not the active one, and (for <c>ask</c>) while another menu or prompt is showing.
    /// </summary>
    internal void Drain()
    {
        if (_queue.Count == 0)
            return;

        while (_environment.IsAvailable)
        {
            ClipboardUserConfig config = _config();
            ClipboardReadMode mode = ClipboardReadModes.Parse(config.Osc52Read);
            if (mode == ClipboardReadMode.Deny)
            {
                // The policy was tightened while requests were waiting.
                _queue.Clear();
                return;
            }

            if (!_environment.IsWindowFocused)
                return;
            if (mode == ClipboardReadMode.Ask && _environment.IsMenuVisible)
                return;
            if (!_queue.TryDequeue(_isOnActiveTab, out PendingClipboardRead pending))
                return;

            Serve(pending, mode, config.Osc52ReadLimitBytes);
        }
    }

    /// <summary>Forgets requests from a session that left the window.</summary>
    internal void RemoveSession(TerminalSession session) => _queue.RemoveSession(session);

    /// <summary>Forgets every request (window closing).</summary>
    internal void Clear() => _queue.Clear();

    private bool IsOnActiveTab(TerminalSession session) =>
        _environment.TryResolveOwner(session, out TerminalTab tab, out _) && _environment.IsActiveTab(tab);

    private void Serve(PendingClipboardRead pending, ClipboardReadMode mode, int limitBytes)
    {
        TerminalSession session = pending.Session;
        if (!_environment.TryResolveOwner(session, out TerminalTab tab, out LeafPane pane))
            return;
        if (!_environment.AllowRead(pane, tab))
            return;

        // Snapshot once: the text the user is shown is exactly the text that is sent.
        string? text = _environment.ReadClipboard();
        if (text is null)
            return;
        // UTF-8 needs at least one byte per UTF-16 unit, so an oversize unit count rules the clipboard out cheaply.
        if (text.Length > limitBytes || Encoding.UTF8.GetByteCount(text) > limitBytes)
            return;

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (!Osc52Reply.TryEncode(pending.Selection, utf8, out byte[] reply))
            return;

        if (mode == ClipboardReadMode.Allow)
        {
            _environment.WriteReply(session, reply);
            return;
        }

        IClipboardReadEnvironment environment = _environment;
        Func<ClipboardUserConfig> config = _config;
        _environment.ShowPrompt(ClipboardReadPromptBuilder.Build(
            tab.Title,
            text,
            allow: () =>
            {
                // The prompt can stay open for a long time: honour a policy tightened to deny, and never answer a
                // session that has left the window meanwhile.
                if (ClipboardReadModes.Parse(config().Osc52Read) == ClipboardReadMode.Deny)
                    return;
                if (!environment.TryResolveOwner(session, out _, out _))
                    return;
                environment.WriteReply(session, reply);
            }));
    }
}
