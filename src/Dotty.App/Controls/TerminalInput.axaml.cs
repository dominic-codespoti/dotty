using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Dotty.App.Controls
{
    public partial class TerminalInput : UserControl
    {
    private TextBox? _inputBox;
    private TerminalPromptRenderer? _promptRenderer;
    private string _promptPlain = string.Empty;
    // When true, changes to the TextBox.Text are coming from code
    // and should not be forwarded as RawTextInput events.
    private bool _suppressTextChanged = false;

    public event EventHandler<string?>? Submitted;
    // Raw text/keys typed by the user. The handler receives the literal string to write to the pty (UTF-8 bytes).
    public event Action<string?>? RawTextInput;

        public TerminalInput()
        {
            this.InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _inputBox = this.FindControl<TextBox>("PART_InputBox");
            _promptRenderer = this.FindControl<TerminalPromptRenderer>("PART_PromptRenderer");
            if (_inputBox != null)
            {
                _inputBox.KeyDown += InputBox_KeyDown;
                _lastText = _inputBox.Text ?? string.Empty;
                // Subscribe to text changes to detect typed characters (including IME/paste)
                _inputBox.PropertyChanged += (ss, ee) =>
                {
                    if (ee.Property == TextBox.TextProperty)
                    {
                        InputBox_TextChanged(_inputBox.Text);
                    }
                };
            }
        }

        private string _lastText = string.Empty;
        private IDisposable? _textSub;

        private void InputBox_TextChanged(string? newText)
        {
            try
            {
                newText ??= string.Empty;
                var old = _lastText ?? string.Empty;
                if (_suppressTextChanged)
                {
                    // update last text but do not emit any RawTextInput
                    _lastText = newText;
                    return;
                }
                if (newText.Length > old.Length)
                {
                    // inserted text
                    var inserted = newText.Substring(old.Length);
                    RawTextInput?.Invoke(inserted);
                }
                else if (newText.Length < old.Length)
                {
                    // deletion(s) - send DEL for each removed char
                    int removed = old.Length - newText.Length;
                    for (int i = 0; i < removed; i++) RawTextInput?.Invoke("\u007f");
                }
                _lastText = newText;
            }
            catch { }
        }

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _inputBox != null)
            {
                if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
                {
                    return;
                }

                SubmitCurrentText();
                e.Handled = true;
            }

            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control && _inputBox != null)
            {
                SubmitCurrentText();
                e.Handled = true;
            }

            // Handle some special keys and forward them as raw sequences
            if (_inputBox != null)
            {
                switch (e.Key)
                {
                    case Key.Back:
                        RawTextInput?.Invoke("\u007f"); // DEL
                        e.Handled = true;
                        break;
                    case Key.Tab:
                        RawTextInput?.Invoke("\t");
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        RawTextInput?.Invoke("\u001b");
                        e.Handled = true;
                        break;
                    case Key.Left:
                        RawTextInput?.Invoke("\u001b[D");
                        e.Handled = true;
                        break;
                    case Key.Right:
                        RawTextInput?.Invoke("\u001b[C");
                        e.Handled = true;
                        break;
                    case Key.Up:
                        RawTextInput?.Invoke("\u001b[A");
                        e.Handled = true;
                        break;
                    case Key.Down:
                        RawTextInput?.Invoke("\u001b[B");
                        e.Handled = true;
                        break;
                    default:
                        break;
                }
            }
        }

        private void InputBox_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _inputBox != null)
            {
                if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
                    return;

                SubmitCurrentText();
                e.Handled = true;
            }
        }

        private void SubmitCurrentText()
        {
            if (_inputBox == null) return;
            var text = _inputBox.Text;
            Submitted?.Invoke(this, text);
            _inputBox.CaretIndex = _inputBox.Text?.Length ?? 0;
        }

        public string? Text
        {
            get => _inputBox?.Text;
            set
            {
                if (_inputBox != null)
                {
                    try
                    {
                        _suppressTextChanged = true;
                        _inputBox.Text = value ?? string.Empty;
                    }
                    finally
                    {
                        _suppressTextChanged = false;
                    }
                    _inputBox.CaretIndex = _inputBox.Text.Length;
                }
            }
        }

        public void FocusInput()
        {
            _inputBox?.Focus();
        }

        public void Clear()
        {
            if (_inputBox != null)
            {
                try
                {
                    _suppressTextChanged = true;
                    _inputBox.Text = string.Empty;
                }
                finally
                {
                    _suppressTextChanged = false;
                }
            }
        }

        public string? Prompt
        {
            get => _promptPlain;
            set
            {
                _promptPlain = value ?? string.Empty;
                // If no renderer present, set the input's watermark as a simple fallback
                if (_promptRenderer == null && _inputBox != null)
                {
                    _inputBox.Watermark = _promptPlain;
                }
            }
        }

        public void SetPromptSegments(List<Dotty.App.PromptSegment>? segments)
        {
            if (_promptRenderer != null)
            {
                _promptRenderer.Segments = segments;
            }
            
            // Also set plain text fallback for accessibility/search
            if (segments == null || segments.Count == 0)
            {
                // Use the Prompt setter which already suppresses text-changed events
                Prompt = string.Empty;
                return;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var s in segments)
            {
                sb.Append(s.Text);
            }
            Prompt = sb.ToString();
        }

        public string? WorkingDirectory
        {
            get => _inputBox?.Watermark;
            set
            {
                if (_inputBox != null)
                {
                    _inputBox.Watermark = value ?? string.Empty;
                }
            }
        }
    }
}
