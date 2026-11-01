using System.Globalization;

namespace Dotty.Runtime.Localization;

/// <summary>
/// Every user-visible chrome, menu and prompt string in one place (the seam for future localization and for
/// accessible names). <c>UiStringsGuardTests</c> fails when a literal label reappears in the menu and prompt builders.
/// </summary>
public static class UiStrings
{
    // Tab context menu
    public const string TabSplitRight = "Split Right";
    public const string TabSplitDown = "Split Down";
    public const string TabNew = "New Tab";
    public const string TabClose = "Close Tab";

    // Terminal context menu
    public const string Copy = "Copy";
    public const string CopyCommandOutput = "Copy Command Output";
    public const string PreviousPrompt = "Previous Prompt";
    public const string NextPrompt = "Next Prompt";
    public const string Paste = "Paste";
    public const string SelectAll = "Select All";
    public const string SplitPaneRight = "Split Pane Right";
    public const string SplitPaneDown = "Split Pane Down";
    public const string ClearBuffer = "Clear Buffer";

    // Shared prompt actions
    public const string Cancel = "Cancel";
    public const string Dismiss = "Dismiss";
    public const string Allow = "Allow";
    public const string Deny = "Deny";

    // Paste confirmation
    public const string PasteReasonMultiline = "Multiple lines: the shell would run them immediately.";
    public const string PasteReasonControl = "Control characters will be replaced by spaces.";
    public const string PasteReasonEndMarker = "Contains a paste-end marker (possible injection).";

    public static string PasteTitle(int lineCount, int characters) => string.Create(CultureInfo.InvariantCulture,
        $"Paste {lineCount:N0} {(lineCount == 1 ? "line" : "lines")}, {characters:N0} characters?");

    // Clipboard read (OSC 52) prompt
    public const string ClipboardReadUnnamedTab = "A tab wants to read the clipboard";
    public static string ClipboardReadNamedTab(string name) => "Tab \"" + name + "\" wants to read the clipboard";
    public const string ClipboardEmpty = "(clipboard is empty)";
    public static string CharacterCount(int count) =>
        count == 1 ? "(1 character)" : "(" + count.ToString(CultureInfo.InvariantCulture) + " characters)";

    // Hyperlink prompt
    public const string OpenLinkTitle = "Open link?";
    public const string OpenLink = "Open";
    public const string CopyLinkAddress = "Copy link address";
    public static string LinkNonWeb(string scheme) => "Non-web link (" + scheme + ")";
    public const string LinkLocalFile = "Opens a local file";
    public const string LinkCredentials = "URL contains credentials";
    public const string LinkNonAsciiHost = "Host contains non-ASCII characters";
    public const string LinkTextDiffers = "Link text differs from the target:";

    // Render failure prompt
    public const string RenderFailureTitle = "Dotty could not draw this window";
    public const string RenderFailureShellsRunning = "Your shells are still running.";
    public const string RenderFailureDetails = "Details were written to stderr.";
    public const string TryAgain = "Try again";
    public const string QuitDotty = "Quit Dotty";
    public static string RenderFailureOpenGl(string status) => "OpenGL " + status;

    // Window chrome
    public const string ContextLostWindowTitle = "Dotty \u2014 GPU reset, rendering stopped; restart Dotty";
}
