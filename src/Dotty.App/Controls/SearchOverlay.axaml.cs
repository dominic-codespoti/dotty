using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Controls;

/// <summary>
/// Search overlay control for the terminal.
/// Provides search input, options, and navigation UI.
/// </summary>
public partial class SearchOverlay : UserControl
{
    private TerminalSearch? _search;
    private bool _isUpdating;

    // Events
    public event EventHandler? SearchRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<SearchMatch>? MatchNavigated;

    // Properties
    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<SearchOverlay, string>(nameof(SearchText), string.Empty);

    public static readonly StyledProperty<bool> CaseSensitiveProperty =
        AvaloniaProperty.Register<SearchOverlay, bool>(nameof(CaseSensitive), false);

    public static readonly StyledProperty<bool> UseRegexProperty =
        AvaloniaProperty.Register<SearchOverlay, bool>(nameof(UseRegex), false);

    public static readonly StyledProperty<int> CurrentMatchIndexProperty =
        AvaloniaProperty.Register<SearchOverlay, int>(nameof(CurrentMatchIndex), -1);

    public static readonly StyledProperty<int> MatchCountProperty =
        AvaloniaProperty.Register<SearchOverlay, int>(nameof(MatchCount), 0);

    public string SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public bool CaseSensitive
    {
        get => GetValue(CaseSensitiveProperty);
        set => SetValue(CaseSensitiveProperty, value);
    }

    public bool UseRegex
    {
        get => GetValue(UseRegexProperty);
        set => SetValue(UseRegexProperty, value);
    }

    public int CurrentMatchIndex
    {
        get => GetValue(CurrentMatchIndexProperty);
        private set => SetValue(CurrentMatchIndexProperty, value);
    }

    public int MatchCount
    {
        get => GetValue(MatchCountProperty);
        private set => SetValue(MatchCountProperty, value);
    }

    /// <summary>
    /// Returns true if there are matches to navigate.
    /// </summary>
    public bool HasMatches => MatchCount > 0;

    /// <summary>
    /// The currently selected match, or Empty if none.
    /// </summary>
    public SearchMatch CurrentMatch => _search?.CurrentMatch ?? SearchMatch.Empty;

    /// <summary>
    /// All matches from the current search.
    /// </summary>
    public IReadOnlyList<SearchMatch> Matches => _search?.Matches ?? Array.Empty<SearchMatch>();

    public SearchOverlay()
    {
        InitializeComponent();

        // Ensure this control can receive focus and intercept keyboard events
        Focusable = true;

        // Wire up event handlers
        SearchTextBox.KeyDown += OnSearchTextBoxKeyDown;
        SearchTextBox.TextChanged += OnSearchTextChanged;

        CaseSensitiveToggle.Click += OnCaseSensitiveToggleClick;
        RegexToggle.Click += OnRegexToggleClick;

        PreviousButton.Click += OnPreviousClick;
        NextButton.Click += OnNextClick;
        CloseButton.Click += OnCloseClick;

        KeyDown += OnOverlayKeyDown;

        // Add preview handler to intercept keys BEFORE they bubble to parent (terminal)
        // This is critical because TerminalView also uses Tunnel strategy
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Update UI when properties change
        PropertyChanged += OnPropertyChanged;
    }

    /// <summary>
    /// Initializes search with a TerminalSearch instance.
    /// </summary>
    public void InitializeSearch(TerminalSearch search)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
    }

    /// <summary>
    /// Shows the search overlay and focuses the input.
    /// </summary>
    public void ShowSearch()
    {
        IsVisible = true;
        
        // Make sure overlay itself is focusable to intercept keyboard events
        Focusable = true;
        
        // Delay focus to ensure control is attached to visual tree
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Focus only the text box - don't steal it back with this.Focus()
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            // REMOVED: this.Focus();  // This was stealing focus back from the TextBox!
        }, Avalonia.Threading.DispatcherPriority.Input);

        // Perform search if there's already text
        if (!string.IsNullOrEmpty(SearchText))
        {
            PerformSearch();
        }
    }

    /// <summary>
    /// Hides the search overlay.
    /// </summary>
    public void HideSearch()
    {
        IsVisible = false;
        ClearSearch();
    }

    /// <summary>
    /// Clears search results without hiding the overlay.
    /// </summary>
    public void ClearSearch()
    {
        _search?.Clear();
        UpdateMatchCounter();
    }

    /// <summary>
    /// Navigates to the next match.
    /// </summary>
    public bool NextMatch()
    {
        if (_search?.NextMatch() != true) return false;

        CurrentMatchIndex = _search.CurrentMatchIndex;
        UpdateMatchCounter();
        MatchNavigated?.Invoke(this, _search.CurrentMatch);
        return true;
    }

    /// <summary>
    /// Navigates to the previous match.
    /// </summary>
    public bool PreviousMatch()
    {
        if (_search?.PreviousMatch() != true) return false;

        CurrentMatchIndex = _search.CurrentMatchIndex;
        UpdateMatchCounter();
        MatchNavigated?.Invoke(this, _search.CurrentMatch);
        return true;
    }

    /// <summary>
    /// Refreshes the search (useful after buffer content changes).
    /// </summary>
    public void RefreshSearch()
    {
        if (!string.IsNullOrEmpty(SearchText))
        {
            int count = _search?.RefreshSearch() ?? 0;
            MatchCount = count;
            CurrentMatchIndex = _search?.CurrentMatchIndex ?? -1;
            UpdateMatchCounter();
        }
    }

    private void PerformSearch()
    {
        if (_search == null) 
        {
            return;
        }

        int count = _search.Search(SearchText, CaseSensitive, UseRegex);
        MatchCount = count;
        CurrentMatchIndex = count > 0 ? 0 : -1;

        UpdateMatchCounter();

        if (count > 0)
        {
            MatchNavigated?.Invoke(this, _search.CurrentMatch);
        }

        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMatchCounter()
    {
        if (MatchCount == 0)
        {
            MatchCounter.Text = string.IsNullOrEmpty(SearchText) ? "0/0" : "No matches";
            MatchCounter.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF6666"));
        }
        else
        {
            MatchCounter.Text = $"{CurrentMatchIndex + 1}/{MatchCount}";
            MatchCounter.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#66FF66"));
        }
    }

    private void OnSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                PreviousMatch();
                break;

            case Key.Enter:
                e.Handled = true;
                NextMatch();
                break;

            case Key.Escape:
                e.Handled = true;
                HideSearch();
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;

        SearchText = SearchTextBox.Text ?? string.Empty;
        PerformSearch();
    }

    private void OnCaseSensitiveToggleClick(object? sender, RoutedEventArgs e)
    {
        CaseSensitive = CaseSensitiveToggle.IsChecked ?? false;
        PerformSearch();
    }

    private void OnRegexToggleClick(object? sender, RoutedEventArgs e)
    {
        UseRegex = RegexToggle.IsChecked ?? false;
        PerformSearch();
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e)
    {
        PreviousMatch();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        NextMatch();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        HideSearch();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        // Handle Alt+C for case sensitive
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            CaseSensitiveToggle.IsChecked = !(CaseSensitiveToggle.IsChecked ?? false);
            CaseSensitive = CaseSensitiveToggle.IsChecked ?? false;
            PerformSearch();
            return;
        }

        // Handle Alt+R for regex
        if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            RegexToggle.IsChecked = !(RegexToggle.IsChecked ?? false);
            UseRegex = RegexToggle.IsChecked ?? false;
            PerformSearch();
            return;
        }
    }

    /// <summary>
    /// Preview key handler that intercepts keys BEFORE they reach the terminal.
    /// This is registered with RoutingStrategies.Tunnel to get priority over TerminalView.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Only intercept when the overlay is visible and has focus or any child has focus
        if (!IsVisible)
            return;

        // Check if any control in the search overlay has focus
        if (!IsAnyChildFocused() && !IsFocused)
            return;

        // Only handle specific navigation/control keys
        // DO NOT mark text input keys as handled - they should go to the TextBox normally
        switch (e.Key)
        {
            case Key.Escape:
                // Close search on Escape
                e.Handled = true;
                HideSearch();
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;

            case Key.Enter when !SearchTextBox.IsFocused:
                // Navigate to next match if Enter pressed on a button
                e.Handled = true;
                NextMatch();
                break;

            case Key.Tab:
                // Handle Tab to prevent it from reaching terminal, but allow normal navigation
                // We just mark as handled - Avalonia's default Tab navigation will still work
                e.Handled = true;
                break;
        }
        
        // Note: We do NOT set e.Handled for regular text input keys
        // They will be processed by the TextBox normally
    }

    /// <summary>
    /// Returns true if any child control in the search overlay currently has focus.
    /// </summary>
    public bool IsAnyChildFocused()
    {
        // Get the currently focused element
        var focusedElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        
        if (focusedElement == null)
            return false;

        // Check if the focused element is a descendant of this overlay
        if (focusedElement is Visual visual)
        {
            return IsVisualDescendantOf(this, visual);
        }

        // Also check the named controls directly
        return focusedElement == SearchTextBox ||
               focusedElement == CaseSensitiveToggle ||
               focusedElement == RegexToggle ||
               focusedElement == PreviousButton ||
               focusedElement == NextButton ||
               focusedElement == CloseButton;
    }

    /// <summary>
    /// Helper method to check if a visual element is a descendant of another.
    /// Uses the FocusManager to find the focus scope and check if this overlay contains the element.
    /// </summary>
    private bool IsVisualDescendantOf(Visual parent, Visual child)
    {
        if (child == parent)
            return true;

        // Walk up the logical tree using Parent property which is public
        Control? current = child as Control;
        while (current != null)
        {
            if (current == parent)
                return true;
            current = current.Parent as Control;
        }

        return false;
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == SearchTextProperty)
        {
            _isUpdating = true;
            try
            {
                if (SearchTextBox.Text != SearchText)
                {
                    SearchTextBox.Text = SearchText;
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }
        else if (e.Property == CaseSensitiveProperty)
        {
            CaseSensitiveToggle.IsChecked = CaseSensitive;
        }
        else if (e.Property == UseRegexProperty)
        {
            RegexToggle.IsChecked = UseRegex;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Sync initial state
        CaseSensitiveToggle.IsChecked = CaseSensitive;
        RegexToggle.IsChecked = UseRegex;
        SearchTextBox.Text = SearchText;
    }
}
