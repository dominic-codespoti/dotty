using System;
using Dotty.Runtime;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DottyVersionTests
{
    [Theory]
    [InlineData("0.3.0+abc123", "0.3.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("0.4.0-beta.1", "0.4.0-beta.1")]
    [InlineData("0.4.0-beta.1+sha.5114f85", "0.4.0-beta.1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void StripBuildMetadata_RemovesOnlyBuildMetadata(string? input, string expected) =>
        Assert.Equal(expected, DottyVersion.StripBuildMetadata(input));

    [Theory]
    // Release builds (no source revision): the central version, byte-exact.
    [InlineData("0.3.0", null, "0.3.0")]
    [InlineData("0.4.0-beta.1", null, "0.4.0-beta.1")]
    [InlineData(" 0.3.0 ", null, "0.3.0")]
    [InlineData("0.3.0+", null, "0.3.0")]
    // Dev builds: the SDK appends the full commit; it is shortened to 7 hex digits.
    [InlineData("0.3.0+0123456789abcdef0123456789abcdef01234567", null, "0.3.0-dev+0123456")]
    [InlineData("0.3.0+ABCDEF0123", null, "0.3.0-dev+ABCDEF0")]
    [InlineData("0.3.0+abc123", null, "0.3.0-dev+abc123")]
    [InlineData("0.3.0+abc1234+abc1234", null, "0.3.0-dev+abc1234")]
    [InlineData("0.4.0-beta.1+0123456789abcdef", null, "0.4.0-beta.1.dev+0123456")]
    [InlineData("0.4.0-beta.1+sha.5114f85", null, "0.4.0-beta.1.dev+sha.5114f85")]
    // No informational version: the assembly version, then 0.0.0.
    [InlineData(null, "1.2.3.4", "1.2.3")]
    [InlineData("", "1.2", "1.2.0")]
    [InlineData("+abc1234", "2.0.1", "2.0.1")]
    [InlineData(null, null, "0.0.0")]
    public void Describe_FormatsReleaseAndDevBuilds(string? informational, string? assembly, string expected) =>
        Assert.Equal(expected, DottyVersion.Describe(informational, assembly is null ? null : Version.Parse(assembly)));

    [Fact]
    public void Describe_CurrentBuildIsASemVerString() =>
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", DottyVersion.Describe());
}
