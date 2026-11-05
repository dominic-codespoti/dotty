using System;
using Dotty.Runtime.Clipboard;
using Dotty.Runtime.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Runtime.Panes;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk;

/// <summary>
/// Single funnel for every paste (Ctrl+Shift+V, middle click, context menu). Reads the clipboard, decides whether
/// the paste needs confirmation from the target pane's bracketed-paste state and the live clipboard config, and
/// either writes immediately or shows a prompt. The text previewed in the prompt is exactly what is pasted.
/// </summary>
public sealed class ClipboardPasteRouter
{
    private readonly ITerminalClipboard _clipboard;
    private readonly Func<ClipboardUserConfig> _config;
    private readonly Action<ContextMenuModel> _showPrompt;
    private readonly Func<LeafPane, bool> _isPaneAlive;

    /// <param name="config">Live config accessor (called at paste time and again at confirm time).</param>
    /// <param name="showPrompt">Installs the prompt as the active context menu.</param>
    /// <param name="isPaneAlive">Whether the pane is still part of the window when the user confirms.</param>
    public ClipboardPasteRouter(
        ITerminalClipboard clipboard,
        Func<ClipboardUserConfig> config,
        Action<ContextMenuModel> showPrompt,
        Func<LeafPane, bool> isPaneAlive)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _showPrompt = showPrompt ?? throw new ArgumentNullException(nameof(showPrompt));
        _isPaneAlive = isPaneAlive ?? throw new ArgumentNullException(nameof(isPaneAlive));
    }

    public void Paste(LeafPane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);

        PasteText(pane, _clipboard.GetText());
    }

    /// <summary>Pastes <paramref name="text"/> (e.g. dropped file paths) with the same confirmation rules as a clipboard paste.</summary>
    public void PasteText(LeafPane pane, string? text)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (string.IsNullOrEmpty(text))
            return;

        bool bracketed = pane.Session.Adapter.Buffer.BracketedPasteMode;
        ClipboardUserConfig config = _config();
        PasteRisk risk = PasteSafety.Evaluate(text);
        if (!PasteSafety.NeedsConfirmation(text, bracketed, config))
        {
            Send(pane, text);
            return;
        }

        PasteRisk shown = PasteSafety.Reasons(risk, bracketed, config.BracketedPasteIsSafe);
        _showPrompt(PastePromptBuilder.Build(text, shown, () => ConfirmedPaste(pane, text, risk, shown)));
    }

    private void ConfirmedPaste(LeafPane pane, string text, PasteRisk risk, PasteRisk shown)
    {
        if (!_isPaneAlive(pane))
            return;

        // The application may have flipped mode 2004 (or RIS cleared it) while the prompt was open. If that
        // exposes a risk the user was not shown, fail closed.
        bool bracketedNow = pane.Session.Adapter.Buffer.BracketedPasteMode;
        PasteRisk now = PasteSafety.Reasons(risk, bracketedNow, _config().BracketedPasteIsSafe);
        if ((now & ~shown) != PasteRisk.None)
            return;

        Send(pane, text);
    }

    private static void Send(LeafPane pane, string text) =>
        pane.Session.WriteUserInput(Encode(text, pane.Session.Adapter));

    public static byte[] Encode(string text, TerminalAdapter adapter) =>
        PasteEncoder.Encode(text, adapter.Buffer.BracketedPasteMode);
}
