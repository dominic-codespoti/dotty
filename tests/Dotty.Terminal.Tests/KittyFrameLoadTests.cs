using System.Collections.Generic;
using Dotty.Abstractions.Graphics;
using System.Linq;
using System.Text;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>Helpers shared by the animation test classes.</summary>
internal static class AnimKit
{
    public static readonly byte[] Red = { 255, 0, 0, 255 };
    public static readonly byte[] Clear = { 0, 0, 0, 0 };

    public static byte[] Cat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var part in parts)
            all.AddRange(part);
        return all.ToArray();
    }

    public static byte[] Rep(byte[] pixel, int count) => Cat(Enumerable.Repeat(pixel, count).ToArray());

    public static KittyImage Img(KittyRig rig, uint id)
    {
        var image = rig.Store.FindByClientId(id);
        Assert.NotNull(image);
        return image!;
    }

    public static byte[] CanvasOf(KittyImage image, int index) => image.Frames[index].Canvas.AsSpan().ToArray();

    /// <summary>Transmits a straight-alpha RGBA image (<c>f=32</c>); <paramref name="extra"/> starts with a comma.</summary>
    public static void Transmit(KittyRig rig, uint id, int w, int h, byte[] rgba, string extra = "") =>
        rig.Apc($"i={id},s={w},v={h},f=32{extra}", B64(rgba));

    /// <summary>An <c>a=f</c> command with a straight-alpha RGBA payload.</summary>
    public static void LoadFrame(KittyRig rig, uint id, int w, int h, byte[] rgba, string extra = "") =>
        rig.Apc($"a=f,i={id},s={w},v={h},f=32{extra}", B64(rgba));

    /// <summary>A rig with a 2x2 opaque red image with id 1 and the replies cleared.</summary>
    public static KittyRig RedRig(KittyGraphicsOptions? options = null)
    {
        var rig = new KittyRig(options);
        Transmit(rig, 1, 2, 2, Rep(Red, 4));
        rig.Replies.Clear();
        return rig;
    }
}

/// <summary><c>a=f</c>: frame creation, editing, base frames, gaps, errors, quota, chunking, media, ids and replies.</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyFrameLoadTests
{
    private static readonly byte[] Blue = { 0, 0, 255, 255 };

    // ----------------------------------------------------------------------------------------------- new frames

    [Fact]
    public void NewFrameStartsTransparentAndTheDataLandsAtXY()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",x=1,y=0");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        var image = AnimKit.Img(rig, 1);
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(AnimKit.Cat(AnimKit.Clear, Blue, AnimKit.Clear, AnimKit.Clear), AnimKit.CanvasOf(image, 1));
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, image.Frames[1].Canvas.Format);
        Assert.Equal(KittyAnimation.DefaultGapMs, image.Frames[1].GapMs);
        Assert.Equal(0, image.CurrentFrameIndex);
        Assert.Equal(32, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData("0", 0, 0, 0, 0)]                // default: transparent black
    [InlineData("4278190208", 128, 0, 0, 128)]   // 0xFF000080: red at alpha 128 -> 255*128/255 = 128
    [InlineData("16711935", 0, 255, 0, 255)]     // 0x00FF00FF: opaque green
    [InlineData("4294967040", 0, 0, 0, 0)]       // 0xFFFFFF00: alpha 0 premultiplies to zero whatever the colour
    [InlineData("2155905152", 64, 64, 64, 128)]  // 0x80808080: 128*128/255 = 64.25 -> 64
    public void BackgroundColourFillsANewFramePremultiplied(string y, int r, int g, int b, int a)
    {
        using var rig = AnimKit.RedRig();
        // a fully transparent 1x1 patch changes nothing, so the canvas is the pure background fill
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, $",Y={y}");

        var image = AnimKit.Img(rig, 1);
        Assert.Equal(AnimKit.Rep(new[] { (byte)r, (byte)g, (byte)b, (byte)a }, 4), AnimKit.CanvasOf(image, 1));
    }

    [Fact]
    public void BackgroundColourIsIgnoredWhenABaseFrameIsGiven()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, ",c=1,Y=16711935");

        Assert.Equal(AnimKit.Rep(AnimKit.Red, 4), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));
    }

    [Fact]
    public void DataIsBlendedOntoTheBaseFrameByDefaultAndOverwritesWithX1()
    {
        using var rig = AnimKit.RedRig();
        byte[] translucentGreen = { 0, 255, 0, 128 }; // stored premultiplied as (0, 128, 0, 128)
        AnimKit.LoadFrame(rig, 1, 1, 1, translucentGreen, ",c=1");               // frame 2: blend
        AnimKit.LoadFrame(rig, 1, 1, 1, translucentGreen, ",c=1,X=1");           // frame 3: overwrite

        var image = AnimKit.Img(rig, 1);
        // blend over opaque red: src + round(dst * 127 / 255): R = 0 + 127, G = 128 + 0, A = 128 + 127
        Assert.Equal(AnimKit.Cat(new byte[] { 127, 128, 0, 255 }, AnimKit.Red, AnimKit.Red, AnimKit.Red), AnimKit.CanvasOf(image, 1));
        Assert.Equal(AnimKit.Cat(new byte[] { 0, 128, 0, 128 }, AnimKit.Red, AnimKit.Red, AnimKit.Red), AnimKit.CanvasOf(image, 2));
    }

    [Fact]
    public void NewFrameCopiesTheBaseFrameAndStaysIndependentOfIt()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",x=1,y=0");                              // frame 2
        AnimKit.LoadFrame(rig, 1, 1, 1, new byte[] { 255, 255, 255, 255 }, ",c=2,x=0,y=1"); // frame 3 = frame 2 + white
        AnimKit.LoadFrame(rig, 1, 1, 1, new byte[] { 1, 2, 3, 255 }, ",r=3,x=1,y=1,X=1");  // edit frame 3 only

        var image = AnimKit.Img(rig, 1);
        Assert.Equal(AnimKit.Cat(AnimKit.Clear, Blue, AnimKit.Clear, AnimKit.Clear), AnimKit.CanvasOf(image, 1));
        Assert.Equal(AnimKit.Cat(AnimKit.Clear, Blue, new byte[] { 255, 255, 255, 255 }, new byte[] { 1, 2, 3, 255 }), AnimKit.CanvasOf(image, 2));
        Assert.NotEqual(image.Frames[1].Canvas.Serial, image.Frames[2].Canvas.Serial);
    }

    [Fact]
    public void RgbPayloadGainsOpaqueAlpha()
    {
        using var rig = AnimKit.RedRig();
        rig.Apc("a=f,i=1,s=1,v=1,f=24", B64(9, 8, 7));

        var image = AnimKit.Img(rig, 1);
        Assert.Equal(AnimKit.Cat(new byte[] { 9, 8, 7, 255 }, AnimKit.Clear, AnimKit.Clear, AnimKit.Clear), AnimKit.CanvasOf(image, 1));
    }

    [Fact]
    public void PngPayloadIsPremultipliedAndSizedByItsOwnDimensions()
    {
        using var rig = AnimKit.RedRig();
        byte[] png = TestPng.Build(1, 1, new byte[] { 10, 20, 30, 128 });
        rig.Apc("a=f,i=1,f=100,x=1,y=1", B64(png));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        // 10*128/255 = 5.02, 20*128/255 = 10.04, 30*128/255 = 15.06
        Assert.Equal(AnimKit.Cat(AnimKit.Clear, AnimKit.Clear, AnimKit.Clear, new byte[] { 5, 10, 15, 128 }), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));
    }

    [Fact]
    public void CompressedPayloadIsInflated()
    {
        using var rig = AnimKit.RedRig();
        byte[] raw = AnimKit.Rep(Blue, 4);
        byte[] zipped = TestPng.Zlib(raw);
        rig.Apc("a=f,i=1,s=2,v=2,f=32,o=z", B64(zipped));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(raw, AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));
    }

    // ------------------------------------------------------------------------------------------------ editing

    [Fact]
    public void EditingAFrameKeepsTheRestAndSwapsInANewBuffer()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 2, 2, AnimKit.Rep(Blue, 4));  // frame 2: all blue
        var image = AnimKit.Img(rig, 1);
        var before = image.Frames[1].Canvas.Retain();            // e.g. a render lease
        long serial = before.Serial;
        rig.Replies.Clear();

        AnimKit.LoadFrame(rig, 1, 1, 1, new byte[] { 9, 9, 9, 255 }, ",r=2,x=1,y=1");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(AnimKit.Cat(Blue, Blue, Blue, new byte[] { 9, 9, 9, 255 }), AnimKit.CanvasOf(image, 1));
        Assert.NotEqual(serial, image.Frames[1].Canvas.Serial);
        Assert.Equal(AnimKit.Rep(Blue, 4), before.AsSpan().ToArray()); // the retained buffer was not mutated
        before.Release();
        Assert.Equal(32, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void EditingTheRootConvertsAnRgbImageToRgba()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24", B64(1, 2, 3, 4, 5, 6));
        var image = AnimKit.Img(rig, 1);
        Assert.Equal(6, rig.Store.UsedBytes);
        long serial = image.Root.Serial;
        rig.Replies.Clear();

        AnimKit.LoadFrame(rig, 1, 1, 1, new byte[] { 10, 20, 30, 255 }, ",r=1,x=1,y=0");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(1, image.Frames.Count);
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, image.Root.Format);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 10, 20, 30, 255 }, image.Root.AsSpan().ToArray());
        Assert.NotEqual(serial, image.Root.Serial);
        Assert.Equal(8, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void EditWithABaseFrameReplacesTheTargetByACopyOfTheBase()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 2, 2, AnimKit.Rep(Blue, 4));   // frame 2 blue
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, ",r=2,c=1"); // frame 2 := copy of frame 1 (red), nothing drawn

        Assert.Equal(AnimKit.Rep(AnimKit.Red, 4), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));
    }

    // ----------------------------------------------------------------------------------------------- gap rules

    [Theory]
    [InlineData("", 40)]        // absent
    [InlineData(",z=0", 40)]
    [InlineData(",z=1", 1)]
    [InlineData(",z=250", 250)]
    [InlineData(",z=-1", 0)]    // gapless
    [InlineData(",z=-100", 0)]
    public void NewFrameGapFollowsTheZKey(string z, int expectedGap)
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, z);

        Assert.Equal(expectedGap, AnimKit.Img(rig, 1).Frames[1].GapMs);
    }

    [Theory]
    [InlineData("", 77)]        // an edit without z keeps the gap
    [InlineData(",z=0", 77)]
    [InlineData(",z=5", 5)]
    [InlineData(",z=-3", 0)]
    public void EditGapIsKeptUnlessZIsGiven(string z, int expectedGap)
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, ",z=77");
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, ",r=2" + z);

        Assert.Equal(expectedGap, AnimKit.Img(rig, 1).Frames[1].GapMs);
    }

    // --------------------------------------------------------------------------------------------------- errors

    [Fact]
    public void UnknownImageAnswersEnoent()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 9, 1, 1, AnimKit.Clear);

        Assert.Equal(new[] { Err("i=9", "ENOENT:Image not found") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
    }

    [Theory]
    [InlineData(",r=2")]     // only frame 1 exists: no frame 2 to edit
    [InlineData(",r=99")]
    [InlineData(",c=2")]     // base frame missing
    [InlineData(",c=4294967295")]
    public void MissingFramesAnswerEnoentAndChangeNothing(string keys)
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear, keys);

        Assert.Equal(new[] { Err("i=1", "ENOENT:Frame not found") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.Equal(16, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData(2, 2, ",x=1")]            // right edge
    [InlineData(2, 2, ",y=1")]            // bottom edge
    [InlineData(1, 1, ",x=2")]            // origin outside
    [InlineData(1, 1, ",y=2")]
    [InlineData(1, 1, ",x=4294967295")]   // no 32-bit wrap-around
    [InlineData(3, 1, "")]                // wider than the image
    public void RectanglesOutsideTheImageAreRejected(int w, int h, string keys)
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, w, h, AnimKit.Rep(Blue, w * h), keys);

        Assert.Equal(new[] { Err("i=1", "EINVAL:Rectangle out of bounds") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.Equal(16, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void RectangleTouchingTheLastPixelIsAccepted()
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",x=1,y=1");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void PayloadErrorsUseTheTransmitMessages()
    {
        using var rig = AnimKit.RedRig();
        rig.Apc("a=f,i=1,s=1,v=1,f=32", B64(1, 2, 3));            // 3 of 4 bytes
        rig.Apc("a=f,i=1,s=1,v=1,f=77", B64(1, 2, 3, 4));         // unknown format
        rig.Apc("a=f,i=1,v=1,f=32", B64(1, 2, 3, 4));             // no width
        rig.Apc("a=f,i=1,s=1,v=1,f=32", "!!!!");                   // bad base64

        Assert.Equal(new[]
        {
            Err("i=1", "ENODATA:Insufficient image data"),
            Err("i=1", "EINVAL:Unknown image format"),
            Err("i=1", "EINVAL:Invalid key value"),
            Err("i=1", "EINVAL:Invalid base64 data"),
        }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.Equal(16, rig.Store.UsedBytes);
    }

    // ---------------------------------------------------------------------------------------------------- quota

    [Theory]
    [InlineData(47, false)]   // root 16 + frame 16 + frame 16 = 48
    [InlineData(48, true)]
    public void FrameQuotaBoundaryIsExact(int limit, bool thirdFits)
    {
        using var rig = AnimKit.RedRig();
        rig.Store.SetStorageLimit(limit);
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear);
        rig.Replies.Clear();
        AnimKit.LoadFrame(rig, 1, 1, 1, AnimKit.Clear);

        var image = AnimKit.Img(rig, 1);
        if (thirdFits)
        {
            Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
            Assert.Equal(3, image.Frames.Count);
            Assert.Equal(48, rig.Store.UsedBytes);
        }
        else
        {
            Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, rig.Replies);
            Assert.Equal(2, image.Frames.Count);
            Assert.Equal(32, rig.Store.UsedBytes);
        }
        rig.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData(15, false)]   // 12-byte RGB root grows by 4 bytes when edited
    [InlineData(16, true)]
    public void EditingAnRgbRootCountsTheGrowthAgainstTheQuota(int limit, bool fits)
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=2,f=24", B64(AnimKit.Rep(new byte[] { 1, 2, 3 }, 4)));
        rig.Store.SetStorageLimit(limit);
        rig.Replies.Clear();

        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",r=1");

        var image = AnimKit.Img(rig, 1);
        Assert.Equal(fits ? ImagePixelFormat.Rgba8Premultiplied : ImagePixelFormat.Rgb8, image.Root.Format);
        Assert.Equal(fits ? 16 : 12, rig.Store.UsedBytes);
        Assert.Equal(new[] { fits ? Ok("i=1") : Err("i=1", "ENOSPC:Storage quota exceeded") }, rig.Replies);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void GrowingAnImageEvictsOtherImagesButNeverItself()
    {
        using var rig = new KittyRig();
        rig.Store.SetStorageLimit(40);
        AnimKit.Transmit(rig, 1, 2, 2, AnimKit.Rep(AnimKit.Red, 4));
        AnimKit.Transmit(rig, 2, 2, 2, AnimKit.Rep(Blue, 4));
        rig.Replies.Clear();

        AnimKit.LoadFrame(rig, 2, 1, 1, AnimKit.Clear); // 16 + 32 = 48 > 40: image 1 is the only victim

        Assert.Equal(new[] { Ok("i=2") }, rig.Replies);
        Assert.Null(rig.Store.FindByClientId(1));
        Assert.Equal(2, AnimKit.Img(rig, 2).Frames.Count);
        Assert.Equal(32, rig.Store.UsedBytes);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void FrameCountPerImageIsCapped()
    {
        using var rig = new KittyRig();
        AnimKit.Transmit(rig, 1, 1, 1, AnimKit.Red);
        for (int i = 1; i < KittyAnimation.MaxFramesPerImage; i++)
            rig.Apc("a=f,i=1,s=1,v=1,f=32", B64(AnimKit.Red));
        rig.Replies.Clear();
        var image = AnimKit.Img(rig, 1);
        Assert.Equal(KittyAnimation.MaxFramesPerImage, image.Frames.Count);

        rig.Apc("a=f,i=1,s=1,v=1,f=32", B64(AnimKit.Red));

        Assert.Equal(new[] { Err("i=1", "ENOSPC:Storage quota exceeded") }, rig.Replies);
        Assert.Equal(KittyAnimation.MaxFramesPerImage, image.Frames.Count);
        rig.Store.ValidateInvariants();
    }

    // -------------------------------------------------------------------------------------------- chunked loads

    private static readonly byte[] TwoPixels = { 10, 20, 30, 255, 40, 50, 60, 255 };

    private static void AssertTwoPixelFrameAtRow1(KittyImage image)
    {
        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(AnimKit.Cat(AnimKit.Clear, AnimKit.Clear, TwoPixels), AnimKit.CanvasOf(image, 1));
    }

    [Fact]
    public void ChunkedFrameLoadRepeatingTheActionKeyCompletesWithOneReply()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels); // 12 chars
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        Assert.Empty(rig.Replies);
        Assert.True(rig.Controller.HasOpenTransfer);
        rig.Apc("a=f,m=0", b64.Substring(8));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.False(rig.Controller.HasOpenTransfer);
        AssertTwoPixelFrameAtRow1(AnimKit.Img(rig, 1));
    }

    [Fact]
    public void ChunkedFrameLoadWithBareContinuationsCompletes()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 4));
        rig.Apc("m=1", b64.Substring(4, 4));
        rig.Apc("m=0", b64.Substring(8));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        AssertTwoPixelFrameAtRow1(AnimKit.Img(rig, 1));
    }

    [Fact]
    public void LaterChunkQuietOverridesTheFirst()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        rig.Apc("a=f,m=0,q=1", b64.Substring(8));

        Assert.Empty(rig.Replies);
        Assert.Equal(2, AnimKit.Img(rig, 1).Frames.Count);
    }

    [Fact]
    public void AnotherCommandAbortsTheOpenFrameLoadAndTheRestIsDropped()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        rig.Apc("a=p,i=1");                                  // a completed put ends the load
        rig.Replies.Clear();
        rig.Apc("a=f,m=0", b64.Substring(8));                // stray continuation: silent

        Assert.Empty(rig.Replies);
        Assert.False(rig.Controller.HasOpenTransfer);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
    }

    [Fact]
    public void ACancelledChunkKillsTheFrameLoad()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        rig.CancelledApc("a=f,m=1", "AAAA");
        rig.Apc("a=f,m=0", b64.Substring(8));

        Assert.Empty(rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Fact]
    public void ANewFirstChunkReplacesTheOpenFrameLoad()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=0,m=1", "AAAA");                 // abandoned
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));   // not a continuation: it carries i
        rig.Apc("a=f,m=0", b64.Substring(8));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        AssertTwoPixelFrameAtRow1(AnimKit.Img(rig, 1));
    }

    [Fact]
    public void ATransmitDuringAFrameLoadAbortsItAndStoresTheNewImage()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        AnimKit.Transmit(rig, 2, 1, 1, Blue);
        rig.Apc("a=f,m=0", b64.Substring(8));

        Assert.Equal(new[] { Ok("i=2") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.NotNull(rig.Store.FindByClientId(2));
    }

    [Fact]
    public void ABadChunkInTheMiddleFailsTheLoadOnceWithTheFirstChunksIds()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        rig.Apc("m=1", "!!!!");
        rig.Apc("m=0", b64.Substring(8));

        Assert.Equal(new[] { Err("i=1", "EINVAL:Invalid base64 data") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Fact]
    public void ARunOutOfDataAtTheFinalChunkAnswersEnodata()
    {
        using var rig = AnimKit.RedRig();
        string b64 = B64(TwoPixels);
        rig.Apc("a=f,i=1,s=2,v=1,f=32,y=1,m=1", b64.Substring(0, 8));
        rig.Apc("m=0");

        Assert.Equal(new[] { Err("i=1", "ENODATA:Insufficient image data") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
    }

    [Fact]
    public void AStrayFrameContinuationWithoutAnOpenLoadIsSilent()
    {
        using var rig = AnimKit.RedRig();
        rig.Apc("a=f,m=0", "AAAA");

        Assert.Empty(rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
    }

    [Fact]
    public void ATransmitChunkSequenceStillIgnoresARepeatedTransmitAction()
    {
        // a=t is not a frame load: a later "a=t,m=0" is a new transmit command, not a continuation.
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=32,m=1", "AAAA");
        Assert.True(rig.Controller.HasOpenTransfer);
        rig.Apc("a=t,m=0", "AAAA");

        Assert.False(rig.Controller.HasOpenTransfer);
        Assert.Null(rig.Store.FindByClientId(1));
    }

    // ---------------------------------------------------------------------------------------------------- media

    [Fact]
    public void FileMediumFramesGoThroughTheSameReaderAndSizeRules()
    {
        var reader = new FakeMediumReader { Handler = () => FakeMediumReader.Ok(Blue) };
        using var rig = new KittyMediaRig(reader);
        rig.Apc("i=1,s=2,v=2,f=32", B64(AnimKit.Rep(AnimKit.Red, 4)));
        rig.Replies.Clear();

        rig.Apc("a=f,i=1,s=1,v=1,f=32,t=f,x=1,y=1", B64(Encoding.ASCII.GetBytes("/tmp/pixel.rgba")));

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        var call = Assert.Single(reader.Calls);
        Assert.Equal('f', call.Medium);
        var image = rig.Store.FindByClientId(1)!;
        Assert.Equal(AnimKit.Cat(AnimKit.Clear, AnimKit.Clear, AnimKit.Clear, Blue), image.Frames[1].Canvas.AsSpan().ToArray());

        // a file that is too short fails like a transmit does
        reader.Handler = () => FakeMediumReader.Ok(new byte[] { 1, 2, 3 });
        rig.Replies.Clear();
        rig.Apc("a=f,i=1,s=1,v=1,f=32,t=f", B64(Encoding.ASCII.GetBytes("/tmp/short.rgba")));
        Assert.Equal(new[] { Err("i=1", "ENODATA:Insufficient image data") }, rig.Replies);
        Assert.Equal(2, image.Frames.Count);
    }

    [Fact]
    public void DisabledMediumIsRefusedForFramesToo()
    {
        using var rig = new KittyRig(new KittyGraphicsOptions { Enabled = true }); // direct only
        AnimKit.Transmit(rig, 1, 1, 1, AnimKit.Red);
        rig.Replies.Clear();

        rig.Apc("a=f,i=1,s=1,v=1,f=32,t=f", B64(Encoding.ASCII.GetBytes("/tmp/x")));

        Assert.Equal(new[] { Err("i=1", "EINVAL:Unsupported transmission type") }, rig.Replies);
    }

    // ----------------------------------------------------------------------------- addressing, replies, hints

    [Fact]
    public void ImageNumberAddressesTheNewestImageAndTheReplyCarriesBothIds()
    {
        using var rig = new KittyRig();
        rig.Apc("I=7,s=1,v=1,f=32", B64(AnimKit.Red));  // gets the first allocated client id
        rig.Replies.Clear();

        rig.Apc("a=f,I=7,s=1,v=1,f=32", B64(Blue));

        Assert.Equal(new[] { Ok("i=2147483648,I=7") }, rig.Replies);
        Assert.Equal(2, rig.Store.FindByNumber(7)!.Frames.Count);
    }

    [Fact]
    public void UnknownImageNumberAnswersEnoentWithTheNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("a=f,I=7,s=1,v=1,f=32", B64(Blue));

        Assert.Equal(new[] { Err("I=7", "ENOENT:Image not found") }, rig.Replies);
    }

    [Fact]
    public void BothIdAndNumberIsAnError()
    {
        using var rig = AnimKit.RedRig();
        rig.Apc("a=f,i=1,I=2,s=1,v=1,f=32", B64(Blue));

        Assert.Equal(new[] { Err("i=1,I=2", "EINVAL:Cannot specify both image id and image number") }, rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
    }

    [Fact]
    public void WithoutAnIdNothingIsAddressableAndNothingIsAnswered()
    {
        using var rig = AnimKit.RedRig();
        rig.Apc("a=f,s=1,v=1,f=32", B64(Blue));

        Assert.Empty(rig.Replies);
        Assert.Equal(1, AnimKit.Img(rig, 1).Frames.Count);
        Assert.False(rig.Controller.HasOpenTransfer);
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]   // errors only
    [InlineData(2, false, false)]  // silence
    public void QuietLevelsApplyToFrameReplies(int quiet, bool okReply, bool errReply)
    {
        using var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, $",q={quiet}");
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, $",q={quiet},r=9");

        var expected = new List<string>();
        if (okReply) expected.Add(Ok("i=1"));
        if (errReply) expected.Add(Err("i=1", "ENOENT:Frame not found"));
        Assert.Equal(expected, rig.Replies);
    }

    [Fact]
    public void TransientHintMarksTheImageTransient()
    {
        using var rig = AnimKit.RedRig();
        var image = AnimKit.Img(rig, 1);
        Assert.False(image.Transient);

        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",N=1");

        Assert.True(image.Transient);
    }

    [Fact]
    public void SuccessfulFrameLoadsNotifyTheHostAndFailuresDoNot()
    {
        using var rig = AnimKit.RedRig();
        int before = rig.Host.GraphicsChangedCalls;
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",r=9");
        Assert.Equal(before, rig.Host.GraphicsChangedCalls);

        AnimKit.LoadFrame(rig, 1, 1, 1, Blue);
        Assert.Equal(before + 1, rig.Host.GraphicsChangedCalls);
    }

    // ------------------------------------------------------------------------------- lifetime and accounting

    [Fact]
    public void RetransmittingTheIdDropsAllFramesAndTheirBytes()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = AnimKit.RedRig())
        {
            AnimKit.LoadFrame(rig, 1, 1, 1, Blue);
            AnimKit.LoadFrame(rig, 1, 1, 1, Blue);
            var old = AnimKit.Img(rig, 1);
            Assert.Equal(3, old.Frames.Count);
            Assert.Equal(48, rig.Store.UsedBytes);

            AnimKit.Transmit(rig, 1, 1, 1, Blue);

            var fresh = AnimKit.Img(rig, 1);
            Assert.NotSame(old, fresh);
            Assert.True(old.IsDeleted);
            Assert.Equal(1, fresh.Frames.Count);
            Assert.Equal(0, fresh.CurrentFrameIndex);
            Assert.Equal(4, rig.Store.UsedBytes);
            Assert.Equal(1, rig.Store.Count);
            rig.Store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void MetricsReturnToBaselineAfterFramesAreLoadedEditedAndDisposed()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = AnimKit.RedRig())
        {
            for (int i = 0; i < 5; i++)
                AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",c=1");
            AnimKit.LoadFrame(rig, 1, 2, 2, AnimKit.Rep(Blue, 4), ",r=3");
            rig.Apc("a=f,i=1,s=1,v=1,f=32,r=99", B64(Blue)); // error path
            rig.Apc("a=f,i=1,s=3,v=1,f=32", B64(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)); // out-of-bounds path
            Assert.Equal(6, AnimKit.Img(rig, 1).Frames.Count);
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void StoreRefusesFramesForADeletedImage()
    {
        using var store = new KittyImageStore(10_000);
        var pixels = ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgba8Premultiplied);
        Assert.True(store.TryAdd(pixels, 1, 0, false, 0, out var image));
        var data = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgba8Premultiplied);
        try
        {
            Assert.True(store.Delete(image!));
            Assert.Equal(KittyStatus.ImageNotFound, store.LoadFrame(image!, KittyCommands.Parse("a=f,i=1,s=1,v=1"), data, 0));
            Assert.Equal(KittyStatus.ImageNotFound, store.ComposeFrames(image!, KittyCommands.Parse("a=c,i=1,r=1,c=1"), 0));
            Assert.Equal(KittyStatus.ImageNotFound, store.Animate(image!, KittyCommands.Parse("a=a,i=1,s=3")));
        }
        finally
        {
            data.Dispose();
        }
    }

    [Fact]
    public void StoreDoesNotTakeOwnershipOfTheDataBuffer()
    {
        using var store = new KittyImageStore(10_000);
        Assert.True(store.TryAdd(ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgba8Premultiplied), 1, 0, false, 0, out var image));
        var data = ImagePixels.Allocate(1, 1, ImagePixelFormat.Rgba8Premultiplied);
        data.AsWritableSpan()[3] = 255;

        Assert.Equal(KittyStatus.Ok, store.LoadFrame(image!, KittyCommands.Parse("a=f,i=1,s=1,v=1"), data, 0));

        Assert.Equal((byte)255, data.AsSpan()[3]); // still readable and untouched: the caller owns it
        data.Dispose();
        store.ValidateInvariants();
    }
}
