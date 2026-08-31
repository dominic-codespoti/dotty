using System;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
namespace Dotty.Runtime.Tabs;

public sealed class TerminalTab : IDisposable
{
    private string _title;
    private int _scrollOffset;
    private bool _isDisposed;

    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                TitleChanged?.Invoke(_title);
            }
        }
    }

    public PaneTree PaneTree { get; }
    public LeafPane ActivePane => PaneTree.ActivePane;
    public TerminalSession Session => ActivePane.Session;
    public string? WorkingDirectory { get; }

    public int ScrollOffset
    {
        get => _scrollOffset;
        private set => _scrollOffset = Math.Max(0, value);
    }

    public bool IsActive { get; set; }
    public bool HasBellAlert { get; set; }
    public event Action<string>? TitleChanged;

    public TerminalTab(string? title = null, string? workingDirectory = null, int rows = 24, int columns = 80)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "Terminal" : title;
        WorkingDirectory = workingDirectory;
        PaneTree = new PaneTree(workingDirectory: workingDirectory, rows: rows, columns: columns);
        Session.TitleChanged += OnSessionTitleChanged;
    }

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
    }

    public void ScrollUp(int lines, int maxScrollback)
    {
        if (lines <= 0) return;
        var maxOffset = Math.Max(0, maxScrollback);
        _scrollOffset = Math.Min(maxOffset, _scrollOffset + lines);
    }

    public void ScrollDown(int lines)
    {
        if (lines <= 0) return;
        _scrollOffset = Math.Max(0, _scrollOffset - lines);
    }

    public void ScrollTo(int offset, int maxScrollback)
    {
        var maxOffset = Math.Max(0, maxScrollback);
        _scrollOffset = Math.Clamp(offset, 0, maxOffset);
    }

    private void OnSessionTitleChanged(string newTitle)
    {
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            Title = newTitle;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Session.TitleChanged -= OnSessionTitleChanged;
        PaneTree.Dispose();
    }
}
