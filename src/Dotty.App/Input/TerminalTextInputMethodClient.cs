using Avalonia;
using Avalonia.Input.TextInput;
using Dotty.App.Controls;

namespace Dotty.App.Input;

/// <summary>
/// <see cref="TextInputMethodClient"/> for terminal focus. Exposes the cursor
/// cell rectangle so the platform positions its candidate window, and displays
/// preedit text as an overlay at the terminal cursor. Surrounding-text exposure
/// is disabled (bounded per the roadmap); committed text arrives through the
/// normal <c>TextInput</c> event and is sent exactly once by the view.
/// </summary>
public sealed class TerminalTextInputMethodClient : TextInputMethodClient
{
    private readonly TerminalCanvas _canvas;
    private string? _preeditText;
    private int? _preeditCursor;

    public TerminalTextInputMethodClient(TerminalCanvas canvas)
    {
        _canvas = canvas;
    }

    public override Visual TextViewVisual => _canvas;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    /// <summary>
    /// The terminal cursor cell in the canvas's local coordinates; the platform
    /// anchors its candidate window here.
    /// </summary>
    public override Rect CursorRectangle => _canvas.GetCursorScreenRect();

    public override TextSelection Selection
    {
        get => new(_canvas.GetCursorCellOffset(), _canvas.GetCursorCellOffset());
        set { }
    }

    /// <summary>
    /// The active preedit text and its cursor offset, if composing.
    /// </summary>
    public string? PreeditText => _preeditText;
    public int? PreeditCursor => _preeditCursor;

    public override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

    public override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        _preeditText = preeditText;
        _preeditCursor = cursorPos;
        _canvas.SetPreedit(preeditText, cursorPos);
    }

    /// <summary>
    /// Called when the terminal cursor moves so the candidate window follows
    /// the cell.
    /// </summary>
    public void NotifyCursorMoved() => RaiseCursorRectangleChanged();

    /// <summary>
    /// Clears the active composition (used on focus/session changes and on
    /// platform reset requests).
    /// </summary>
    public void ResetComposition()
    {
        if (_preeditText == null)
        {
            return;
        }

        _preeditText = null;
        _preeditCursor = null;
        _canvas.SetPreedit(null, null);
    }
}
