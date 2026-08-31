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
}
