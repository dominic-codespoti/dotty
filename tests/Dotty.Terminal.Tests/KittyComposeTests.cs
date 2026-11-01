using System.Collections.Generic;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary><c>a=c</c>: composing rectangles between frames, blend modes, bounds/overlap rules, quota and replies.</summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyComposeTests
{
    private static readonly byte[] Green = { 0, 255, 0, 255 };
    private static readonly byte[] Blue = { 0, 0, 255, 255 };
    private static readonly byte[] HalfWhite = { 128, 128, 128, 128 };   // straight (255,255,255,128) premultiplied
    private static readonly byte[] WhiteOverRed = { 255, 128, 128, 255 }; // HalfWhite over opaque red, see below

    /// <summary>
    /// Image 1: 2x2 opaque red root, frame 2 = [green, blue, transparent, half-white] (loaded onto a transparent
    /// canvas, so stored exactly as given: alpha 255 copies, alpha 0 leaves zero, alpha 128 over zero is the source).
    /// </summary>
    private static KittyRig Setup()
    {
        var rig = AnimKit.RedRig();
        AnimKit.LoadFrame(rig, 1, 2, 2, AnimKit.Cat(Green, Blue, AnimKit.Clear, new byte[] { 255, 255, 255, 128 }));
        rig.Replies.Clear();
        Assert.Equal(AnimKit.Cat(Green, Blue, AnimKit.Clear, HalfWhite), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));
        return rig;
    }

    // -------------------------------------------------------------------------------------------- blend modes

    [Fact]
    public void BlendComposesWholeFrameWithSourceOverArithmetic()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=1");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        // px0/px1 opaque: copied. px2 alpha 0: destination (red) stays. px3: 128 + round(255*127/255) = 255 for R and A,
        // G and B are 128 + 0.
        Assert.Equal(AnimKit.Cat(Green, Blue, AnimKit.Red, WhiteOverRed), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void C1OverwritesInsteadOfBlending()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=1,C=1");

        Assert.Equal(AnimKit.Cat(Green, Blue, AnimKit.Clear, HalfWhite), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Fact]
    public void SourceRectangleAndDestinationOffsetAreIndependent()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=1,X=1,Y=1,w=1,h=1,x=0,y=0"); // half-white pixel of frame 2 onto the top-left of frame 1

        Assert.Equal(AnimKit.Cat(WhiteOverRed, AnimKit.Red, AnimKit.Red, AnimKit.Red), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Fact]
    public void ZeroWidthAndHeightExtendToTheSourceEdge()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=1,X=1,x=1"); // w = 2 - 1, h = 2 - 0: the right column of frame 2 onto the right column

        Assert.Equal(AnimKit.Cat(AnimKit.Red, Blue, AnimKit.Red, WhiteOverRed), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Fact]
    public void ComposingOntoTheSameFrameCopiesBetweenDisjointRectangles()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=2,w=1,h=2,x=1,C=1"); // column 0 of frame 2 onto its column 1

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(AnimKit.Cat(Green, Green, AnimKit.Clear, AnimKit.Clear), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));
    }

    [Fact]
    public void BlendKeepsTheShareOfATranslucentDestinationThatTheSourceDoesNotCover()
    {
        using var rig = new KittyRig();
        AnimKit.Transmit(rig, 1, 1, 1, new byte[] { 255, 128, 64, 200 });  // stored premultiplied: (200, 100, 50, 200)
        AnimKit.LoadFrame(rig, 1, 1, 1, new byte[] { 0, 255, 255, 128 });  // frame 2, premultiplied: (0, 128, 128, 128)
        rig.Replies.Clear();

        rig.Apc("a=c,i=1,r=2,c=1");

        // out = src + round(dst * (255 - 128) / 255): R 0 + 100, G 128 + 50, B 128 + 25, A 128 + 100
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(new byte[] { 100, 178, 153, 228 }, AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Fact]
    public void CompositionNeverChangesFrameCountGapsCurrentFrameOrBytes()
    {
        using var rig = Setup();
        var image = AnimKit.Img(rig, 1);
        rig.Apc("a=a,i=1,r=2,z=33");
        rig.Replies.Clear();
        long used = rig.Store.UsedBytes;

        rig.Apc("a=c,i=1,r=2,c=1");

        Assert.Equal(2, image.Frames.Count);
        Assert.Equal(33, image.Frames[1].GapMs);
        Assert.Equal(0, image.Frames[0].GapMs); // the root frame has no gap until a=a,r=1,z= gives it one
        Assert.Equal(0, image.CurrentFrameIndex);
        Assert.Equal(used, rig.Store.UsedBytes);
    }

    [Fact]
    public void DestinationGetsANewBufferAndTheRetainedOldOneIsUntouched()
    {
        using var rig = Setup();
        var image = AnimKit.Img(rig, 1);
        var oldRoot = image.Root.Retain();
        long oldSerial = oldRoot.Serial;
        long sourceSerial = image.Frames[1].Canvas.Serial;

        rig.Apc("a=c,i=1,r=2,c=1,C=1");

        Assert.NotEqual(oldSerial, image.Root.Serial);                       // the displayed frame changed identity
        Assert.Equal(sourceSerial, image.Frames[1].Canvas.Serial);           // the source frame did not
        Assert.Equal(AnimKit.Rep(AnimKit.Red, 4), oldRoot.AsSpan().ToArray());
        oldRoot.Release();
    }

    [Fact]
    public void RgbRootAsSourceAndDestinationBecomesOpaqueRgba()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=2,f=24", B64(AnimKit.Rep(new byte[] { 1, 2, 3 }, 4)));
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue, ",c=1"); // frame 2 = RGBA copy of the root with a blue pixel
        rig.Replies.Clear();

        rig.Apc("a=c,i=1,r=1,c=2,C=1");                // RGB root as the source onto frame 2
        Assert.Equal(AnimKit.Rep(new byte[] { 1, 2, 3, 255 }, 4), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 1));

        rig.Apc("a=c,i=1,r=2,c=1,C=1,w=1,h=1");        // and as the destination
        var image = AnimKit.Img(rig, 1);
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, image.Root.Format);
        Assert.Equal(AnimKit.Rep(new byte[] { 1, 2, 3, 255 }, 4), AnimKit.CanvasOf(image, 0));
        Assert.Equal(new[] { Ok("i=1"), Ok("i=1") }, rig.Replies);
    }

    // --------------------------------------------------------------------------------------------------- errors

    [Theory]
    [InlineData("r=0,c=1")]
    [InlineData("r=1,c=0")]
    [InlineData("c=1")]
    [InlineData("r=3,c=1")]
    [InlineData("r=1,c=3")]
    [InlineData("r=4294967295,c=1")]
    public void MissingFramesAnswerEnoent(string keys)
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1," + keys);

        Assert.Equal(new[] { Err("i=1", "ENOENT:Frame not found") }, rig.Replies);
        Assert.Equal(AnimKit.Rep(AnimKit.Red, 4), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
        rig.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData("X=1,w=2")]          // source wider than the image allows
    [InlineData("Y=1,h=2")]
    [InlineData("x=1,w=2")]          // destination out of bounds
    [InlineData("y=1,h=2")]
    [InlineData("X=2")]              // zero width resolves to nothing left of the edge
    [InlineData("Y=2")]
    [InlineData("w=3")]
    [InlineData("X=4294967295,w=1")] // no wrap-around in the bounds arithmetic
    [InlineData("x=4294967295,w=1")]
    public void RectanglesOutsideTheImageAreRejectedWithoutChanges(string keys)
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=1," + keys);

        Assert.Equal(new[] { Err("i=1", "EINVAL:Rectangle out of bounds") }, rig.Replies);
        Assert.Equal(AnimKit.Rep(AnimKit.Red, 4), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Fact]
    public void RectanglesExactlyFillingTheCornerAreAccepted()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=2,c=1,X=1,Y=1,w=1,h=1,x=1,y=1,C=1");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(AnimKit.Cat(AnimKit.Red, AnimKit.Red, AnimKit.Red, HalfWhite), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Theory]
    [InlineData(0, 1, 1, false)] // [0,1) vs [1,2): adjacent
    [InlineData(1, 1, 0, false)]
    [InlineData(0, 2, 1, true)]  // [0,2) vs [1,3): one shared column
    [InlineData(1, 2, 0, true)]  // [1,3) vs [0,2)
    [InlineData(0, 1, 0, true)]  // identical
    public void SameFrameCompositionsMustNotOverlap(int srcX, int w, int dstX, bool overlap)
    {
        using var rig = new KittyRig();
        AnimKit.Transmit(rig, 1, 3, 1, AnimKit.Cat(AnimKit.Red, Green, Blue));
        rig.Replies.Clear();

        rig.Apc($"a=c,i=1,r=1,c=1,X={srcX},w={w},h=1,x={dstX},C=1");

        if (overlap)
            Assert.Equal(new[] { Err("i=1", "EINVAL:Source and destination rectangles overlap") }, rig.Replies);
        else
            Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
    }

    [Fact]
    public void OverlapDecisionMatchesACellByCellModelOverAllSmallRectangles()
    {
        using var store = new KittyImageStore(1_000_000);
        var image = store.AddImage(3, 3, 1);
        int checkedCases = 0;
        for (int sx = 0; sx <= 3; sx++)
            for (int sy = 0; sy <= 3; sy++)
                for (int dx = 0; dx <= 3; dx++)
                    for (int dy = 0; dy <= 3; dy++)
                        for (int w = 1; w <= 3; w++)
                            for (int h = 1; h <= 3; h++)
                            {
                                bool inBounds = sx + w <= 3 && sy + h <= 3 && dx + w <= 3 && dy + h <= 3;
                                bool overlap = false;
                                if (inBounds)
                                {
                                    var cells = new HashSet<(int, int)>();
                                    for (int i = 0; i < w; i++)
                                        for (int j = 0; j < h; j++)
                                            cells.Add((sx + i, sy + j));
                                    for (int i = 0; i < w && !overlap; i++)
                                        for (int j = 0; j < h && !overlap; j++)
                                            overlap = cells.Contains((dx + i, dy + j));
                                }
                                var expected = !inBounds ? KittyStatus.RectOutOfBounds : overlap ? KittyStatus.FrameOverlap : KittyStatus.Ok;

                                var cmd = KittyCommands.Parse($"a=c,i=1,r=1,c=1,X={sx},Y={sy},x={dx},y={dy},w={w},h={h},C=1");
                                Assert.Equal(expected, store.ComposeFrames(image, in cmd, 0));
                                checkedCases++;
                            }
        Assert.Equal(4 * 4 * 4 * 4 * 9, checkedCases);
        store.ValidateInvariants();
    }

    // ----------------------------------------------------------------------------------------------- addressing

    [Fact]
    public void UnknownImageBothIdsAndNoIdFollowTheFrameCommandRules()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=9,r=2,c=1");
        rig.Apc("a=c,i=1,I=3,r=2,c=1");
        rig.Apc("a=c,r=2,c=1");

        Assert.Equal(new[]
        {
            Err("i=9", "ENOENT:Image not found"),
            Err("i=1,I=3", "EINVAL:Cannot specify both image id and image number"),
        }, rig.Replies);
    }

    [Fact]
    public void ImageNumberReplyCarriesIdAndNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("I=5,s=1,v=1,f=32", B64(AnimKit.Red));
        rig.Apc("a=f,I=5,s=1,v=1,f=32", B64(Blue));
        rig.Replies.Clear();

        rig.Apc("a=c,I=5,r=2,c=1,C=1");

        Assert.Equal(new[] { Ok("i=2147483648,I=5") }, rig.Replies);
        Assert.Equal(Blue, rig.Store.FindByNumber(5)!.Root.AsSpan().ToArray());
    }

    [Fact]
    public void UnknownImageNumberAnswersEnoentWithTheNumberOnly()
    {
        using var rig = Setup();
        rig.Apc("a=c,I=9,r=2,c=1");

        Assert.Equal(new[] { Err("I=9", "ENOENT:Image not found") }, rig.Replies);
    }

    [Fact]
    public void ImageNumberErrorReplyCarriesIdAndNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("I=5,s=1,v=1,f=32", B64(AnimKit.Red));
        rig.Replies.Clear();

        rig.Apc("a=c,I=5,r=2,c=1"); // the image exists, frame 2 does not

        Assert.Equal(new[] { Err("i=2147483648,I=5", "ENOENT:Frame not found") }, rig.Replies);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void QuietLevelsApplyToComposeReplies(int quiet)
    {
        using var rig = Setup();
        rig.Apc($"a=c,i=1,r=2,c=1,q={quiet}");
        rig.Apc($"a=c,i=1,r=7,c=1,q={quiet}");

        var expected = new List<string>();
        if (quiet == 1)
            expected.Add(Err("i=1", "ENOENT:Frame not found"));
        Assert.Equal(expected, rig.Replies);
    }

    [Fact]
    public void TransientHintPropagatesToTheImage()
    {
        using var rig = Setup();
        var image = AnimKit.Img(rig, 1);
        rig.Apc("a=c,i=1,r=2,c=1");
        Assert.False(image.Transient);

        rig.Apc("a=c,i=1,r=2,c=1,N=1");

        Assert.True(image.Transient);
    }

    [Fact]
    public void FailedCompositionDoesNotMarkTheImageTransient()
    {
        using var rig = Setup();
        rig.Apc("a=c,i=1,r=9,c=1,N=1");

        Assert.False(AnimKit.Img(rig, 1).Transient);
    }

    [Fact]
    public void SuccessfulComposeNotifiesTheHostAndFailuresDoNot()
    {
        using var rig = Setup();
        int before = rig.Host.GraphicsChangedCalls;
        rig.Apc("a=c,i=1,r=7,c=1");
        rig.Apc("a=c,i=1,r=2,c=1,w=3");
        Assert.Equal(before, rig.Host.GraphicsChangedCalls);

        rig.Apc("a=c,i=1,r=2,c=1");

        Assert.Equal(before + 1, rig.Host.GraphicsChangedCalls);
    }

    // ---------------------------------------------------------------------------------------------------- quota

    [Theory]
    [InlineData(31, false)] // root 12 (RGB) + frame 16 = 28; converting the root to RGBA adds 4 -> 32
    [InlineData(32, true)]
    public void ComposingOntoAnRgbRootCountsTheGrowthAgainstTheQuota(int limit, bool fits)
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=2,f=24", B64(AnimKit.Rep(new byte[] { 1, 2, 3 }, 4)));
        AnimKit.LoadFrame(rig, 1, 1, 1, Blue);
        Assert.Equal(28, rig.Store.UsedBytes);
        rig.Store.SetStorageLimit(limit);
        rig.Replies.Clear();

        rig.Apc("a=c,i=1,r=2,c=1,C=1");

        var image = AnimKit.Img(rig, 1);
        Assert.Equal(fits ? ImagePixelFormat.Rgba8Premultiplied : ImagePixelFormat.Rgb8, image.Frames[0].Canvas.Format);
        Assert.Equal(fits ? 32 : 28, rig.Store.UsedBytes);
        Assert.Equal(new[] { fits ? Ok("i=1") : Err("i=1", "ENOSPC:Storage quota exceeded") }, rig.Replies);
        rig.Store.ValidateInvariants();
    }

    [Fact]
    public void ComposingBetweenRgbaFramesNeedsNoExtraQuota()
    {
        using var rig = Setup();
        rig.Store.SetStorageLimit(32); // exactly the two canvases
        rig.Apc("a=c,i=1,r=2,c=1");

        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        Assert.Equal(32, rig.Store.UsedBytes);
    }

    [Fact]
    public void ComposesKeepLivePixelBytesEqualToTheQuotaAccountingAndAnswerInOrder()
    {
        var baseline = new GraphicsBaseline();
        using (var rig = Setup())
        {
            rig.Apc("a=c,i=1,r=2,c=1");              // blend
            rig.Apc("a=c,i=1,r=1,c=2,C=1,w=1");      // overwrite one column of frame 2
            rig.Apc("a=c,i=1,r=2,c=1,w=3");          // out of bounds
            rig.Apc("a=c,i=1,r=2,c=2");              // same frame, identical rectangles
            rig.Apc("a=c,i=1,r=7,c=1");              // no such frame

            Assert.Equal(new[]
            {
                Ok("i=1"),
                Ok("i=1"),
                Err("i=1", "EINVAL:Rectangle out of bounds"),
                Err("i=1", "EINVAL:Source and destination rectangles overlap"),
                Err("i=1", "ENOENT:Frame not found"),
            }, rig.Replies);
            Assert.Equal(32, rig.Store.UsedBytes);
            Assert.Equal(rig.Store.UsedBytes, baseline.LiveBytesDelta);
            Assert.Equal(2, baseline.LiveCountDelta);
            Assert.Equal(0, baseline.PendingDelta);
            rig.Store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    // ------------------------------------------------------------------------------------------- cancellation

    [Fact]
    public void ACancelledComposeDoesNothingAndLeavesAnOpenFrameLoadAlone()
    {
        using var rig = Setup();
        rig.Apc("a=f,i=1,s=1,v=1,f=32,m=1", "AAAA");
        Assert.True(rig.Controller.HasOpenTransfer);

        rig.CancelledApc("a=c,i=1,r=2,c=1", "junk");

        Assert.Empty(rig.Replies);
        Assert.True(rig.Controller.HasOpenTransfer);
        Assert.Equal(AnimKit.Rep(AnimKit.Red, 4), AnimKit.CanvasOf(AnimKit.Img(rig, 1), 0));
    }

    [Fact]
    public void ACompletedComposeEndsAnOpenFrameLoad()
    {
        using var rig = Setup();
        rig.Apc("a=f,i=1,s=1,v=1,f=32,m=1", "AAAA");

        rig.Apc("a=c,i=1,r=2,c=1,C=1");

        Assert.False(rig.Controller.HasOpenTransfer);
        Assert.Equal(new[] { Ok("i=1") }, rig.Replies);
        rig.Replies.Clear();
        rig.Apc("a=f,m=0", "AAAA"); // stray continuation
        Assert.Empty(rig.Replies);
        Assert.Equal(2, AnimKit.Img(rig, 1).Frames.Count);
    }
}
