using System;
using System.Globalization;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Validates the graphics contract requested by the Silk.NET desktop host.
/// </summary>
public static class GraphicsCapabilities
{
    public const int RequiredOpenGlMajor = 3;
    public const int RequiredOpenGlMinor = 3;

    public static bool IsOpenGlVersionSupported(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        string value = version.Trim();
        int separator = value.IndexOf('.');
        if (separator <= 0)
            return false;

        int end = separator + 1;
        while (end < value.Length && char.IsDigit(value[end]))
            end++;

        if (!int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(value.AsSpan(separator + 1, end - separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            return false;
        }

        return major > RequiredOpenGlMajor ||
            (major == RequiredOpenGlMajor && minor >= RequiredOpenGlMinor);
    }

    public static string DescribeUnsupportedVersion(string? version) =>
        $"Dotty requires OpenGL {RequiredOpenGlMajor}.{RequiredOpenGlMinor} or newer; the active driver reported '{version ?? "unknown"}'.";

    public static string DescribeInitializationFailure(Exception exception) =>
        $"Dotty could not initialize its OpenGL {RequiredOpenGlMajor}.{RequiredOpenGlMinor} desktop renderer: {exception.Message}";
}
