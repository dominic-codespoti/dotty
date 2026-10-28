using System;
using System.Collections.Generic;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class ImageUploadPlannerTests
{
    [Theory]
    [InlineData(100, 100, 200, 100, 100)]     // fits
    [InlineData(200, 100, 200, 200, 100)]     // longest side exactly at the limit
    [InlineData(100, 200, 200, 100, 200)]
    [InlineData(201, 100, 200, 200, 100)]     // 99.5 rounds up to 100
    [InlineData(4000, 3000, 2048, 2048, 1536)]
    [InlineData(3000, 4000, 2048, 1536, 2048)]
    [InlineData(1, 10000, 4096, 1, 4096)]     // thin: 0.41 clamps to 1
    [InlineData(10000, 3, 4096, 4096, 1)]     // 1.23 -> 1
    [InlineData(7, 5, 4, 4, 3)]               // 2.86 -> 3
    [InlineData(5, 4, 4, 4, 3)]               // 3.2 -> 3
    [InlineData(4, 1, 2, 2, 1)]               // 0.5 rounds up
    [InlineData(8, 8, 4, 4, 4)]
    [InlineData(1, 1, 1, 1, 1)]
    [InlineData(1, 1, 16384, 1, 1)]
    public void ComputeUploadSize_ClampsLongestSideAndKeepsAspect(int w, int h, int max, int ew, int eh)
    {
        Assert.Equal(new ImageUploadSize(ew, eh), ImageUploadPlanner.ComputeUploadSize(w, h, max));
    }

    [Fact]
    public void ComputeUploadSize_HugeDimensionsDoNotOverflow()
    {
        ImageUploadSize s = ImageUploadPlanner.ComputeUploadSize(int.MaxValue, int.MaxValue / 2, 8192);
        Assert.Equal(8192, s.Width);
        Assert.Equal(4096, s.Height);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(-1, 1, 1)]
    public void ComputeUploadSize_RejectsNonPositive(int w, int h, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageUploadPlanner.ComputeUploadSize(w, h, max));
    }

    [Theory]
    [InlineData(256, 256, true)]
    [InlineData(255, 256, false)]
    [InlineData(256, 255, false)]
    [InlineData(4000, 1, false)]
    public void WantsMipmaps_RequiresBothSidesAtLeast256(int w, int h, bool expected)
    {
        Assert.Equal(expected, ImageUploadPlanner.WantsMipmaps(w, h));
    }

    [Fact]
    public void EstimateTextureBytes_AddsMipChainOnlyWhenMipmapped()
    {
        Assert.Equal(2 * 2 * 4, ImageUploadPlanner.EstimateTextureBytes(2, 2, 4));
        long baseBytes = 256L * 256 * 3;
        Assert.Equal(baseBytes + baseBytes / 3, ImageUploadPlanner.EstimateTextureBytes(256, 256, 3));
    }

    [Fact]
    public void PlanUploads_StopsAtFirstRefusalEvenIfLaterOnesFit()
    {
        var c = Candidates(60, 30, 20, 5);
        var admit = new bool[4];
        int n = ImageUploadPlanner.PlanUploads(c, 100, admit);
        Assert.Equal(2, n);
        Assert.Equal(new[] { true, true, false, false }, admit);
    }

    [Fact]
    public void PlanUploads_ExactFitIsAdmitted()
    {
        var admit = new bool[2];
        Assert.Equal(2, ImageUploadPlanner.PlanUploads(Candidates(50, 50), 100, admit));
    }

    [Fact]
    public void PlanUploads_FirstImageLargerThanBudgetStillUploads()
    {
        var admit = new bool[2];
        int n = ImageUploadPlanner.PlanUploads(Candidates(150, 10), 100, admit);
        Assert.Equal(1, n);
        Assert.True(admit[0]);
        Assert.False(admit[1]);
    }

    [Fact]
    public void PlanUploads_ZeroBudgetStillMakesProgressOneImagePerFrame()
    {
        var admit = new bool[3];
        Assert.Equal(1, ImageUploadPlanner.PlanUploads(Candidates(5, 5, 5), 0, admit));
    }

    [Fact]
    public void PlanUploads_DeferredImagesDrainInOrderOverFrames()
    {
        var pending = new List<UploadCandidate>(Candidates(60, 60, 60));
        var uploadedOrder = new List<long>();
        int frames = 0;
        while (pending.Count > 0)
        {
            frames++;
            Assert.True(frames <= 3);
            var admit = new bool[pending.Count];
            ImageUploadPlanner.PlanUploads(pending.ToArray(), 100, admit);
            var deferred = new List<UploadCandidate>();
            for (int i = 0; i < pending.Count; i++)
            {
                if (admit[i]) uploadedOrder.Add(pending[i].Serial);
                else deferred.Add(pending[i]);
            }
            pending = deferred;
        }

        Assert.Equal(3, frames);
        Assert.Equal(new long[] { 0, 1, 2 }, uploadedOrder);
    }

    [Fact]
    public void PlanUploads_ShortAdmitSpanThrows()
    {
        Assert.Throws<ArgumentException>(() => ImageUploadPlanner.PlanUploads(Candidates(1, 2), 10, new bool[1]));
    }

    [Fact]
    public void UploadBudgetTracker_ReportsDeferredAndUsed()
    {
        var t = new UploadBudgetTracker(100);
        Assert.False(t.Deferred);
        Assert.True(t.TryAdmit(100));
        Assert.Equal(100, t.Used);
        Assert.False(t.TryAdmit(1));
        Assert.True(t.Deferred);
        Assert.Equal(100, t.Used);
        Assert.False(t.TryAdmit(0));   // still refused after the first refusal
    }

    [Fact]
    public void PlanEviction_IdleBeyondLimitEvictsButExactLimitStays()
    {
        var r = Resident((1, 10, 699), (2, 10, 700), (3, 10, 1000));
        long[] ev = Evict(r, 1000, 300, long.MaxValue);
        Assert.Equal(new long[] { 1 }, ev);
    }

    [Fact]
    public void PlanEviction_OverBudgetEvictsOldestFirstUntilUnderBudget()
    {
        var r = Resident((1, 60, 5), (2, 60, 7), (3, 60, 10));
        Assert.Equal(new long[] { 1, 2 }, Evict(r, 10, 300, 100));
    }

    [Fact]
    public void PlanEviction_StopsWhenTotalEqualsBudget()
    {
        var r = Resident((1, 50, 1), (2, 50, 2), (3, 50, 3));
        Assert.Equal(new long[] { 1 }, Evict(r, 10, 300, 100));
    }

    [Fact]
    public void PlanEviction_NeverEvictsTexturesUsedThisFrame()
    {
        var r = Resident((1, 200, 10));
        Assert.Empty(Evict(r, 10, 300, 100));
    }

    [Fact]
    public void PlanEviction_TiesPreferLargerThenLowerSerial()
    {
        var r = Resident((1, 10, 5), (3, 50, 5), (2, 50, 5));
        Assert.Equal(new long[] { 2 }, Evict(r, 10, 300, 60));
        r = Resident((1, 10, 5), (3, 50, 5), (2, 50, 5));
        Assert.Equal(new long[] { 2, 3 }, Evict(r, 10, 300, 10));
    }

    [Fact]
    public void PlanEviction_IdleAndBudgetCombineInAgeOrder()
    {
        // current 1000, idle limit 300: serial 9 (last 100) and 8 (last 500) are idle; 7 (last 900) is
        // not idle but 7+6 exceed the budget, so 7 goes after them; 6 (current) stays.
        var r = Resident((6, 40, 1000), (7, 40, 900), (8, 40, 500), (9, 40, 100));
        Assert.Equal(new long[] { 9, 8, 7 }, Evict(r, 1000, 300, 50));
    }

    [Fact]
    public void PlanEviction_EmptyAndShortOutput()
    {
        Assert.Equal(0, ImageUploadPlanner.PlanEviction(Span<ResidentTexture>.Empty, 5, 300, 0, Span<long>.Empty));
        var r = Resident((1, 1, 1), (2, 1, 1));
        Assert.Throws<ArgumentException>(() => ImageUploadPlanner.PlanEviction(r, 9, 1, 0, new long[1]));
    }

    [Fact]
    public void BoxDownscale_AveragesExactBlocks()
    {
        byte[] src = { 0, 10, 20, 30, 40, 50, 60, 70 };
        byte[] dst = new byte[2];
        ImageUploadPlanner.BoxDownscale(src, 4, 2, 4, 1, dst, 2, 1);
        Assert.Equal(new byte[] { 25, 45 }, dst);
    }

    [Fact]
    public void BoxDownscale_OddRatioOverlapsNeighbours()
    {
        byte[] src = { 0, 100, 200 };
        byte[] dst = new byte[2];
        ImageUploadPlanner.BoxDownscale(src, 3, 1, 3, 1, dst, 2, 1);
        Assert.Equal(new byte[] { 50, 150 }, dst);
    }

    [Fact]
    public void BoxDownscale_HonoursStridePaddingAndChannels()
    {
        // 2x2 RGB, stride 8 (2 padding bytes of 0xFF that must not be sampled), averaged into 1x1.
        byte[] src =
        {
            10, 20, 30,  20, 40, 60,  0xFF, 0xFF,
            30, 60, 90,  41, 81, 121, 0xFF, 0xFF,
        };
        byte[] dst = new byte[3];
        ImageUploadPlanner.BoxDownscale(src, 2, 2, 8, 3, dst, 1, 1);
        // sums: 101, 201, 301 over 4 pixels -> 25.25, 50.25, 75.25 -> 25, 50, 75
        Assert.Equal(new byte[] { 25, 50, 75 }, dst);
    }

    [Fact]
    public void BoxDownscale_RoundsHalfUp()
    {
        byte[] dst = new byte[1];
        ImageUploadPlanner.BoxDownscale(new byte[] { 1, 2 }, 2, 1, 2, 1, dst, 1, 1);
        Assert.Equal(2, dst[0]);
    }

    [Fact]
    public void BoxDownscale_PreservesConstantImagesAtAwkwardRatios()
    {
        byte[] src = new byte[7 * 5 * 4];
        Array.Fill(src, (byte)200);
        byte[] dst = new byte[3 * 2 * 4];
        ImageUploadPlanner.BoxDownscale(src, 7, 5, 28, 4, dst, 3, 2);
        Assert.All(dst, b => Assert.Equal(200, b));
    }

    [Fact]
    public void BoxDownscale_PremultipliedAlphaNeverExceedsColourInvariant()
    {
        // premultiplied pixels (r <= a); averaging keeps r <= a per channel
        byte[] src = { 10, 0, 0, 10,   0, 0, 0, 0,   50, 50, 50, 100,   20, 5, 5, 30 };
        byte[] dst = new byte[4];
        ImageUploadPlanner.BoxDownscale(src, 2, 2, 8, 4, dst, 1, 1);
        Assert.True(dst[0] <= dst[3] && dst[1] <= dst[3] && dst[2] <= dst[3]);
    }

    [Fact]
    public void BoxDownscale_Identity_CopiesAndRejectsUpscale()
    {
        byte[] src = { 1, 2, 3, 4 };
        byte[] dst = new byte[4];
        ImageUploadPlanner.BoxDownscale(src, 2, 2, 2, 1, dst, 2, 2);
        Assert.Equal(src, dst);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageUploadPlanner.BoxDownscale(src, 2, 2, 2, 1, new byte[9], 3, 3));
    }

    [Fact]
    public void BoxDownscale_ShortBuffersThrow()
    {
        Assert.Throws<ArgumentException>(() =>
            ImageUploadPlanner.BoxDownscale(new byte[3], 2, 2, 2, 1, new byte[1], 1, 1));
        Assert.Throws<ArgumentException>(() =>
            ImageUploadPlanner.BoxDownscale(new byte[4], 2, 2, 2, 1, Span<byte>.Empty, 1, 1));
    }

    private static UploadCandidate[] Candidates(params long[] bytes)
    {
        var r = new UploadCandidate[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            r[i] = new UploadCandidate(i, bytes[i]);
        return r;
    }

    private static ResidentTexture[] Resident(params (long serial, long bytes, long last)[] items)
    {
        var r = new ResidentTexture[items.Length];
        for (int i = 0; i < items.Length; i++)
            r[i] = new ResidentTexture(items[i].serial, items[i].bytes, items[i].last);
        return r;
    }

    private static long[] Evict(ResidentTexture[] resident, long frame, int idle, long budget)
    {
        var buf = new long[resident.Length];
        int n = ImageUploadPlanner.PlanEviction(resident, frame, idle, budget, buf);
        return buf.AsSpan(0, n).ToArray();
    }
}
