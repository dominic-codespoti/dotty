using System;
using System.Collections.Generic;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>Who gets a reply, with which ids and message, under which quiet level - exact reply bytes.</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyResponsesTests
{
    private const string OnePixel = "AAAA";

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, true, false)]
    [InlineData(2, false, false)]
    [InlineData(3, true, false)]    // anything above 2 is as quiet as 2
    [InlineData(3, false, false)]
    public void QuietLevelMatrix(int quiet, bool success, bool expectReply)
    {
        using var rig = new KittyRig();
        rig.Apc($"i=1,s=1,v=1,f={(success ? 24 : 7)},q={quiet}", OnePixel);

        if (!expectReply)
        {
            Assert.Empty(rig.Replies);
            return;
        }
        string expected = success ? Ok("i=1") : Err("i=1", "EINVAL:Unknown image format");
        Assert.Equal(new[] { expected }, rig.Replies);
    }

    [Theory]
    [InlineData("s=1,v=1,f=24")]
    [InlineData("s=1,v=1,f=7")]
    [InlineData("s=1,v=1,f=24,p=5")]
    [InlineData("s=1,v=1,f=24,q=0,a=T")]
    [InlineData("s=0,v=1,f=24")]
    [InlineData("s=99999999999")]
    [InlineData("a=z")]
    public void WithoutAnIdOrNumberThereIsNeverAReply(string control)
    {
        using var rig = new KittyRig();
        rig.Apc(control, OnePixel);
        Assert.Empty(rig.Replies);
    }

    [Fact]
    public void IdReplyEchoesTheIdAndThePlacementId()
    {
        using var rig = new KittyRig();
        rig.Apc("i=7,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=8,p=9,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=10,p=11,s=1,v=1,f=7", OnePixel);
        Assert.Equal(new[]
        {
            Ok("i=7"),
            Ok("i=8,p=9"),
            Err("i=10,p=11", "EINVAL:Unknown image format"),
        }, rig.Replies);
    }

    [Fact]
    public void NumberReplyCarriesTheAssignedIdAndTheNumber_ErrorsOnlyTheNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("I=3,s=1,v=1,f=24", OnePixel);
        rig.Apc("I=3,p=2,s=1,v=1,f=24", OnePixel);
        rig.Apc("I=4,s=1,v=1,f=7", OnePixel);
        rig.Apc("I=4,p=2,s=1,v=1,f=24,o=q", OnePixel);
        Assert.Equal(new[]
        {
            Ok("i=2147483648,I=3"),
            Ok("i=2147483649,I=3,p=2"),
            Err("I=4", "EINVAL:Unknown image format"),
            Err("I=4,p=2", "EINVAL:Invalid key value"),
        }, rig.Replies);
    }

    [Fact]
    public void ReplyIdsAreFormattedWithoutLeadingZerosAtTheExtremes()
    {
        using var rig = new KittyRig();
        rig.Apc("i=4294967295,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=1,p=4294967295,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Ok("i=4294967295"), Ok("i=1,p=4294967295") }, rig.Replies);
    }

    [Fact]
    public void ErrorsCarryTheIdsOfTheFirstChunk()
    {
        using var rig = new KittyRig();
        rig.Apc("i=5,p=6,s=2,v=1,f=24,m=1", OnePixel);
        rig.Apc("m=0", "A!AA");
        Assert.Equal(new[] { Err("i=5,p=6", "EINVAL:Invalid base64 data") }, rig.Replies);
    }

    [Fact]
    public void ABothIdsErrorMentionsBothIdsAndThePlacement()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,I=2,p=3,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=1,I=2,q=2,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[] { Err("i=1,I=2,p=3", "EINVAL:Cannot specify both image id and image number") }, rig.Replies);
    }

    [Fact]
    public void AnOkIsNeverSentForIntermediateChunksOrStrayChunks()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=3,v=1,f=24,m=1", OnePixel);
        rig.Apc("m=1", OnePixel);
        Assert.Empty(rig.Replies);
        rig.Apc("m=0", OnePixel);
        rig.Apc("m=0", OnePixel);   // stray after completion
        rig.Apc("m=1", OnePixel);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void RepliesAreQueuedInCommandOrder()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=2,s=1,v=1,f=7", OnePixel);
        rig.Apc("i=3,s=1,v=1,a=q,f=24", OnePixel);
        rig.Apc("i=4,s=1,v=1,f=24", OnePixel);
        Assert.Equal(new[]
        {
            Ok("i=1"),
            Err("i=2", "EINVAL:Unknown image format"),
            Ok("i=3"),
            Ok("i=4"),
        }, rig.Replies);
    }

    [Fact]
    public void ARejectedControlBlockHonoursItsQuietKeyEvenWhenTheBadKeyComesFirst()
    {
        using var rig = new KittyRig();
        rig.Apc("s=99999999999,i=3,q=2");
        rig.Apc("s=99999999999,i=3,q=1");
        rig.Apc("s=99999999999,I=4,q=0");
        Assert.Equal(new[]
        {
            Err("i=3", "EINVAL:Invalid key value"),
            Err("I=4", "EINVAL:Invalid key value"),
        }, rig.Replies);
    }

    [Fact]
    public void EveryReplyIsAnApcWithTheGraphicsIntroducerAndAStringTerminator()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=1,v=1,f=24", OnePixel);
        rig.Apc("i=2,s=1,v=1,f=0", OnePixel);
        foreach (string reply in rig.Replies)
        {
            Assert.StartsWith("\u001b_G", reply);
            Assert.EndsWith("\u001b\\", reply);
            Assert.Equal(1, reply.Split(';').Length - 1);  // exactly one key/message separator
            Assert.True(reply.Length <= KittyResponse.MaxLength);
        }
    }
}
