using System;

namespace Dotty.Runtime.Search;

/// <summary>
/// Layout descriptor for the floating search overlay dialog.
/// Positioned at the top-right, constrained to the framebuffer and scaled with the device.
/// </summary>
public readonly record struct SearchOverlayLayout
{
    public const float DefaultWidth = 320f;
    public const float DefaultHeight = 36f;
    public const float DefaultMarginRight = 16f;
    private readonly record struct BadgeKey(
        int ActiveMatchIndex,
        int TotalMatches,
        bool SearchActive);

    private static readonly object BadgeCacheLock = new();
    private static BadgeKey _badgeKey;
    private static string? _badgeText;
    private static bool _hasBadgeKey;

    private static string GetBadgeText(string? query, int activeMatchIndex, int totalMatches)
    {
        var key = new BadgeKey(
            activeMatchIndex,
            totalMatches,
            !string.IsNullOrEmpty(query));

        lock (BadgeCacheLock)
        {
            if (_hasBadgeKey && key.Equals(_badgeKey))
                return _badgeText!;

            _badgeKey = key;
            _hasBadgeKey = true;
            _badgeText = totalMatches > 0
                ? $"{(activeMatchIndex >= 0 ? activeMatchIndex + 1 : 0)}/{totalMatches}"
                : "0/0";
            return _badgeText;
        }
    }

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

    /// <summary>Previous match button bounding rect.</summary>
    public OverlayRect PrevButtonRect { get; init; }

    /// <summary>Next match button bounding rect.</summary>
    public OverlayRect NextButtonRect { get; init; }

    /// <summary>Close button bounding rect.</summary>
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
        float marginTop = DefaultMarginTop,
        float scale = 1f,
        float minimumHeight = 0f)
    {
        width = Math.Min(Math.Max(0f, viewportWidth), width * scale);
        height = Math.Min(Math.Max(0f, viewportHeight), Math.Max(height * scale, minimumHeight));
        float x = Math.Clamp(viewportWidth - width - marginRight * scale, 0f, Math.Max(0f, viewportWidth - width));
        float y = Math.Clamp(marginTop * scale, 0f, Math.Max(0f, viewportHeight - height));
        string badge = GetBadgeText(query, activeMatchIndex, totalMatches);
        float pad = Math.Min(4f * scale, Math.Min(width, height) / 4f);
        float innerW = Math.Max(0f, width - pad * 2f);
        float innerH = Math.Max(0f, height - pad * 2f);
        float controlScale = Math.Min(scale, innerW / 224f);
        float gap = 4f * controlScale;
        float btnW = 24f * controlScale;
        float closeW = 28f * controlScale;
        float badgeW = 64f * controlScale;
        float inputW = Math.Max(0f, innerW - badgeW - btnW * 2f - closeW - gap * 4f);
        float curX = x + pad;
        var inputRect = new OverlayRect(curX, y + pad, inputW, innerH);
        curX += inputW + gap;
        var badgeRect = new OverlayRect(curX, y + pad, badgeW, innerH);
        curX += badgeW + gap;
        var prevRect = new OverlayRect(curX, y + pad, btnW, innerH);
        curX += btnW + gap;
        var nextRect = new OverlayRect(curX, y + pad, btnW, innerH);
        curX += btnW + gap;
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
