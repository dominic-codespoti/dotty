using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class GraphicsCapabilitiesTests
{
    [Theory]
    [InlineData("3.3 Mesa 24.0", true)]
    [InlineData("4.6.0 NVIDIA 555", true)]
    [InlineData("3.2 Mesa 24.0", false)]
    [InlineData("OpenGL ES 3.2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOpenGlVersionSupported_EnforcesCoreMinimum(string? version, bool expected)
    {
        Assert.Equal(expected, GraphicsCapabilities.IsOpenGlVersionSupported(version));
    }

    [Fact]
    public void DescribeUnsupportedVersion_ContainsRequiredVersionAndReportedValue()
    {
        string message = GraphicsCapabilities.DescribeUnsupportedVersion("3.2 Mesa");

        Assert.Contains("OpenGL 3.3", message);
        Assert.Contains("3.2 Mesa", message);
    }

    [Theory]
    [InlineData("3.3 (Core Profile) Mesa 24.0.5", 3, 3)]
    [InlineData("4.6.0 NVIDIA 555.42", 4, 6)]
    [InlineData("  4.1 Metal - 88 ", 4, 1)]
    [InlineData("10.12", 10, 12)]
    public void TryParseOpenGlVersion_ReadsLeadingMajorMinor(string version, int major, int minor)
    {
        Assert.True(GraphicsCapabilities.TryParseOpenGlVersion(version, out int parsedMajor, out int parsedMinor));
        Assert.Equal((major, minor), (parsedMajor, parsedMinor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OpenGL ES 3.2")]
    [InlineData(".3")]
    [InlineData("4.")]
    public void TryParseOpenGlVersion_RejectsNonDesktopOrMalformed(string? version) =>
        Assert.False(GraphicsCapabilities.TryParseOpenGlVersion(version, out _, out _));

    [Theory]
    // Software rasterizers: the renderer string decides, whatever the vendor says.
    [InlineData("Mesa", "llvmpipe (LLVM 17.0.6, 256 bits)", true, GraphicsRendererFamily.Llvmpipe)]
    [InlineData("VMware, Inc.", "LLVMPIPE (LLVM 12.0.0, 128 bits)", true, GraphicsRendererFamily.Llvmpipe)]
    [InlineData("Collabora Ltd", "zink Vulkan 1.3(llvmpipe (LLVM 15.0.7, 256 bits) (MESA_LLVMPIPE))", true, GraphicsRendererFamily.Llvmpipe)]
    [InlineData("Mesa", "softpipe", true, GraphicsRendererFamily.Softpipe)]
    [InlineData("Google Inc.", "Google SwiftShader", true, GraphicsRendererFamily.SwiftShader)]
    [InlineData("Mesa Project", "Software Rasterizer", true, GraphicsRendererFamily.MesaSoftwareRasterizer)]
    [InlineData("Microsoft Corporation", "GDI Generic", true, GraphicsRendererFamily.GdiGeneric)]
    [InlineData("Microsoft Corporation", "D3D12 (Microsoft Basic Render Driver)", true, GraphicsRendererFamily.MicrosoftBasicRender)]
    [InlineData("Apple Inc.", "Apple Software Renderer", true, GraphicsRendererFamily.AppleSoftware)]
    // Hardware.
    [InlineData("NVIDIA Corporation", "NVIDIA GeForce RTX 3080/PCIe/SSE2", false, GraphicsRendererFamily.Nvidia)]
    [InlineData("AMD", "AMD Radeon RX 6800 (radeonsi, navi21, LLVM 15.0.7, DRM 3.49)", false, GraphicsRendererFamily.Amd)]
    [InlineData("ATI Technologies Inc.", "Radeon Pro 560X OpenGL Engine", false, GraphicsRendererFamily.Amd)]
    [InlineData("Intel", "Mesa Intel(R) UHD Graphics 620 (KBL GT2)", false, GraphicsRendererFamily.Intel)]
    [InlineData("Intel Inc.", "Intel Iris Pro OpenGL Engine", false, GraphicsRendererFamily.Intel)]
    [InlineData("Apple", "Apple M2 Pro", false, GraphicsRendererFamily.Apple)]
    [InlineData("ARM", "Mali-G78", false, GraphicsRendererFamily.Arm)]
    [InlineData("Qualcomm", "Adreno (TM) 740", false, GraphicsRendererFamily.Qualcomm)]
    // Unknown, empty, null and localised strings never throw and never claim software.
    [InlineData("Red Hat", "virgl", false, GraphicsRendererFamily.Unknown)]
    [InlineData("", "", false, GraphicsRendererFamily.Unknown)]
    [InlineData(null, null, false, GraphicsRendererFamily.Unknown)]
    [InlineData("Неизвестный поставщик", "Графический адаптер", false, GraphicsRendererFamily.Unknown)]
    public void Classify_RecognisesRendererFamilies(string? vendor, string? renderer, bool software, GraphicsRendererFamily family)
    {
        var result = GraphicsCapabilities.Classify(vendor, renderer, "3.3 Mesa 24.0");

        Assert.Equal(software, result.IsSoftware);
        Assert.Equal(family, result.Family);
    }

    [Fact]
    public void Classify_SummaryNamesRendererVendorAndVersion()
    {
        var result = GraphicsCapabilities.Classify("Mesa", "llvmpipe (LLVM 17.0.6, 256 bits)", "4.5 (Core Profile) Mesa 24.0.5");

        Assert.Equal(
            "software renderer (Mesa llvmpipe): llvmpipe (LLVM 17.0.6, 256 bits); vendor Mesa; OpenGL 4.5 (Core Profile) Mesa 24.0.5",
            result.Summary);
    }

    [Fact]
    public void Classify_NullInputsProduceAnUnknownSummary()
    {
        var result = GraphicsCapabilities.Classify(null, null, null);

        Assert.Equal("unrecognised renderer (unknown): unknown renderer; vendor unknown; OpenGL unknown", result.Summary);
    }
}
