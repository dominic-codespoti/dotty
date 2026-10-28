using System;
using System.Linq;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class KittyResponseTests
{
    private const string Esc = "\u001b";

    private static string Fmt(uint i, uint n, uint p, KittyStatus s, byte q = 0)
    {
        Span<char> buf = stackalloc char[KittyResponse.MaxLength];
        int len = KittyResponse.Format(buf, i, n, p, s, q);
        return buf.Slice(0, len).ToString();
    }

    [Fact]
    public void Ok_WithImageId_IsByteExact()
    {
        Assert.Equal(Esc + "_Gi=31;OK" + Esc + "\\", Fmt(31, 0, 0, KittyStatus.Ok));
    }

    [Fact]
    public void Error_WithImageId_IsByteExact()
    {
        Assert.Equal(Esc + "_Gi=31;ENOENT:Image not found" + Esc + "\\", Fmt(31, 0, 0, KittyStatus.ImageNotFound));
    }

    [Fact]
    public void ImageNumber_ReplyCarriesAssignedIdAndNumber()
    {
        Assert.Equal(Esc + "_Gi=2147483649,I=13;OK" + Esc + "\\", Fmt(2147483649u, 13, 0, KittyStatus.Ok));
    }

    [Fact]
    public void ImageNumberOnly_Error_OmitsMissingId()
    {
        Assert.Equal(Esc + "_GI=5;EINVAL:Invalid key value" + Esc + "\\", Fmt(0, 5, 0, KittyStatus.InvalidKeyValue));
    }

    [Fact]
    public void PlacementId_IsAppendedAfterImageId()
    {
        Assert.Equal(Esc + "_Gi=1,p=2;OK" + Esc + "\\", Fmt(1, 0, 2, KittyStatus.Ok));
        Assert.Equal(Esc + "_Gi=1,I=3,p=2;ENOPARENT:Parent placement not found" + Esc + "\\",
            Fmt(1, 3, 2, KittyStatus.NoParent));
    }

    [Fact]
    public void PlacementIdAlone_DoesNotTriggerAReply()
    {
        Assert.Equal("", Fmt(0, 0, 7, KittyStatus.Ok));
        Assert.Equal("", Fmt(0, 0, 7, KittyStatus.ImageNotFound));
    }

    [Fact]
    public void NoIds_NeverReplies_EvenForErrors()
    {
        foreach (KittyStatus s in Enum.GetValues<KittyStatus>())
            Assert.Equal("", Fmt(0, 0, 0, s));
    }

    [Fact]
    public void MaxUintIds_FormatWithoutTruncation()
    {
        Assert.Equal(Esc + "_Gi=4294967295,I=4294967295,p=4294967295;OK" + Esc + "\\",
            Fmt(uint.MaxValue, uint.MaxValue, uint.MaxValue, KittyStatus.Ok));
    }

    [Theory]
    // quiet, status-is-ok, expectReply
    [InlineData(0, true, true)]
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, true, false)]
    [InlineData(2, false, false)]
    [InlineData(200, false, false)]
    public void QuietMatrix(int quiet, bool ok, bool expectReply)
    {
        var status = ok ? KittyStatus.Ok : KittyStatus.StorageQuota;
        string s = Fmt(5, 0, 0, status, (byte)quiet);
        Assert.Equal(expectReply, s.Length > 0);
        Assert.Equal(expectReply, KittyResponse.ShouldReply((byte)quiet, status, 5, 0));
        if (expectReply)
            Assert.Equal(Esc + "_Gi=5;" + KittyResponse.Message(status) + Esc + "\\", s);
    }

    [Fact]
    public void ShouldReply_RequiresIdOrNumber()
    {
        Assert.False(KittyResponse.ShouldReply(0, KittyStatus.Ok, 0, 0));
        Assert.True(KittyResponse.ShouldReply(0, KittyStatus.Ok, 1, 0));
        Assert.True(KittyResponse.ShouldReply(0, KittyStatus.Ok, 0, 1));
    }

    [Fact]
    public void MessageTable_HasExactTextsForEveryStatus()
    {
        Assert.Equal("OK", KittyResponse.Message(KittyStatus.Ok));
        Assert.Equal("EINVAL:Invalid key value", KittyResponse.Message(KittyStatus.InvalidKeyValue));
        Assert.Equal("EINVAL:Unknown image format", KittyResponse.Message(KittyStatus.UnknownFormat));
        Assert.Equal("EINVAL:Invalid base64 data", KittyResponse.Message(KittyStatus.InvalidBase64));
        Assert.Equal("EINVAL:Unsupported transmission type", KittyResponse.Message(KittyStatus.UnsupportedMedium));
        Assert.Equal("ENOENT:Image not found", KittyResponse.Message(KittyStatus.ImageNotFound));
        Assert.Equal("ENOSPC:Storage quota exceeded", KittyResponse.Message(KittyStatus.StorageQuota));
        Assert.Equal("ENODATA:Insufficient image data", KittyResponse.Message(KittyStatus.InsufficientData));
        Assert.Equal("EFBIG:Image too large", KittyResponse.Message(KittyStatus.ImageTooLarge));
        Assert.Equal("EBADF:Failed to read image file", KittyResponse.Message(KittyStatus.BadFile));
        Assert.Equal("EILSEQ:Failed to decode image data", KittyResponse.Message(KittyStatus.DecodeFailed));
        Assert.StartsWith("ETOODEEP", KittyResponse.Message(KittyStatus.TooDeep));
        Assert.StartsWith("ECYCLE", KittyResponse.Message(KittyStatus.Cycle));
        Assert.StartsWith("ENOPARENT", KittyResponse.Message(KittyStatus.NoParent));
        Assert.StartsWith("EBADPNG", KittyResponse.Message(KittyStatus.BadPng));
    }

    [Fact]
    public void EveryStatus_HasDistinctAsciiMessage_WithWellFormedCode()
    {
        var all = Enum.GetValues<KittyStatus>();
        var messages = all.Select(KittyResponse.Message).ToArray();
        Assert.Equal(messages.Length, messages.Distinct().Count());
        string[] codes = { "EINVAL", "ENOENT", "ENOSPC", "ENODATA", "EFBIG", "EBADF", "EILSEQ", "ETOODEEP", "ECYCLE", "ENOPARENT", "EBADPNG" };
        foreach (var s in all)
        {
            string m = KittyResponse.Message(s);
            Assert.All(m, c => Assert.InRange(c, ' ', '~'));
            Assert.DoesNotContain(';', m);
            if (s == KittyStatus.Ok) { Assert.Equal("OK", m); continue; }
            int colon = m.IndexOf(':');
            Assert.True(colon > 0, m);
            Assert.Contains(m.Substring(0, colon), codes);
            Assert.True(m.Length > colon + 1, m);
        }
    }

    [Fact]
    public void LongestPossibleReply_FitsWithinMaxLength()
    {
        int worst = 0;
        foreach (KittyStatus s in Enum.GetValues<KittyStatus>())
            worst = Math.Max(worst, Fmt(uint.MaxValue, uint.MaxValue, uint.MaxValue, s).Length);
        Assert.InRange(worst, 1, KittyResponse.MaxLength);
        Assert.Equal(192, KittyResponse.MaxLength);
    }

    [Fact]
    public void Format_WritesOnlyTheReturnedPrefix()
    {
        var buf = new char[KittyResponse.MaxLength + 8];
        Array.Fill(buf, '#');
        int len = KittyResponse.Format(buf, 1, 0, 0, KittyStatus.Ok, 0);
        Assert.Equal(Esc + "_Gi=1;OK" + Esc + "\\", new string(buf, 0, len));
        Assert.All(buf.Skip(len), c => Assert.Equal('#', c));
    }

    [Fact]
    public void Format_RejectsTooSmallBuffer()
    {
        var small = new char[KittyResponse.MaxLength - 1];
        Assert.Throws<ArgumentException>(() => KittyResponse.Format(small, 1, 0, 0, KittyStatus.Ok, 0));
        // an exactly-MaxLength buffer works
        var exact = new char[KittyResponse.MaxLength];
        Assert.True(KittyResponse.Format(exact, 1, 0, 0, KittyStatus.Ok, 0) > 0);
    }

    [Fact]
    public void Suppressed_ReturnsZeroAndLeavesBufferUntouched()
    {
        var buf = new char[KittyResponse.MaxLength];
        Array.Fill(buf, '#');
        Assert.Equal(0, KittyResponse.Format(buf, 1, 0, 0, KittyStatus.Ok, 2));
        Assert.All(buf, c => Assert.Equal('#', c));
    }

    [Fact]
    public void Message_UnknownStatus_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KittyResponse.Message((KittyStatus)250));
    }

    [Fact]
    public void ResponseFromParsedCommand_RoundTripsIds()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("a=T,i=7,p=3,q=1,f=24");
        Assert.Equal(KittyParseResult.Ok, KittyControlParser.TryParse(bytes, out var c));
        Assert.Equal("", Fmt(c.ImageId, c.ImageNumber, c.PlacementId, KittyStatus.Ok, c.Quiet));
        Assert.Equal(Esc + "_Gi=7,p=3;EINVAL:Unknown image format" + Esc + "\\",
            Fmt(c.ImageId, c.ImageNumber, c.PlacementId, KittyStatus.UnknownFormat, c.Quiet));
    }

    [Fact]
    public void Format_DoesNotAllocate()
    {
        var buf = new char[KittyResponse.MaxLength];
        AllocationAssert.NoAllocations(() =>
        {
            KittyResponse.Format(buf, 4000000000u, 99, 12, KittyStatus.TooDeep, 0);
            KittyResponse.Format(buf, 1, 0, 0, KittyStatus.Ok, 0);
        });
    }
}
