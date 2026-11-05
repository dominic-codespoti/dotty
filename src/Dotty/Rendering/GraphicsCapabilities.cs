using System;
using System.Globalization;

namespace Dotty.Rendering.Gpu;

/// <summary>GPU or rasterizer family recognised from the driver's <c>GL_VENDOR</c>/<c>GL_RENDERER</c> strings.</summary>
public enum GraphicsRendererFamily
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Apple,
    Arm,
    Qualcomm,
    Llvmpipe,
    Softpipe,
    SwiftShader,
    MesaSoftwareRasterizer,
    GdiGeneric,
    MicrosoftBasicRender,
    AppleSoftware,
}

/// <summary>Result of <see cref="GraphicsCapabilities.Classify"/>.</summary>
/// <param name="IsSoftware">True for a CPU rasterizer (no GPU acceleration).</param>
/// <param name="Family">Recognised family; <see cref="GraphicsRendererFamily.Unknown"/> when nothing matched.</param>
/// <param name="Summary">One human-readable line for logs, diagnostics and notices.</param>
public readonly record struct GraphicsAdapterClassification(
    bool IsSoftware,
    GraphicsRendererFamily Family,
    string Summary);

/// <summary>
/// Validates the graphics contract requested by the Silk.NET desktop host and classifies the active driver.
/// </summary>
public static class GraphicsCapabilities
{
    public const int RequiredOpenGlMajor = 3;
    public const int RequiredOpenGlMinor = 3;

    public static bool IsOpenGlVersionSupported(string? version)
    {
        if (!TryParseOpenGlVersion(version, out int major, out int minor))
            return false;

        return major > RequiredOpenGlMajor ||
            (major == RequiredOpenGlMajor && minor >= RequiredOpenGlMinor);
    }

    /// <summary>
    /// Parses the leading <c>major.minor</c> of a desktop <c>GL_VERSION</c> string ("4.6.0 NVIDIA 555", "3.3 (Core
    /// Profile) Mesa 24.0"). Strings that do not start with a version (for example "OpenGL ES 3.2") are rejected.
    /// </summary>
    public static bool TryParseOpenGlVersion(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        string value = version.Trim();
        int separator = value.IndexOf('.');
        if (separator <= 0)
            return false;

        int end = separator + 1;
        while (end < value.Length && char.IsDigit(value[end]))
            end++;

        return int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
            int.TryParse(value.AsSpan(separator + 1, end - separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>
    /// Classifies the driver from its <c>GL_VENDOR</c>, <c>GL_RENDERER</c> and <c>GL_VERSION</c> strings. Total: any
    /// input (null, empty, localised, unknown) yields a result; matching is ordinal and case-insensitive on stable
    /// ASCII product names, which drivers do not localise. Software rasterizers are checked first because their
    /// vendor string is often a hardware-looking one ("Mesa", "Microsoft Corporation", "Apple Inc.", "Google Inc.").
    /// </summary>
    public static GraphicsAdapterClassification Classify(string? vendor, string? renderer, string? version)
    {
        string vendorText = vendor?.Trim() ?? string.Empty;
        string rendererText = renderer?.Trim() ?? string.Empty;

        GraphicsRendererFamily family = ClassifySoftware(rendererText);
        bool isSoftware = family != GraphicsRendererFamily.Unknown;
        if (!isSoftware)
            family = ClassifyHardware(vendorText, rendererText);

        string summary = string.Concat(
            isSoftware ? "software renderer" : family == GraphicsRendererFamily.Unknown ? "unrecognised renderer" : "hardware renderer",
            " (",
            FamilyName(family),
            "): ",
            rendererText.Length == 0 ? "unknown renderer" : rendererText,
            "; vendor ",
            vendorText.Length == 0 ? "unknown" : vendorText,
            "; OpenGL ",
            string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim());
        return new GraphicsAdapterClassification(isSoftware, family, summary);
    }

    private static GraphicsRendererFamily ClassifySoftware(string renderer)
    {
        if (Contains(renderer, "llvmpipe"))
            return GraphicsRendererFamily.Llvmpipe;
        if (Contains(renderer, "softpipe"))
            return GraphicsRendererFamily.Softpipe;
        if (Contains(renderer, "SwiftShader"))
            return GraphicsRendererFamily.SwiftShader;
        if (Contains(renderer, "Software Rasterizer"))
            return GraphicsRendererFamily.MesaSoftwareRasterizer;
        if (Contains(renderer, "GDI Generic"))
            return GraphicsRendererFamily.GdiGeneric;
        if (Contains(renderer, "Microsoft Basic Render"))
            return GraphicsRendererFamily.MicrosoftBasicRender;
        if (Contains(renderer, "Apple Software Renderer"))
            return GraphicsRendererFamily.AppleSoftware;
        return GraphicsRendererFamily.Unknown;
    }

    private static GraphicsRendererFamily ClassifyHardware(string vendor, string renderer)
    {
        if (Contains(vendor, "NVIDIA") || Contains(renderer, "NVIDIA") || Contains(renderer, "GeForce") || Contains(renderer, "Quadro"))
            return GraphicsRendererFamily.Nvidia;
        if (Contains(vendor, "AMD") || Contains(vendor, "ATI Technologies") || Contains(renderer, "Radeon") || Contains(renderer, "AMD"))
            return GraphicsRendererFamily.Amd;
        if (Contains(vendor, "Intel") || Contains(renderer, "Intel"))
            return GraphicsRendererFamily.Intel;
        if (Contains(renderer, "Mali") || Contains(vendor, "ARM"))
            return GraphicsRendererFamily.Arm;
        if (Contains(renderer, "Adreno") || Contains(vendor, "Qualcomm"))
            return GraphicsRendererFamily.Qualcomm;
        if (Contains(renderer, "Apple") || Contains(vendor, "Apple"))
            return GraphicsRendererFamily.Apple;
        return GraphicsRendererFamily.Unknown;
    }

    private static bool Contains(string text, string token) =>
        text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string FamilyName(GraphicsRendererFamily family) => family switch
    {
        GraphicsRendererFamily.Nvidia => "NVIDIA",
        GraphicsRendererFamily.Amd => "AMD",
        GraphicsRendererFamily.Intel => "Intel",
        GraphicsRendererFamily.Apple => "Apple",
        GraphicsRendererFamily.Arm => "Arm Mali",
        GraphicsRendererFamily.Qualcomm => "Qualcomm Adreno",
        GraphicsRendererFamily.Llvmpipe => "Mesa llvmpipe",
        GraphicsRendererFamily.Softpipe => "Mesa softpipe",
        GraphicsRendererFamily.SwiftShader => "SwiftShader",
        GraphicsRendererFamily.MesaSoftwareRasterizer => "Mesa software rasterizer",
        GraphicsRendererFamily.GdiGeneric => "Windows GDI Generic",
        GraphicsRendererFamily.MicrosoftBasicRender => "Microsoft Basic Render Driver",
        GraphicsRendererFamily.AppleSoftware => "Apple Software Renderer",
        _ => "unknown",
    };

    public static string DescribeUnsupportedVersion(string? version) =>
        $"Dotty requires OpenGL {RequiredOpenGlMajor}.{RequiredOpenGlMinor} or newer; the active driver reported '{version ?? "unknown"}'.";

    public static string DescribeInitializationFailure(Exception exception) =>
        $"Dotty could not initialize its OpenGL {RequiredOpenGlMajor}.{RequiredOpenGlMinor} desktop renderer: {exception.Message}";
}
