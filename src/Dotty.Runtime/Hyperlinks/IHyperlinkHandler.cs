using System.Threading.Tasks;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Interface for opening and handling hyperlinks detected in the terminal.
/// </summary>
public interface IHyperlinkHandler
{
    /// <summary>
    /// Attempts to open the specified URL with the host system or default application.
    /// </summary>
    /// <param name="url">The URL or link target to open.</param>
    /// <returns>A task that completes with true if the URL was opened successfully; otherwise false.</returns>
    Task<bool> OpenUrlAsync(string url);

    /// <summary>
    /// Determines whether the given URL is valid and allowed to be opened by this handler.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>True if the URL scheme or pattern is permitted; otherwise false.</returns>
    bool CanOpen(string url);
}
