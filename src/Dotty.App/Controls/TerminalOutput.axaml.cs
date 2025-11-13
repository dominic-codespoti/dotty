using Avalonia.Controls;
using Avalonia.Threading;

namespace Dotty.App.Controls
{
    public partial class TerminalOutput : UserControl
    {
        public TerminalOutput()
        {
            InitializeComponent();
        }

        private TextBox? TextBoxPart => this.FindControl<TextBox>("PART_TextBox");

        public string Text
        {
            get => TextBoxPart?.Text ?? string.Empty;
            set
            {
                // Ensure we update on UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    if (TextBoxPart != null)
                    {
                        TextBoxPart.Text = value;
                        // Move caret to end; setting CaretIndex should bring it into view.
                        TextBoxPart.CaretIndex = TextBoxPart.Text.Length;
                        TextBoxPart.SelectionStart = TextBoxPart.Text.Length;
                        TextBoxPart.SelectionEnd = TextBoxPart.Text.Length;
                    }
                });
            }
        }

        public int CaretIndex
        {
            get => TextBoxPart?.CaretIndex ?? 0;
            set { if (TextBoxPart != null) TextBoxPart.CaretIndex = value; }
        }
    }
}
