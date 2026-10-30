using System.Text;
using Dotty.NativePty.Unix;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;

namespace Dotty.Silk;

/// <summary>Pure formatting for <c>dotty +diagnose</c>; <see cref="CliActions"/> gathers the facts.</summary>
internal static class DiagnoseReport
{
    /// <summary>One <c>label  value</c> line per row, labels padded to a common width; every line ends with <c>\n</c>.</summary>
    public static string Render(IReadOnlyList<(string Label, string Value)> rows)
    {
        int width = 0;
        foreach ((string label, _) in rows)
            width = Math.Max(width, label.Length);

        var sb = new StringBuilder();
        foreach ((string label, string value) in rows)
            sb.Append(label).Append(':').Append(' ', width - label.Length + 2).Append(value).Append('\n');
        return sb.ToString();
    }

    public static string DescribeConfigFile(string path, bool exists, int problems) =>
        !exists ? $"{path} (missing; built-in defaults apply)"
        : problems == 0 ? $"{path} (valid)"
        : $"{path} ({problems} problem{(problems == 1 ? "" : "s")}; run 'dotty +validate-config')";

    /// <summary>Display variables GLFW uses on Linux; null on other platforms, where no variable is involved.</summary>
    public static string? DescribeDisplay(HostPlatform platform, Func<string, string?> environment)
    {
        if (platform != HostPlatform.Linux)
            return null;
        string? wayland = environment("WAYLAND_DISPLAY");
        string? x11 = environment("DISPLAY");
        bool hasWayland = !string.IsNullOrEmpty(wayland);
        bool hasX11 = !string.IsNullOrEmpty(x11);
        if (!hasWayland && !hasX11)
            return "none (neither WAYLAND_DISPLAY nor DISPLAY is set; no window can open)";
        if (hasWayland && hasX11)
            return $"WAYLAND_DISPLAY={wayland}, DISPLAY={x11}";
        return hasWayland ? $"WAYLAND_DISPLAY={wayland}" : $"DISPLAY={x11}";
    }

    public static string DescribePtyHelper(PtyHelperResolution resolution) =>
        resolution.Path is not null
            ? $"{resolution.Path} ({resolution.Source})"
            : resolution.ProbedPaths.Count == 0
                ? "not found"
                : $"not found; probed {string.Join(", ", resolution.ProbedPaths)}";

    public static string DescribeTerm(string configured, string resolved, string? bundledTerminfoDirectory)
    {
        string bundled = bundledTerminfoDirectory is null ? "no bundled terminfo" : $"bundled terminfo in {bundledTerminfoDirectory}";
        return string.Equals(configured, resolved, StringComparison.Ordinal)
            ? $"{resolved} ({bundled})"
            : $"{resolved} (configured '{configured}' has no terminfo entry here; {bundled})";
    }

    public static string DescribeGraphics(GlInfo info, GraphicsAdapterClassification classification) =>
        $"{info.Version ?? "unknown version"}; {info.Vendor ?? "unknown vendor"}; {info.Renderer ?? "unknown renderer"} ({classification.Summary}"
        + (GraphicsCapabilities.IsOpenGlVersionSupported(info.Version) ? ")" : $"; {GraphicsCapabilities.DescribeUnsupportedVersion(info.Version)})");
}
