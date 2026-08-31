using System;

namespace Dotty.Silk;

public static class WindowFocusRouter
{
    public static void Route(
        ref bool? lastFocus,
        bool focused,
        bool closed,
        Action<bool>? report)
    {
        if (lastFocus == focused)
            return;

        lastFocus = focused;
        if (closed)
            return;

        report?.Invoke(focused);
    }
}
