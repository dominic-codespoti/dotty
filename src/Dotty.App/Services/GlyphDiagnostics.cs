using System;

namespace Dotty.App.Services;

internal static class GlyphDiagnostics
{
    public static void Run()
    {
        // Keep diagnostics out of normal runs; delegate to the FontProbe tool
        try
        {
            Tools.FontProbe.Run();
        }
        catch (Exception ex)
        {
            try { Console.WriteLine($"[Dotty][GlyphDiag] error: {ex}"); } catch { }
        }
    }
}
