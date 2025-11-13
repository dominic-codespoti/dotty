using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Dotty.App.Controls
{
    public partial class TerminalInput : UserControl
    {
        private TextBox? _inputBox;
        private TextBlock? _promptBlock;

        public event EventHandler<string?>? Submitted;

        public TerminalInput()
        {
            this.InitializeComponent();
            this.AttachedToVisualTree += OnAttached;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _inputBox = this.FindControl<TextBox>("PART_InputBox");
            _promptBlock = this.FindControl<TextBlock>("PromptBlock");

            if (_inputBox != null)
            {
                _inputBox.KeyDown += InputBox_KeyDown;
            }
        }

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _inputBox != null)
            {
                var text = _inputBox.Text;
                Submitted?.Invoke(this, text);
                e.Handled = true;
            }
        }

        public string? Text
        {
            get => _inputBox?.Text;
            set
            {
                if (_inputBox != null)
                {
                    _inputBox.Text = value ?? string.Empty;
                    _inputBox.CaretIndex = _inputBox.Text.Length;
                }
            }
        }

        public string? Prompt
        {
            get => _promptBlock?.Text;
            set { if (_promptBlock != null) _promptBlock.Text = value ?? string.Empty; }
        }

        public void FocusInput()
        {
            _inputBox?.Focus();
        }

        public void Clear()
        {
            if (_inputBox != null)
                _inputBox.Text = string.Empty;
        }
    }
}
