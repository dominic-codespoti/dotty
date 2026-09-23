using System;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
namespace Dotty.Runtime.Tabs;

public sealed class TerminalTab : IDisposable
{
    private string _title;
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

    public bool IsActive { get; set; }
    public bool HasBellAlert { get; set; }
    public event Action<string>? TitleChanged;
    public event Action<LeafPane, int>? ProcessExited;

    public TerminalTab(string? title = null, string? workingDirectory = null, int rows = 24, int columns = 80, string? shell = null, bool deferStart = false)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "Terminal" : title;
        WorkingDirectory = workingDirectory;
        PaneTree = new PaneTree(rows: rows, columns: columns);
        Session.TitleChanged += OnSessionTitleChanged;
        PaneTree.ProcessExited += OnPaneProcessExited;
        if (!deferStart && (!string.IsNullOrEmpty(workingDirectory) || !string.IsNullOrEmpty(shell)))
            Session.StartWithOptions(shell: shell, workingDirectory: workingDirectory);
    }

    private void OnSessionTitleChanged(string newTitle)
    {
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            Title = newTitle;
        }
    }
    private void OnPaneProcessExited(LeafPane leaf, int exitCode)
    {
        if (!_isDisposed)
            ProcessExited?.Invoke(leaf, exitCode);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Session.TitleChanged -= OnSessionTitleChanged;
        PaneTree.ProcessExited -= OnPaneProcessExited;
        PaneTree.Dispose();
    }
}
