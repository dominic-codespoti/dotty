namespace Dotty.Runtime.Clipboard;

/// <summary>
/// The X11-style PRIMARY selection. Implementations are used from the window (main) thread only.
/// On platforms without a real primary selection an in-process buffer stands in (<see cref="IsSystemSelection"/> false).
/// </summary>
public interface IPrimarySelection
{
    /// <summary>True when the buffer is the operating system's PRIMARY selection, visible to other applications.</summary>
    bool IsSystemSelection { get; }

    /// <summary>Current selection text; null when there is none or it cannot be read. Never throws.</summary>
    string? Get();

    /// <summary>Replaces the selection. Never throws.</summary>
    void Set(string text);
}

/// <summary>Primary selection that lives only inside this process.</summary>
public sealed class InProcessPrimarySelection : IPrimarySelection
{
    private readonly object _lock = new();
    private string? _text;

    public bool IsSystemSelection => false;

    public string? Get()
    {
        lock (_lock)
            return _text;
    }

    public void Set(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_lock)
            _text = text;
    }
}

/// <summary>Presents <see cref="IPrimarySelection"/> as a clipboard so it can feed the shared paste router.</summary>
public sealed class PrimarySelectionClipboard : ITerminalClipboard
{
    private readonly IPrimarySelection _primary;

    public PrimarySelectionClipboard(IPrimarySelection primary) =>
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));

    public string? GetText() => _primary.Get();
    public void SetText(string text) => _primary.Set(text);
    public bool HasText => !string.IsNullOrEmpty(GetText());
}
