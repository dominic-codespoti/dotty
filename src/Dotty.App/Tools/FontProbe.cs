using System.Collections.Generic;

namespace Dotty.App.Tools;

internal static class FontProbe
{
    public static void Run(IEnumerable<int>? codepoints = null)
    {
        // FontProbe removed heavy diagnostics. This method is intentionally a no-op
        // to avoid side-effects in normal application runs.
    }
}
