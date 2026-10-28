namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Represents a detected hyperlink range within a terminal row.
/// </summary>
/// <param name="Row">The 0-based row index.</param>
/// <param name="StartCol">The inclusive 0-based start column index.</param>
/// <param name="EndCol">The inclusive 0-based end column index.</param>
/// <param name="Url">The target URL or link destination as found (unvalidated).</param>
/// <param name="Id">For explicit spans, the buffer's internal URI-table index (not the OSC 8 <c>id=</c>); null for detected spans.</param>
/// <param name="Source">Whether the link is an OSC 8 explicit link or detected from text.</param>
public readonly record struct HyperlinkSpan(int Row, int StartCol, int EndCol, string Url, string? Id, HyperlinkSource Source);

/// <summary>Options controlling which kinds of links a scan reports.</summary>
/// <param name="IncludeExplicit">Report OSC 8 links (false hides them; detected URLs are unaffected).</param>
public readonly record struct HyperlinkScanOptions(bool IncludeExplicit)
{
    public static HyperlinkScanOptions Default => new(true);
}
