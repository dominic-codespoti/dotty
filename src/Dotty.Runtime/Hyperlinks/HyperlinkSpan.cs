namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Represents a detected hyperlink range within a terminal row.
/// </summary>
/// <param name="Row">The 0-based row index.</param>
/// <param name="StartCol">The inclusive 0-based start column index.</param>
/// <param name="EndCol">The inclusive 0-based end column index.</param>
/// <param name="Url">The target URL or link destination.</param>
/// <param name="Id">Optional OSC 8 hyperlink ID or explicit identifier.</param>
public readonly record struct HyperlinkSpan(int Row, int StartCol, int EndCol, string Url, string? Id);
