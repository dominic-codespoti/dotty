using System.Text.RegularExpressions;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// ANSI helper utilities for adapter and parser code.
/// </summary>
public static class AnsiUtilities
{
    private static readonly Regex OscPattern = new("\u001b\\].*?\u0007", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CsiPattern = new("\u001b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex SimpleEscPattern = new("\u001b.", RegexOptions.Compiled);

    public static string StripAnsi(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var noOsc = OscPattern.Replace(input, string.Empty);
        var noCsi = CsiPattern.Replace(noOsc, string.Empty);
        var noEsc = SimpleEscPattern.Replace(noCsi, string.Empty);
        return noEsc;
    }
}
