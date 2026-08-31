using System;

namespace Dotty.Runtime.Search;

/// <summary>
/// Layout descriptor for the floating search overlay dialog.
/// Positioned at the top-right corner of the terminal view ($320\text{px} \times 36\text{px}$).
/// </summary>
public readonly record struct SearchOverlayLayout
{
    public const float DefaultWidth = 320f;
    public const float DefaultHeight = 36f;
    public const float DefaultMarginRight = 16f;
    public const float DefaultMarginTop = 8f;

    /// <summary>Total width in pixels.</summary>
    public float Width { get; init; }

    /// <summary>Total height in pixels.</summary>
    public float Height { get; init; }

    /// <summary>Top-left X position in viewport pixels.</summary>
    public float X { get; init; }

    /// <summary>Top-left Y position in viewport pixels.</summary>
    public float Y { get; init; }

    /// <summary>Input query box bounding rect (X, Y, Width, Height).</summary>
    public OverlayRect InputBoxRect { get; init; }

    /// <summary>Match counter badge bounding rect (X, Y, Width, Height).</summary>
    public OverlayRect MatchCountRect { get; init; }

    /// <summary>Previous match button (▲) bounding rect.</summary>
    public OverlayRect PrevButtonRect { get; init; }

    /// <summary>Next match button (▼) bounding rect.</summary>
    public OverlayRect NextButtonRect { get; init; }

    /// <summary>Close button (×) bounding rect.</summary>
    public OverlayRect CloseButtonRect { get; init; }

    /// <summary>Current query text.</summary>
    public string Query { get; init; }

    /// <summary>Formatted match count badge string (e.g. "3/42" or "0/0").</summary>
    public string MatchBadgeText { get; init; }

    /// <summary>
    /// Computes layout for a search overlay in a viewport of the given dimensions.
    /// </summary>
    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <param name="query">Current search query string.</param>
    /// <param name="activeMatchIndex">0-based active match index, or -1 if none.</param>
    /// <param name="totalMatches">Total match count.</param>
    /// <param name="width">Width of overlay box.</param>
    /// <param name="height">Height of overlay box.</param>
    /// <param name="marginRight">Right margin in pixels.</param>
    /// <param name="marginTop">Top margin in pixels.</param>
    /// <returns>Computed layout.</returns>
    public static SearchOverlayLayout Compute(
        float viewportWidth,
        float viewportHeight,
        string query,
        int activeMatchIndex,
        int totalMatches,
        float width = DefaultWidth,
        float height = DefaultHeight,
        float marginRight = DefaultMarginRight,
        float marginTop = DefaultMarginTop)
    {
        float x = Math.Max(0f, viewportWidth - width - marginRight);
        float y = marginTop;

        string badge = totalMatches > 0
            ? $"{(activeMatchIndex >= 0 ? activeMatchIndex + 1 : 0)}/{totalMatches}"
            : "0/0";

        // Internal layout:
        // [ Input text area (~160px) | Match Badge (~60px) | Prev ▲ (24px) | Next ▼ (24px) | Close × (28px) ]
        // Padding: 4px top/bottom/left/right
        float pad = 4f;
        float innerH = height - (pad * 2);
        float curX = x + pad;

        float btnW = 24f;
        float closeW = 28f;
        float badgeW = 64f;
        float inputW = Math.Max(60f, width - (pad * 2) - badgeW - (btnW * 2) - closeW - (pad * 4));

        var inputRect = new OverlayRect(curX, y + pad, inputW, innerH);
        curX += inputW + pad;

        var badgeRect = new OverlayRect(curX, y + pad, badgeW, innerH);
        curX += badgeW + pad;

        var prevRect = new OverlayRect(curX, y + pad, btnW, innerH);
        curX += btnW + pad;

        var nextRect = new OverlayRect(curX, y + pad, btnW, innerH);
        curX += btnW + pad;

        var closeRect = new OverlayRect(curX, y + pad, closeW, innerH);

        return new SearchOverlayLayout
        {
            Width = width,
            Height = height,
            X = x,
            Y = y,
            InputBoxRect = inputRect,
            MatchCountRect = badgeRect,
            PrevButtonRect = prevRect,
            NextButtonRect = nextRect,
            CloseButtonRect = closeRect,
            Query = query ?? string.Empty,
            MatchBadgeText = badge
        };
    }
}

/// <summary>
/// Simple rectangle for overlay element bounds.
/// </summary>
public readonly record struct OverlayRect(float X, float Y, float Width, float Height)
{
    public bool Contains(float px, float py) =>
        px >= X && px <= X + Width && py >= Y && py <= Y + Height;
}
