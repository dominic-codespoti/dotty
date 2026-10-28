using System;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class HyperlinkUriTests
{
    [Theory]
    [InlineData(0x00, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x07, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x09, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x0A, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x0D, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x1B, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x7F, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x80, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x85, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x9F, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x2028, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x2029, HyperlinkUriStatus.ControlChar)]
    [InlineData(0x00AD, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x200B, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x200F, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x202A, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x202E, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x2060, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x2064, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x2066, HyperlinkUriStatus.FormatChar)]
    [InlineData(0x2069, HyperlinkUriStatus.FormatChar)]
    [InlineData(0xFEFF, HyperlinkUriStatus.FormatChar)]
    public void Validate_RejectsForbiddenCharacters(int code, HyperlinkUriStatus expected)
    {
        string uri = "https://example.com/a" + (char)code + "b";

        Assert.Equal(expected, HyperlinkUri.Validate(uri));
    }

    [Fact]
    public void Validate_RejectsLoneSurrogates()
    {
        Assert.Equal(HyperlinkUriStatus.FormatChar, HyperlinkUri.Validate("https://a/\uD800"));
        Assert.Equal(HyperlinkUriStatus.FormatChar, HyperlinkUri.Validate("https://a/\uD800x"));
        Assert.Equal(HyperlinkUriStatus.FormatChar, HyperlinkUri.Validate("https://a/\uDC00"));
    }

    [Fact]
    public void Validate_AcceptsPrintableAsciiSpaceUnicodeAndSurrogatePairs()
    {
        Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate("https://example.com/a b?x=1&y=2#frag"));
        Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate("https://b\u00FCcher.de/\u4E2D\u6587"));
        Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate("https://example.com/\U0001F600"));
        Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate("https://example.com/\uFFFD"));
        Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate("javascript:alert('x')"));
    }

    [Fact]
    public void Validate_EnforcesLengthBoundary()
    {
        Assert.Equal(HyperlinkUriStatus.Empty, HyperlinkUri.Validate(string.Empty));
        Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate(new string('a', HyperlinkUri.MaxLength)));
        Assert.Equal(HyperlinkUriStatus.TooLong, HyperlinkUri.Validate(new string('a', HyperlinkUri.MaxLength + 1)));
    }

    [Fact]
    public void TryParseOsc8Payload_SplitsParamsAndUriWithSemicolons()
    {
        Assert.True(HyperlinkUri.TryParseOsc8Payload("id=a:b;uri;with;semis", out var id, out var uri));

        Assert.Equal("a", id.ToString());
        Assert.Equal("uri;with;semis", uri.ToString());
    }

    [Fact]
    public void TryParseOsc8Payload_HandlesCloseAndMissingId()
    {
        Assert.True(HyperlinkUri.TryParseOsc8Payload(";", out var id, out var uri));
        Assert.True(id.IsEmpty);
        Assert.True(uri.IsEmpty);

        Assert.True(HyperlinkUri.TryParseOsc8Payload("id=x;", out id, out uri));
        Assert.Equal("x", id.ToString());
        Assert.True(uri.IsEmpty);

        Assert.True(HyperlinkUri.TryParseOsc8Payload("foo=bar;https://example.com", out id, out uri));
        Assert.True(id.IsEmpty);
        Assert.Equal("https://example.com", uri.ToString());

        Assert.False(HyperlinkUri.TryParseOsc8Payload("https://example.com", out _, out _));
        Assert.False(HyperlinkUri.TryParseOsc8Payload(string.Empty, out _, out _));
    }

    [Fact]
    public void TryParseOsc8Payload_EnforcesIdLength()
    {
        Assert.True(HyperlinkUri.TryParseOsc8Payload(
            "id=" + new string('a', HyperlinkUri.MaxIdLength) + ";https://example.com", out var id, out _));
        Assert.Equal(HyperlinkUri.MaxIdLength, id.Length);

        Assert.False(HyperlinkUri.TryParseOsc8Payload(
            "id=" + new string('a', HyperlinkUri.MaxIdLength + 1) + ";https://example.com", out _, out _));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x0A)]
    [InlineData(0x85)]
    [InlineData(0x202E)]
    public void TryParseOsc8Payload_RejectsControlAndFormatCharsInParams(int code)
    {
        string payload = "id=a" + (char)code + "b;https://example.com";

        Assert.False(HyperlinkUri.TryParseOsc8Payload(payload, out _, out _));
    }

    [Fact]
    public void ValidateAndParse_DoNotAllocate()
    {
        const string payload = "id=abc:foo=bar;https://example.com/some/long/path?query=value";
        string clean = "https://example.com/path";
        string dirty = "https://example.com/pa\nth";

        AllocationAssert.NoAllocations(() =>
        {
            Assert.True(HyperlinkUri.TryParseOsc8Payload(payload, out var id, out var uri));
            Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate(uri));
            Assert.Equal(HyperlinkUriStatus.Ok, HyperlinkUri.Validate(clean));
            Assert.Equal(HyperlinkUriStatus.ControlChar, HyperlinkUri.Validate(dirty));
            Assert.Equal(3, id.Length);
        });
    }
}
