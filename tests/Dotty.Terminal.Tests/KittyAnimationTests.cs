using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Animation control (<c>a=a</c>), the pull-model clock (<c>AdvanceAnimations</c>), wake-up deadlines, visibility,
/// frame deletion (<c>d=f/F</c>) and seeded random command sequences. Clock tests use 1000 ticks per second so a tick
/// is a millisecond; every frame is a 1x1 pixel whose red channel is <c>10 * (index + 1)</c>.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyAnimationTests
{
    private static byte[] Col(int index) => new byte[] { (byte)(10 * (index + 1)), 0, 0, 255 };

    /// <summary>
    /// Adds image <paramref name="id"/> (1x1, one frame per entry of <paramref name="gaps"/>) and places it once.
    /// A gap of 0 omits <c>z</c>: a frame then has the protocol's default (40 ms) and the root frame none (gapless);
    /// a negative gap is gapless; entry 0 is the root frame's gap, set with <c>a=a,r=1,z=</c> unless it is 0.
    /// </summary>
    private static KittyImage AddAnim(KittyRig rig, uint id, long anchorRow, params int[] gaps)
    {
        AnimKit.Transmit(rig, id, 1, 1, Col(0));
        for (int i = 1; i < gaps.Length; i++)
            AnimKit.LoadFrame(rig, id, 1, 1, Col(i), gaps[i] == 0 ? "" : $",z={gaps[i]}");
        if (gaps[0] != 0)
            rig.Apc($"a=a,i={id},r=1,z={gaps[0]}");
        var image = AnimKit.Img(rig, id);
        rig.Store.Place(image, "p=1", anchorRow);
        return image;
    }

    private sealed class Anim : IDisposable
    {
        public KittyRig Rig { get; } = new();
        public KittyImage Image { get; }
        public KittyImageStore Store => Rig.Store;

        public Anim(long anchorRow, int[] gaps)
        {
            Rig.Store.TicksPerSecond = 1000;
            Image = AddAnim(Rig, 1, anchorRow, gaps);
            Rig.Replies.Clear();
        }

        /// <summary>One clock pass at <paramref name="now"/> over a 24-row viewport; the returned deadline.</summary>
        public long At(long now) => Store.AdvanceAnimations(now, 24, 0);

        public void Cmd(string control) => Rig.Apc(control);

        public int Current => Image.CurrentFrameIndex;

        public void Dispose() => Rig.Dispose();
    }

    private static Anim NewAnim(params int[] gaps) => new(0, gaps);

    // ------------------------------------------------------------------------------------------ key helpers

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(0, 40)]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void GapFromKeyFollowsTheProtocolRules(int z, int gap) => Assert.Equal(gap, KittyAnimation.GapFromKey(z));

    [Theory]
    [InlineData(40, 1000, 40)]
    [InlineData(40, 10_000_000, 400_000)]
    [InlineData(40, 1_000_000_000, 40_000_000)]
    [InlineData(1500, 1000, 1500)]
    [InlineData(1, 100, 1)]       // 0.1 tick rounds up to one: a deadline is always in the future
    [InlineData(1, 1, 1)]
    [InlineData(3, 1500, 4)]      // 4.5 ticks round down
    [InlineData(25, 100, 2)]      // 2.5 ticks round down
    public void MillisecondsConvertToTicks(int ms, int frequency, int ticks) =>
        Assert.Equal((long)ticks, KittyAnimation.MsToTicks(ms, frequency));

    [Fact]
    public void TheLargestGapAtNanosecondResolutionDoesNotOverflow() =>
        Assert.Equal(2_147_483_647_000_000L, KittyAnimation.MsToTicks(int.MaxValue, 1_000_000_000));

    [Fact]
    public void ClockFrequencyMustBePositive()
    {
        using var store = new KittyImageStore(1000);
        Assert.Equal(TimeProvider.System.TimestampFrequency, store.TicksPerSecond);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.TicksPerSecond = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.TicksPerSecond = -5);
    }

    // ----------------------------------------------------------------------------------------------- stop/run

    [Fact]
    public void AnImageStartsStoppedAndNeverAdvances()
    {
        using var a = NewAnim(0, 10, 10);
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);

        Assert.Equal(0, a.At(1000));
        Assert.Equal(0, a.At(900_000));
        Assert.Equal(0, a.Current);
    }

    [Fact]
    public void RunningAnimationStepsOnItsDeadlinesAndWraps()
    {
        using var a = NewAnim(40, 100, 20); // gaps 40, 100, 20
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(1040, a.At(1000));  // first pass arms the root frame's gap
        Assert.Equal(0, a.Current);
        Assert.Equal(1040, a.At(1039));
        Assert.Equal(0, a.Current);
        Assert.Equal(1140, a.At(1040));
        Assert.Equal(1, a.Current);
        Assert.Equal(1140, a.At(1139));
        Assert.Equal(1, a.Current);
        Assert.Equal(1160, a.At(1140));
        Assert.Equal(2, a.Current);
        Assert.Equal(1200, a.At(1160));  // wrap back to the root: one loop done, infinite limit
        Assert.Equal(0, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
        Assert.Equal(1300, a.At(1200));
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void ALateClockPassStepsOnceAndMeasuresFromNow()
    {
        using var a = NewAnim(40, 100, 20);
        a.Cmd("a=a,i=1,s=3");
        a.At(1000);
        a.At(1040);              // frame 1, deadline 1140

        Assert.Equal(5020, a.At(5000)); // one step (to frame 2), no catch-up burst; next deadline = now + gap(2)
        Assert.Equal(2, a.Current);
        Assert.Equal(5020, a.At(5000)); // the same instant does not step again
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void FramesAndDeadlinesFollowAReferenceModelOverManyWraps()
    {
        int[] gaps = { 10, 20, 30, 40, 50 };
        using var a = NewAnim(gaps);
        a.Cmd("a=a,i=1,s=3");
        long t = 1000;
        Assert.Equal(t + gaps[0], a.At(t));

        int cur = 0;
        long deadline = t + gaps[0];
        for (int step = 0; step < gaps.Length * 4 + 2; step++)
        {
            Assert.Equal(deadline, a.At(deadline - 1));
            Assert.Equal(cur, a.Current);
            cur = (cur + 1) % gaps.Length;
            long next = deadline + gaps[cur];
            Assert.Equal(next, a.At(deadline));
            Assert.Equal(cur, a.Current);
            deadline = next;
        }
        Assert.Equal(4u, a.Image.LoopsDone);
        Assert.Same(a.Image.Frames[a.Current].Canvas, a.Image.Root);
        a.Store.ValidateInvariants();
    }

    [Fact]
    public void StopFreezesTheFrameAndRunRestartsTheTimerFromTheNextPass()
    {
        using var a = NewAnim(10, 10, 10);
        a.Cmd("a=a,i=1,s=3");
        a.At(100);
        Assert.Equal(120, a.At(110));
        Assert.Equal(1, a.Current);

        a.Cmd("a=a,i=1,s=1");
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
        Assert.Equal(0, a.At(200));
        Assert.Equal(0, a.At(100_000));
        Assert.Equal(1, a.Current);

        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(300_010, a.At(300_000)); // armed from now, not from the stale deadline
        Assert.Equal(1, a.Current);
        Assert.Equal(300_020, a.At(300_010));
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void ASingleFrameAnimationHasNoDeadline()
    {
        using var a = NewAnim(40);
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(0, a.At(100));
        Assert.Equal(0, a.At(100_000));
        Assert.Equal(0, a.Current);
    }

    // ----------------------------------------------------------------------------------------------- loops

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void LoopCountStopsAfterVMinusOnePasses(int v)
    {
        using var a = NewAnim(10, 10);
        a.Cmd($"a=a,i=1,s=3,v={v}");

        long stoppedAt = -1;
        for (long now = 100; now <= 100 + 20 * 20; now += 10)
        {
            long deadline = a.At(now);
            if (a.Image.AnimationState == KittyAnimationState.Stopped)
            {
                stoppedAt = now;
                Assert.Equal(0, deadline);
                break;
            }
        }

        Assert.Equal(100 + 20 * (v - 1), stoppedAt);
        Assert.Equal((uint)(v - 1), a.Image.LoopsDone);
        Assert.Equal(1, a.Current); // stays on the last frame
        Assert.Equal(0, a.At(100_000));
    }

    [Fact]
    public void VOneLoopsForever()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3,v=1");
        Assert.Equal(0u, a.Image.MaxLoops);

        long last = 0;
        for (long now = 100; now <= 1000; now += 10)
            last = a.At(now);

        Assert.Equal(KittyAnimationState.Running, a.Image.AnimationState);
        Assert.Equal(45u, a.Image.LoopsDone); // wraps at 120, 140, ... 1000
        Assert.Equal(1010, last);
    }

    [Fact]
    public void VZeroIsIgnoredAndSettingVRestartsTheLoopCount()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3,v=3");
        Assert.Equal(2u, a.Image.MaxLoops);
        for (long now = 100; now <= 120; now += 10)
            a.At(now);
        Assert.Equal(1u, a.Image.LoopsDone);

        a.Cmd("a=a,i=1,v=0");
        Assert.Equal(2u, a.Image.MaxLoops);
        Assert.Equal(1u, a.Image.LoopsDone);

        a.Cmd("a=a,i=1,v=5");
        Assert.Equal(4u, a.Image.MaxLoops);
        Assert.Equal(0u, a.Image.LoopsDone);
    }

    [Fact]
    public void RunningAgainAfterTheLoopLimitResetsTheCounter()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3,v=2"); // one pass
        for (long now = 100; now <= 120; now += 10)
            a.At(now);
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
        Assert.Equal(1u, a.Image.LoopsDone);

        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(0u, a.Image.LoopsDone);
        Assert.Equal(1010, a.At(1000));          // sitting on the last frame
        Assert.Equal(0, a.At(1010));             // its pass ends at once: stopped again
        Assert.Equal(1u, a.Image.LoopsDone);
    }

    [Fact]
    public void StoppingResetsTheLoopCounter()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3,v=3");             // two passes
        for (long now = 100; now <= 120; now += 10)
            a.At(now);
        Assert.Equal(1u, a.Image.LoopsDone);

        a.Cmd("a=a,i=1,s=1");
        Assert.Equal(0u, a.Image.LoopsDone);
        Assert.Equal(2u, a.Image.MaxLoops);    // the limit itself stays

        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(0u, a.Image.LoopsDone);
        Assert.Equal(1010, a.At(1000));
        Assert.Equal(1020, a.At(1010));
        Assert.Equal(1030, a.At(1020));        // first wrap: one pass done, two allowed
        Assert.Equal(1u, a.Image.LoopsDone);
        Assert.Equal(KittyAnimationState.Running, a.Image.AnimationState);
        Assert.Equal(1040, a.At(1030));
        Assert.Equal(0, a.At(1040));           // second wrap: the limit
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
    }

    [Fact]
    public void SwitchingBetweenLoadingAndRunningKeepsTheLoopCounter()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3,v=4");             // three passes
        for (long now = 100; now <= 140; now += 10)
            a.At(now);
        Assert.Equal(2u, a.Image.LoopsDone);   // wraps at 120 and 140

        a.Cmd("a=a,i=1,s=2");
        Assert.Equal(2u, a.Image.LoopsDone);
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(2u, a.Image.LoopsDone);
        Assert.Equal(KittyAnimationState.Running, a.Image.AnimationState);

        Assert.Equal(1010, a.At(1000));
        Assert.Equal(1020, a.At(1010));
        Assert.Equal(0, a.At(1020));           // the third wrap reaches the limit
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
        Assert.Equal(3u, a.Image.LoopsDone);
    }

    // ---------------------------------------------------------------------------------------------- gapless

    [Fact]
    public void TheRootFrameIsGaplessUntilItsGapIsSetAndOtherFramesDefaultTo40()
    {
        using var a = NewAnim(0, 0, -1);

        Assert.Equal(0, a.Image.Frames[0].GapMs);                          // the protocol's default for the root frame
        Assert.Equal(KittyAnimation.DefaultGapMs, a.Image.Frames[1].GapMs);
        Assert.Equal(0, a.Image.Frames[2].GapMs);                          // z=-1
    }

    [Fact]
    public void GaplessFramesInTheMiddleAreSkipped()
    {
        using var a = NewAnim(40, -1, 0); // 40, gapless, 40
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(0, a.Image.Frames[1].GapMs);

        Assert.Equal(140, a.At(100));
        Assert.Equal(180, a.At(140));
        Assert.Equal(2, a.Current);       // 0 -> 2
        Assert.Equal(220, a.At(180));
        Assert.Equal(0, a.Current);       // wrap
        Assert.Equal(260, a.At(220));
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void ATrailingGaplessFrameWrapsRightAfterTheLastDisplayableOne()
    {
        using var a = NewAnim(40, 0, -1);
        a.Cmd("a=a,i=1,s=3");

        a.At(100);
        a.At(140);
        Assert.Equal(1, a.Current);
        Assert.Equal(220, a.At(180));     // frame 2 is skipped, wrap to 0
        Assert.Equal(0, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
    }

    [Fact]
    public void AGaplessRootIsSkippedFromTheFirstPassOnAndNeverShownAgain()
    {
        using var a = NewAnim(0, 20, 30);   // the root keeps its default: no gap
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(120, a.At(100));       // stepped past the root at once; frame 2 armed for its own gap
        Assert.Equal(1, a.Current);
        Assert.Equal(120, a.At(119));
        Assert.Equal(150, a.At(120));
        Assert.Equal(2, a.Current);
        Assert.Equal(170, a.At(150));       // wrap: the root is skipped again
        Assert.Equal(1, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
        Assert.Equal(200, a.At(170));
        Assert.Equal(2, a.Current);
        Assert.Equal(220, a.At(200));
        Assert.Equal(1, a.Current);
        Assert.Equal(2u, a.Image.LoopsDone);
    }

    [Fact]
    public void GivingTheRootAGapMakesItPartOfTheLoop()
    {
        using var a = NewAnim(0, 20, 30);
        a.Cmd("a=a,i=1,r=1,z=15");
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(115, a.At(100));       // the root is held for its own gap
        Assert.Equal(0, a.Current);
        Assert.Equal(135, a.At(115));
        Assert.Equal(1, a.Current);
        Assert.Equal(165, a.At(135));
        Assert.Equal(2, a.Current);
        Assert.Equal(180, a.At(165));       // the wrap lands on the root again
        Assert.Equal(0, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
    }

    [Fact]
    public void MakingTheRootGaplessAgainSkipsItAtTheNextWrap()
    {
        using var a = NewAnim(15, 20, 30);
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(115, a.At(100));
        Assert.Equal(135, a.At(115));       // frame 2

        a.Cmd("a=a,i=1,r=1,z=-1");
        Assert.Equal(0, a.Image.Frames[0].GapMs);

        Assert.Equal(165, a.At(135));       // frame 3: the armed deadline was kept
        Assert.Equal(2, a.Current);
        Assert.Equal(185, a.At(165));       // the wrap skips the root now
        Assert.Equal(1, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
    }

    [Fact]
    public void SelectingAGaplessFrameWhileRunningSkipsItOnTheNextPass()
    {
        using var a = NewAnim(40, -1, 30);
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(140, a.At(100));

        a.Cmd("a=a,i=1,c=2");
        Assert.Equal(1, a.Current);         // shown until the clock runs again

        Assert.Equal(230, a.At(200));       // stepped past at once: frame 3 for its own gap
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void SelectingAGaplessFrameWhileStoppedShowsItWithoutAdvancing()
    {
        using var a = NewAnim(40, -1, 30);

        a.Cmd("a=a,i=1,c=2");

        Assert.Equal(1, a.Current);
        Assert.Same(a.Image.Frames[1].Canvas, a.Image.Root);
        Assert.Equal(0, a.At(100));
        Assert.Equal(0, a.At(100_000));
        Assert.Equal(1, a.Current);

        a.Cmd("a=a,i=1,s=3");               // playing it moves on at once
        Assert.Equal(200_030, a.At(200_000));
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void AllOtherFramesGaplessKeepsTheOnlyDisplayableFrame()
    {
        using var a = NewAnim(40, -1, -1);
        a.Cmd("a=a,i=1,s=3,v=3");
        a.At(100);

        Assert.Equal(180, a.At(140));
        Assert.Equal(0, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
        Assert.Equal(0, a.At(180));       // second pass reaches the limit
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
        Assert.Equal(0, a.Current);
    }

    [Fact]
    public void EveryFrameGaplessNeverSpins()
    {
        using var a = NewAnim(-1, -1, -1);
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(0, a.At(100));        // nothing displayable: waits, no deadline
        Assert.True(a.Image.AnimationWaiting);
        Assert.Equal(0, a.At(100_000));
        Assert.Equal(0, a.Current);

        a.Cmd("a=a,i=1,r=2,z=25");         // a frame becomes displayable: playback resumes
        Assert.False(a.Image.AnimationWaiting);
        Assert.Equal(200_025, a.At(200_000));  // the gapless frame on screen is stepped past at once
        Assert.Equal(1, a.Current);
        Assert.Equal(200_050, a.At(200_025));  // frame 2 is the only displayable one: it stays, a loop is counted
        Assert.Equal(1, a.Current);
    }

    // ----------------------------------------------------------------------------------------- loading state

    [Fact]
    public void LoadingAnimationWaitsOnTheLastFrameAndResumesWhenFramesArrive()
    {
        using var a = NewAnim(40, 10);
        a.Cmd("a=a,i=1,s=2");
        Assert.Equal(KittyAnimationState.Loading, a.Image.AnimationState);

        Assert.Equal(140, a.At(100));
        Assert.Equal(150, a.At(140));
        Assert.Equal(1, a.Current);
        Assert.Equal(0, a.At(150));        // end reached while loading: wait, no deadline
        Assert.Equal(0, a.At(1000));
        Assert.Equal(0u, a.Image.LoopsDone);
        Assert.Equal(KittyAnimationState.Loading, a.Image.AnimationState);

        AnimKit.LoadFrame(a.Rig, 1, 1, 1, Col(2), ",z=30");   // a new frame ends the wait
        Assert.Equal(2010, a.At(2000));    // armed from now with the gap of the frame on screen (10)
        Assert.Equal(2040, a.At(2010));
        Assert.Equal(2, a.Current);
        Assert.Equal(0, a.At(2040));       // waits again at the new end
        Assert.Equal(0u, a.Image.LoopsDone);

        a.Cmd("a=a,i=1,s=3");              // the final run: wraps and loops
        Assert.Equal(3030, a.At(3000));
        Assert.Equal(3070, a.At(3030));
        Assert.Equal(0, a.Current);
        Assert.Equal(1u, a.Image.LoopsDone);
    }

    [Fact]
    public void StepsBeforeTheEndAreTakenNormallyWhileLoading()
    {
        using var a = NewAnim(10, 10, 10);
        a.Cmd("a=a,i=1,s=2");
        a.At(100);
        a.At(110);
        a.At(120);

        Assert.Equal(2, a.Current);
        Assert.Equal(0, a.At(130));
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void SelectingTheCurrentFrameEndsALoadingWait()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=2");
        a.At(100);
        a.At(110);
        Assert.Equal(0, a.At(120));          // waiting on the last frame
        Assert.Equal(1, a.Current);

        a.Cmd("a=a,i=1,c=1");                // still loading, but a frame that can advance is selected

        Assert.Equal(KittyAnimationState.Loading, a.Image.AnimationState);
        Assert.Equal(1010, a.At(1000));
        Assert.Equal(0, a.Current);
        Assert.Equal(1020, a.At(1010));
        Assert.Equal(1, a.Current);
        Assert.Equal(0, a.At(1020));         // the end again: wait
    }

    // ------------------------------------------------------------------------------------------- a=a control

    [Fact]
    public void CurrentFrameSelectionChangesTheDisplayedPixelsAndRestartsTheTimer()
    {
        using var a = NewAnim(10, 10, 10);
        long before = a.Image.Root.Serial;

        a.Cmd("a=a,i=1,c=3");

        Assert.Equal(2, a.Current);
        Assert.Same(a.Image.Frames[2].Canvas, a.Image.Root);
        Assert.NotEqual(before, a.Image.Root.Serial);
        Assert.Equal(new[] { Ok("i=1") }, a.Rig.Replies);

        a.Cmd("a=a,i=1,s=3");
        a.At(100);                  // armed: 110
        a.Cmd("a=a,i=1,c=1");       // moving the current frame restarts the timer
        Assert.Equal(115, a.At(105));
        Assert.Equal(0, a.Current);
    }

    [Fact]
    public void RunningAndSelectingTheCurrentFrameInOneCommandStartsFromThatFrame()
    {
        using var a = NewAnim(10, 20, 30);

        a.Cmd("a=a,i=1,s=3,c=2");

        Assert.Equal(1, a.Current);
        Assert.Equal(120, a.At(100));        // the gap of frame 2
        Assert.Equal(150, a.At(120));
        Assert.Equal(2, a.Current);
    }

    [Fact]
    public void UnknownCurrentFrameIsRejectedWithoutChanges()
    {
        using var a = NewAnim(10, 10);
        a.Rig.Replies.Clear();
        a.Cmd("a=a,i=1,c=9");
        a.Cmd("a=a,i=1,s=3,c=9");

        Assert.Equal(new[] { Err("i=1", "ENOENT:Frame not found"), Err("i=1", "ENOENT:Frame not found") }, a.Rig.Replies);
        Assert.Equal(0, a.Current);
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState); // atomic: s was not applied
    }

    [Fact]
    public void InvalidStateValueIsRejectedAtomically()
    {
        using var a = NewAnim(10, 10);
        a.Rig.Replies.Clear();
        a.Cmd("a=a,i=1,s=9,c=2,v=4");

        Assert.Equal(new[] { Err("i=1", "EINVAL:Invalid key value") }, a.Rig.Replies);
        Assert.Equal(0, a.Current);
        Assert.Equal(0u, a.Image.MaxLoops);
    }

    [Fact]
    public void FrameGapIsSetByRAndZ()
    {
        using var a = NewAnim(0, 0, 0);

        a.Cmd("a=a,i=1,r=2,z=500");
        Assert.Equal(500, a.Image.Frames[1].GapMs);
        a.Cmd("a=a,i=1,r=2,z=0");     // 0 is ignored
        Assert.Equal(500, a.Image.Frames[1].GapMs);
        a.Cmd("a=a,i=1,r=2");         // r alone is ignored
        Assert.Equal(500, a.Image.Frames[1].GapMs);
        a.Cmd("a=a,i=1,r=2,z=-7");    // gapless
        Assert.Equal(0, a.Image.Frames[1].GapMs);
        a.Cmd("a=a,i=1,r=1,z=12");    // the root frame too
        Assert.Equal(12, a.Image.Frames[0].GapMs);
        Assert.Equal(40, a.Image.Frames[2].GapMs);
        Assert.Equal(Enumerable.Repeat(Ok("i=1"), 5), a.Rig.Replies);
    }

    [Fact]
    public void GapOfAnUnknownFrameIsAnErrorButAnUnusedRIsNot()
    {
        using var a = NewAnim(0, 0);
        a.Rig.Replies.Clear();
        a.Cmd("a=a,i=1,r=9,z=5");
        a.Cmd("a=a,i=1,r=9");

        Assert.Equal(new[] { Err("i=1", "ENOENT:Frame not found"), Ok("i=1") }, a.Rig.Replies);
    }

    [Fact]
    public void AGapWithoutAFrameNumberChangesNothing()
    {
        using var a = NewAnim(0, 0);

        a.Cmd("a=a,i=1,z=500");

        Assert.Equal(0, a.Image.Frames[0].GapMs);   // the root has none by default
        Assert.Equal(40, a.Image.Frames[1].GapMs);
        Assert.Equal(new[] { Ok("i=1") }, a.Rig.Replies);
    }

    [Fact]
    public void ANewGapAppliesToTheNextStep()
    {
        using var a = NewAnim(40, 10);
        a.Cmd("a=a,i=1,s=3,r=2,z=500");
        a.At(100);
        Assert.Equal(140 + 500, a.At(140));   // frame 2 now stays 500 ms
    }

    [Fact]
    public void ChangingTheGapOfTheDisplayedFrameKeepsTheArmedDeadline()
    {
        using var a = NewAnim(40, 10); // gaps 40, 10
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(140, a.At(100));

        a.Cmd("a=a,i=1,r=1,z=500");          // the frame on screen keeps its armed deadline

        Assert.Equal(140, a.At(139));
        Assert.Equal(150, a.At(140));
        Assert.Equal(1, a.Current);
        Assert.Equal(650, a.At(150));        // wrapped to the root: its new gap is used from now on
        Assert.Equal(0, a.Current);
    }

    [Fact]
    public void ControlCommandsAddressImagesByNumberAndAnswerUnknownOnesWithEnoent()
    {
        using var rig = new KittyRig();
        rig.Apc("I=3,s=1,v=1,f=32", B64(Col(0)));
        rig.Replies.Clear();

        rig.Apc("a=a,I=3,s=3");
        rig.Apc("a=a,I=4,s=3");
        rig.Apc("a=a,i=77,s=3");
        rig.Apc("a=a,s=3");                 // nothing addressable: silent
        rig.Apc("a=a,i=1,I=3,s=3");

        Assert.Equal(new[]
        {
            Ok("i=2147483648,I=3"),
            Err("I=4", "ENOENT:Image not found"),
            Err("i=77", "ENOENT:Image not found"),
            Err("i=1,I=3", "EINVAL:Cannot specify both image id and image number"),
        }, rig.Replies);
        Assert.Equal(KittyAnimationState.Running, rig.Store.FindByNumber(3)!.AnimationState);
    }

    [Fact]
    public void ACancelledControlCommandDoesNothing()
    {
        using var a = NewAnim(10, 10);
        a.Rig.CancelledApc("a=a,i=1,s=3", "junk");

        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
        Assert.Empty(a.Rig.Replies);
    }

    [Fact]
    public void ACancelledControlCommandLeavesAnOpenFrameLoadAlone()
    {
        using var a = NewAnim(10, 10);
        a.Rig.Apc("a=f,i=1,s=1,v=1,f=32,m=1", "AAAA");
        Assert.True(a.Rig.Controller.HasOpenTransfer);

        a.Rig.CancelledApc("a=a,i=1,s=3", "junk");

        Assert.True(a.Rig.Controller.HasOpenTransfer);
        Assert.Equal(KittyAnimationState.Stopped, a.Image.AnimationState);
        Assert.Empty(a.Rig.Replies);
    }

    [Fact]
    public void ACompletedControlCommandEndsAnOpenFrameLoad()
    {
        using var a = NewAnim(10, 10);
        a.Rig.Apc("a=f,i=1,s=1,v=1,f=32,m=1", "AAAA");

        a.Cmd("a=a,i=1,s=3");

        Assert.False(a.Rig.Controller.HasOpenTransfer);
        Assert.Equal(new[] { Ok("i=1") }, a.Rig.Replies);
        a.Rig.Replies.Clear();
        a.Rig.Apc("a=f,m=0", "AAAA");        // the rest of the abandoned load: silent
        Assert.Empty(a.Rig.Replies);
        Assert.Equal(2, a.Image.Frames.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void QuietLevelsApplyToControlReplies(int quiet)
    {
        using var a = NewAnim(10, 10);

        a.Cmd($"a=a,i=1,s=3,q={quiet}");
        a.Cmd($"a=a,i=1,c=9,q={quiet}");

        var expected = new List<string>();
        if (quiet == 1)
            expected.Add(Err("i=1", "ENOENT:Frame not found"));
        Assert.Equal(expected, a.Rig.Replies);
        Assert.Equal(KittyAnimationState.Running, a.Image.AnimationState);
    }

    [Fact]
    public void ControlCommandsNotifyTheHostOnlyWhenTheySucceed()
    {
        using var a = NewAnim(10, 10);
        int changes = a.Rig.Host.GraphicsChangedCalls;

        a.Cmd("a=a,i=1,c=9");                // unknown frame
        a.Cmd("a=a,i=77,s=3");               // unknown image
        a.Cmd("a=a,i=1,s=9");                // invalid state
        Assert.Equal(changes, a.Rig.Host.GraphicsChangedCalls);

        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(changes + 1, a.Rig.Host.GraphicsChangedCalls);
    }

    // ------------------------------------------------------------------------------ edits during playback

    [Fact]
    public void EditingFramesDoesNotRestartTheTimerSoStreamingCannotStarvePlayback()
    {
        using var a = NewAnim(40, 40);
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(1040, a.At(1000));

        AnimKit.LoadFrame(a.Rig, 1, 1, 1, Col(5), ",r=2");
        a.Cmd("a=c,i=1,r=1,c=2,C=1");

        Assert.Equal(1040, a.At(1039));
        Assert.Equal(0, a.Current);
        Assert.Equal(1080, a.At(1040));
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void EditingTheDisplayedFrameShowsANewBufferWhileTheOldOneStaysValid()
    {
        using var a = NewAnim(10, 10);
        var old = a.Image.Root.Retain();
        long serial = old.Serial;

        AnimKit.LoadFrame(a.Rig, 1, 1, 1, Col(7), ",r=1,X=1");

        Assert.NotEqual(serial, a.Image.Root.Serial);
        Assert.Equal(Col(7), a.Image.Root.AsSpan().ToArray());
        Assert.Equal(Col(0), old.AsSpan().ToArray());
        old.Release();
    }

    // -------------------------------------------------------------------------- visibility and wake-up math

    [Fact]
    public void TheEarliestDeadlineOfAllVisibleAnimationsIsReturned()
    {
        using var rig = new KittyRig();
        rig.Store.TicksPerSecond = 1000;
        AddAnim(rig, 1, 0, 40, 40);
        AddAnim(rig, 2, 1, 100, 100);
        rig.Apc("a=a,i=1,s=3");
        rig.Apc("a=a,i=2,s=3");

        Assert.Equal(1040, rig.Store.AdvanceAnimations(1000, 24, 0));
        rig.Apc("a=a,i=1,s=1");
        Assert.Equal(1100, rig.Store.AdvanceAnimations(1001, 24, 0));
        rig.Apc("a=a,i=2,s=1");
        Assert.Equal(0, rig.Store.AdvanceAnimations(1002, 24, 0));
    }

    [Fact]
    public void ImagesAreAdvancedIndependentlyOfEachOther()
    {
        using var rig = new KittyRig();
        rig.Store.TicksPerSecond = 1000;
        var one = AddAnim(rig, 1, 0, 40, 40);
        var two = AddAnim(rig, 2, 1, 100, 100);
        rig.Apc("a=a,i=1,s=3");
        rig.Apc("a=a,i=2,s=3");

        rig.Store.AdvanceAnimations(1000, 24, 0);
        rig.Store.AdvanceAnimations(1040, 24, 0);

        Assert.Equal(1, one.CurrentFrameIndex);
        Assert.Equal(0, two.CurrentFrameIndex);
        Assert.Equal(1080, one.FrameDeadlineTicks);
        Assert.Equal(1100, two.FrameDeadlineTicks);
    }

    [Fact]
    public void AnImageWithTwoVisiblePlacementsStepsOncePerPass()
    {
        using var a = NewAnim(10, 10, 10);
        a.Store.Place(a.Image, "p=2", anchorRow: 3);
        a.Cmd("a=a,i=1,s=3");
        a.At(100);

        Assert.Equal(120, a.At(110));
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void AHiddenPlacementDoesNotHideTheImageWhenAnotherOneIsVisible()
    {
        using var a = new Anim(500, new[] { 10, 10 }); // the first placement is far below the viewport
        a.Store.Place(a.Image, "p=2", anchorRow: 0);
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(110, a.At(100));
        Assert.Equal(120, a.At(110));
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void AnImageWithoutAPlacementIsNeverAdvanced()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3");
        a.Cmd("a=d,d=i,i=1"); // deletes the placement, keeps the image
        Assert.NotNull(a.Store.FindByClientId(1));
        Assert.Equal(0, a.Store.PlacementCount);

        Assert.Equal(0, a.At(100));
        Assert.Equal(0, a.At(100_000));
        Assert.Equal(0, a.Current);
        Assert.Equal(0, a.Image.FrameDeadlineTicks);
    }

    [Fact]
    public void ScrollbackViewportDecidesVisibilityWithTheSameRuleAsCapture()
    {
        using var a = new Anim(-5, new[] { 10, 10 }); // one-row placement five rows into the history
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(0, a.Store.AdvanceAnimations(1000, 24, 0));
        Assert.Equal(0, a.Store.AdvanceAnimations(1000, 24, 4));   // viewRow -1: wholly above
        Assert.Equal(0, a.Image.FrameDeadlineTicks);
        Assert.Equal(1010, a.Store.AdvanceAnimations(1000, 24, 5)); // viewRow 0: on screen
    }

    [Theory]
    [InlineData(24, 24, false)] // anchored on the first row below a 24-row viewport
    [InlineData(25, 24, true)]
    [InlineData(24, 23, true)]  // anchored on the last row
    [InlineData(23, 23, false)]
    [InlineData(0, 0, false)]   // empty viewport
    public void BottomEdgeOfTheViewport(int rows, int anchorRow, bool visible)
    {
        using var a = new Anim(anchorRow, new[] { 10, 10 });
        a.Cmd("a=a,i=1,s=3");

        long deadline = a.Store.AdvanceAnimations(1000, rows, 0);

        Assert.Equal(visible ? 1010 : 0, deadline);
    }

    [Fact]
    public void ACoveredAnimationDoesNotAdvanceAndResumesWithoutACatchUpBurst()
    {
        using var a = NewAnim(10, 10, 10);
        a.Cmd("a=a,i=1,s=3");
        Assert.Equal(1010, a.At(1000));

        Assert.Equal(0, a.Store.AdvanceAnimations(9000, 24, -30)); // hidden: not stepped, not reported
        Assert.Equal(0, a.Current);
        Assert.Equal(1010, a.Image.FrameDeadlineTicks);

        Assert.Equal(9010, a.At(9000));                            // visible again: one step from now
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void RealisticTickFrequencyGivesAbsoluteDeadlines()
    {
        using var a = NewAnim(40, 0);
        a.Store.TicksPerSecond = 1_000_000_000;
        a.Cmd("a=a,i=1,s=3");

        Assert.Equal(5_000 + 40_000_000L, a.At(5_000));
        Assert.Equal(5_000 + 40_000_000L, a.At(5_000 + 39_999_999));
        Assert.Equal(0, a.Current);
        Assert.Equal(5_000 + 80_000_000L, a.At(5_000 + 40_000_000));
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void ReturnedDeadlinesAreAlwaysInTheFuture()
    {
        using var a = NewAnim(40, 1, 3, 7);
        a.Cmd("a=a,i=1,s=3");
        long now = 100;
        for (int i = 0; i < 200; i++)
        {
            long deadline = a.At(now);
            Assert.True(deadline > now, $"deadline {deadline} at {now}");
            now = deadline;
        }
    }

    // ---------------------------------------------------------------------------- snapshot capture integration

    [Fact]
    public void CaptureShowsTheCurrentFrameAndANewSerialAfterAStep()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3");
        a.At(100);

        using (var first = new KittyRenderSnapshot())
        {
            a.Store.CaptureVisible(first, 24, 0);
            var p = Assert.Single(first.Placements.ToArray());
            Assert.Same(a.Image.Frames[0].Canvas, p.Pixels);
            Assert.Equal(a.Image.Frames[0].Canvas.Serial, p.ImageSerial);

            a.At(110);
            using var second = new KittyRenderSnapshot();
            a.Store.CaptureVisible(second, 24, 0);
            var q = Assert.Single(second.Placements.ToArray());
            Assert.Same(a.Image.Frames[1].Canvas, q.Pixels);
            Assert.NotEqual(p.ImageSerial, q.ImageSerial);
            Assert.Equal(Col(0), p.Pixels.AsSpan().ToArray()); // the first lease still reads its own frame
        }
        a.Store.ValidateInvariants();
    }

    // ---------------------------------------------------------------------------------------- frame deletion

    private static void AssertFrames(Anim a, params int[] colourIndexes)
    {
        Assert.Equal(colourIndexes.Length, a.Image.Frames.Count);
        for (int i = 0; i < colourIndexes.Length; i++)
            Assert.Equal(Col(colourIndexes[i]), a.Image.Frames[i].Canvas.AsSpan().ToArray());
        Assert.Equal(4L * colourIndexes.Length, a.Store.UsedBytes);
        a.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData('f')]
    [InlineData('F')]
    public void DeletingAMiddleFrameShiftsTheLaterOnesAndKeepsTheirGaps(char d)
    {
        using var a = NewAnim(40, 10, 20, 30);
        int changes = a.Rig.Host.GraphicsChangedCalls;

        a.Cmd($"a=d,d={d},i=1,r=2");

        AssertFrames(a, 0, 2, 3);
        Assert.Equal(new[] { 40, 20, 30 }, a.Image.Frames.Select(f => f.GapMs));
        Assert.Equal(0, a.Current);
        Assert.Equal(1, a.Store.PlacementCount); // the image is still shown
        Assert.Equal(changes + 1, a.Rig.Host.GraphicsChangedCalls);
        Assert.Empty(a.Rig.Replies); // deletes never answer
    }

    [Fact]
    public void DeletingTheRootPromotesTheSecondFrame()
    {
        using var a = NewAnim(0, 10, 20, 30);

        a.Cmd("a=d,d=f,i=1,r=1");

        AssertFrames(a, 1, 2, 3);
        Assert.Equal(new[] { 10, 20, 30 }, a.Image.Frames.Select(f => f.GapMs));
        Assert.Equal(Col(1), a.Image.Root.AsSpan().ToArray()); // the displayed pixels are the promoted frame's
        Assert.Equal(1, a.Store.Count);
    }

    [Theory]
    [InlineData('f')]
    [InlineData('F')]
    public void WithoutAFrameNumberNothingIsDeleted(char d)
    {
        using var a = NewAnim(0, 10, 20, 30);
        int changes = a.Rig.Host.GraphicsChangedCalls;

        a.Cmd($"a=d,d={d},i=1");
        a.Cmd($"a=d,d={d},i=1,r=0");

        AssertFrames(a, 0, 1, 2, 3);
        Assert.Equal(changes, a.Rig.Host.GraphicsChangedCalls);
    }

    [Theory]
    [InlineData("r=5")]
    [InlineData("r=4294967295")]
    public void ANonexistentFrameDeletesNothing(string keys)
    {
        using var a = NewAnim(0, 10, 20, 30);
        int changes = a.Rig.Host.GraphicsChangedCalls;

        a.Cmd("a=d,d=f,i=1," + keys);

        AssertFrames(a, 0, 1, 2, 3);
        Assert.Equal(changes, a.Rig.Host.GraphicsChangedCalls);
    }

    [Fact]
    public void FrameDeleteForAnUnknownOrUnaddressedImageDoesNothing()
    {
        using var a = NewAnim(0, 10);
        a.Cmd("a=d,d=f,i=9,r=1");
        a.Cmd("a=d,d=f,r=1");
        a.Cmd("a=d,d=f,I=4,r=1");

        AssertFrames(a, 0, 1);
    }

    [Fact]
    public void FrameDeleteAddressesAnImageByNumber()
    {
        using var rig = new KittyRig();
        rig.Apc("I=8,s=1,v=1,f=32", B64(Col(0)));
        rig.Apc("a=f,I=8,s=1,v=1,f=32", B64(Col(1)));

        rig.Apc("a=d,d=f,I=8,r=1");

        var image = rig.Store.FindByNumber(8)!;
        Assert.Equal(1, image.Frames.Count);
        Assert.Equal(Col(1), image.Root.AsSpan().ToArray());
    }

    [Theory]
    [InlineData(3, 1, 1)]   // current frame 3 (index 2), delete frame 1 -> the same canvas, now at index 1
    [InlineData(3, 3, 2)]   // delete the displayed frame: the one that took its place (old frame 4) is shown
    [InlineData(4, 4, 2)]   // displayed frame was the last: the new last frame is shown
    [InlineData(4, 2, 2)]   // delete before the displayed frame
    [InlineData(1, 2, 0)]   // delete after: unchanged
    public void TheDisplayedFrameSurvivesOrIsReplacedSensibly(int currentFrameNumber, int deleteNumber, int expectedIndex)
    {
        using var a = NewAnim(0, 10, 20, 30);
        a.Cmd($"a=a,i=1,c={currentFrameNumber}");
        byte[] shownBefore = a.Image.Root.AsSpan().ToArray();

        a.Cmd($"a=d,d=f,i=1,r={deleteNumber}");

        Assert.Equal(expectedIndex, a.Current);
        Assert.Same(a.Image.Frames[a.Current].Canvas, a.Image.Root);
        int[] remaining = Enumerable.Range(0, 4).Where(i => i != deleteNumber - 1).ToArray();
        Assert.Equal(Col(remaining[expectedIndex]), a.Image.Root.AsSpan().ToArray()); // and it is the right original frame
        if (deleteNumber != currentFrameNumber)
            Assert.Equal(shownBefore, a.Image.Root.AsSpan().ToArray()); // a surviving displayed frame is unchanged
    }

    [Theory]
    [InlineData('f')]
    [InlineData('F')]
    public void DeletingTheOnlyFrameFreesTheImageAndItsPlacements(char d)
    {
        var baseline = new GraphicsBaseline();
        using (var a = NewAnim(0))
        {
            Assert.Equal(1, a.Store.PlacementCount);

            a.Cmd($"a=d,d={d},i=1,r=1");

            Assert.Null(a.Store.FindByClientId(1));
            Assert.Equal(0, a.Store.PlacementCount);
            Assert.Equal(0, a.Store.UsedBytes);
            Assert.True(a.Image.IsDeleted);
            a.Store.ValidateInvariants();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void FramesCanBeDeletedWhilePlayingDownToASingleFrame()
    {
        using var a = NewAnim(10, 10, 10);
        a.Cmd("a=a,i=1,s=3");
        a.At(100);
        a.At(110);
        Assert.Equal(1, a.Current);

        a.Cmd("a=d,d=f,i=1,r=2");               // delete the displayed frame while running
        Assert.Equal(1, a.Current);              // frame 3 took its place
        Assert.Equal(Col(2), a.Image.Root.AsSpan().ToArray());
        Assert.Equal(215, a.At(205));            // timer restarted from the first pass after the change

        a.Cmd("a=d,d=f,i=1,r=1");
        Assert.Equal(0, a.At(300));              // one frame left: nothing to wait for
        AssertFrames(a, 2);
    }

    [Fact]
    public void PlaybackResumesWhenAFrameIsAddedAfterDeletingDownToOne()
    {
        using var a = NewAnim(10, 10);
        a.Cmd("a=a,i=1,s=3");
        a.Cmd("a=d,d=f,i=1,r=2");
        Assert.Equal(0, a.At(100));              // a single frame: nothing to wait for
        Assert.Equal(KittyAnimationState.Running, a.Image.AnimationState);

        AnimKit.LoadFrame(a.Rig, 1, 1, 1, Col(5), ",z=20");

        Assert.Equal(110, a.At(100));            // the root frame's gap
        Assert.Equal(130, a.At(110));            // then the new frame's
        Assert.Equal(1, a.Current);
    }

    [Fact]
    public void DeletedFramesReturnTheirBytesAndLeaseHoldersKeepTheirPixels()
    {
        var baseline = new GraphicsBaseline();
        using (var a = NewAnim(0, 10, 20))
        {
            var lease = a.Image.Frames[2].Canvas.Retain();

            a.Cmd("a=d,d=f,i=1,r=3");

            Assert.Equal(8, a.Store.UsedBytes);
            Assert.Equal(Col(2), lease.AsSpan().ToArray());
            lease.Release();
        }
        baseline.AssertRestored();
    }

    [Fact]
    public void FramesAreReloadableAfterDeletion()
    {
        using var a = NewAnim(0, 10);
        a.Cmd("a=d,d=f,i=1,r=2");
        a.Rig.Replies.Clear();

        AnimKit.LoadFrame(a.Rig, 1, 1, 1, Col(9), ",z=15");

        Assert.Equal(new[] { Ok("i=1") }, a.Rig.Replies);
        AssertFrames(a, 0, 9);
        Assert.Equal(15, a.Image.Frames[1].GapMs);
    }

    // ----------------------------------------------------------------- random sequences and invariants

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void RandomCommandSequencesKeepTheStoreConsistent(int seed)
    {
        var baseline = new GraphicsBaseline();
        using (var rig = new KittyRig())
        {
            var rnd = new Random(seed);
            var store = rig.Store;
            store.TicksPerSecond = 1000;
            long now = 1000;

            for (int step = 0; step < 400; step++)
            {
                uint id = (uint)rnd.Next(1, 4);
                switch (rnd.Next(11))
                {
                    case 0:
                        rig.Apc($"i={id},s=3,v=2,f=32", B64(RandomBytes(24, rnd.Next())));
                        break;
                    case 1:
                    case 2:
                    case 3:
                    {
                        int s = rnd.Next(1, 4), v = rnd.Next(1, 3);
                        rig.Apc(
                            $"a=f,i={id},s={s},v={v},f=32,r={rnd.Next(0, 5)},c={rnd.Next(0, 5)},x={rnd.Next(0, 4)},y={rnd.Next(0, 3)}," +
                            $"z={rnd.Next(-2, 60)},X={rnd.Next(0, 2)},Y={rnd.Next(0, 2) * 4278190208L},N={rnd.Next(0, 2)}",
                            B64(RandomBytes(4 * s * v, rnd.Next())));
                        break;
                    }
                    case 4:
                        rig.Apc(
                            $"a=c,i={id},r={rnd.Next(0, 5)},c={rnd.Next(0, 5)},X={rnd.Next(0, 3)},Y={rnd.Next(0, 3)}," +
                            $"x={rnd.Next(0, 3)},y={rnd.Next(0, 3)},w={rnd.Next(0, 4)},h={rnd.Next(0, 3)},C={rnd.Next(0, 2)}");
                        break;
                    case 5:
                        rig.Apc($"a=a,i={id},s={rnd.Next(0, 4)},v={rnd.Next(0, 4)},r={rnd.Next(0, 5)},z={rnd.Next(-2, 60)},c={rnd.Next(0, 5)}");
                        break;
                    case 6:
                        rig.Apc($"a=d,d={(rnd.Next(2) == 0 ? 'f' : 'F')},i={id},r={rnd.Next(0, 5)}");
                        break;
                    case 7:
                        rig.Apc($"a=p,i={id},p={rnd.Next(1, 3)},C=1");
                        break;
                    case 8:
                        rig.Apc($"a=d,d={(rnd.Next(2) == 0 ? 'i' : 'I')},i={id}");
                        break;
                    case 9:
                    {
                        now += rnd.Next(0, 120);
                        long deadline = store.AdvanceAnimations(now, 24, 0);
                        Assert.True(deadline == 0 || deadline > now, $"deadline {deadline} at {now}");
                        break;
                    }
                    default:
                        store.SetStorageLimit(rnd.Next(40, 400));
                        break;
                }
                store.ValidateInvariants();
            }

            foreach (var image in store.Images)
                Assert.Same(image.Frames[image.CurrentFrameIndex].Canvas, image.Root);
        }
        baseline.AssertRestored();
    }

    // -------------------------------------------------------------------- terminal buffer (manual clock)

    /// <summary>A clock that only moves when told to: 1000 ticks per second, so one tick is one millisecond.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _now = 1_000;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Volatile.Read(ref _now);

        public long Now => Volatile.Read(ref _now);

        public void Advance(long ticks) => _now += ticks;
    }

    private sealed class BufferRig : IDisposable
    {
        private const int CellW = 10, CellH = 20;

        public ManualClock Clock { get; } = new();
        public TerminalAdapter Adapter { get; }
        public BasicAnsiParser Parser { get; }

        public BufferRig()
        {
            Adapter = new TerminalAdapter(6, 10, 100, Clock);
            Adapter.SetCellPixelSize(CellW, CellH);
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public KittyImageStore Store => Adapter.Buffer.ActiveImageStore;

        public void Apc(string control, string payload = "") => Parser.Feed(ApcBytes(control, payload));

        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));

        public RenderSnapshot Capture(int scrollOffset = 0) => Adapter.Buffer.CaptureRenderSnapshotVisible(0, -1, scrollOffset);

        public static string Pixels(int fill) => Convert.ToBase64String(Enumerable.Repeat((byte)fill, CellW * CellH * 4).ToArray());

        /// <summary>Two frames of 2 ms each (the root frame's gap is set explicitly), playing, placed at the cursor with C=1.</summary>
        public KittyImage PlayTwoFrameAnimation()
        {
            Apc("a=T,i=1,s=10,v=20,f=32,c=1,r=1,C=1", Pixels(10));
            Apc("a=f,i=1,s=10,v=20,f=32,z=2", Pixels(20));
            Apc("a=a,i=1,s=3,r=1,z=2");
            return Store.FindByClientId(1)!;
        }

        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void BufferCaptureAdvancesVisibleAnimationsAndReportsTheWakeDeadline()
    {
        using var t = new BufferRig();
        var image = t.PlayTwoFrameAnimation();
        long start = t.Clock.Now;

        long firstSerial;
        using (var snap = t.Capture())
        {
            var graphics = snap.Graphics!;
            var placement = Assert.Single(graphics.Placements.ToArray());
            firstSerial = placement.ImageSerial;
            // The first capture arms the timer: 2 ms at 1000 ticks per second is two ticks from now. (The store runs
            // on the adapter's clock, not on the system one.)
            Assert.Equal(start + 2, graphics.NextAnimationDeadlineTicks);
        }
        Assert.Equal(0, image.CurrentFrameIndex);

        t.Clock.Advance(1); // one tick before the deadline: nothing changes
        using (var snap = t.Capture())
        {
            var placement = Assert.Single(snap.Graphics!.Placements.ToArray());
            Assert.Equal(firstSerial, placement.ImageSerial);
            Assert.Equal(start + 2, snap.Graphics.NextAnimationDeadlineTicks);
        }

        t.Clock.Advance(1); // exactly at the deadline: frame 2 is current and the next change is 2 ms away
        long secondSerial;
        using (var snap = t.Capture())
        {
            var placement = Assert.Single(snap.Graphics!.Placements.ToArray());
            secondSerial = placement.ImageSerial;
            Assert.NotEqual(firstSerial, secondSerial);
            Assert.Equal(start + 4, snap.Graphics.NextAnimationDeadlineTicks);
        }
        Assert.Equal(1, image.CurrentFrameIndex);

        t.Clock.Advance(2); // wraps to the root frame, which keeps its texture key
        using (var snap = t.Capture())
        {
            var placement = Assert.Single(snap.Graphics!.Placements.ToArray());
            Assert.Equal(firstSerial, placement.ImageSerial);
            Assert.Equal(start + 6, snap.Graphics.NextAnimationDeadlineTicks);
        }
        Assert.Equal(0, image.CurrentFrameIndex);
        t.Store.ValidateInvariants();
        Assert.Equal(2, image.Frames.Count);
    }

    [Fact]
    public void BufferCaptureAfterALongStallStepsOneFrameAndRearmsFromNow()
    {
        using var t = new BufferRig();
        var image = t.PlayTwoFrameAnimation();

        using (t.Capture()) { } // arms the timer
        t.Clock.Advance(1_000_000);
        long stalled = t.Clock.Now;

        using var snap = t.Capture();

        Assert.Equal(1, image.CurrentFrameIndex); // one step, not a burst of half a million
        Assert.Equal(stalled + 2, snap.Graphics!.NextAnimationDeadlineTicks);
    }

    [Fact]
    public void BufferCaptureOfAScrolledOffAnimationDoesNotAdvanceItAndHasNoDeadline()
    {
        using var t = new BufferRig();
        var image = t.PlayTwoFrameAnimation();
        t.Feed("\u001b[6;1H" + new string('\n', 8)); // eight rows of scrolling put the placement into the history

        for (int i = 0; i < 20; i++)
        {
            t.Clock.Advance(5);
            using var snap = t.Capture();
            Assert.Empty(snap.Graphics!.Placements.ToArray());
            Assert.Equal(0, snap.Graphics.NextAnimationDeadlineTicks);
        }
        Assert.Equal(0, image.CurrentFrameIndex);
        Assert.Equal(0, image.FrameDeadlineTicks);

        // Viewed in the scrollback the placement is visible, so it runs: first capture arms, the next one after the gap steps.
        long viewedAt = t.Clock.Now;
        long firstSerial;
        using (var scrolled = t.Capture(scrollOffset: 8))
        {
            var placement = Assert.Single(scrolled.Graphics!.Placements.ToArray());
            firstSerial = placement.ImageSerial;
            Assert.Equal(viewedAt + 2, scrolled.Graphics.NextAnimationDeadlineTicks);
        }
        t.Clock.Advance(2);
        using (var scrolled = t.Capture(scrollOffset: 8))
        {
            var placement = Assert.Single(scrolled.Graphics!.Placements.ToArray());
            Assert.NotEqual(firstSerial, placement.ImageSerial);
        }
        Assert.Equal(1, image.CurrentFrameIndex);

        // Back on the live screen, however much time passes, the hidden animation stays where it is.
        t.Clock.Advance(10_000);
        using (var live = t.Capture())
        {
            Assert.Empty(live.Graphics!.Placements.ToArray());
            Assert.Equal(0, live.Graphics.NextAnimationDeadlineTicks);
        }
        Assert.Equal(1, image.CurrentFrameIndex);
    }

    [Fact]
    public void BufferCaptureWithoutAnimationsReportsNoDeadline()
    {
        using var t = new BufferRig();
        t.Apc("a=T,i=1,s=10,v=20,f=32,c=1,r=1,C=1", BufferRig.Pixels(10));

        using var snap = t.Capture();

        Assert.Single(snap.Graphics!.Placements.ToArray());
        Assert.Equal(0, snap.Graphics.NextAnimationDeadlineTicks);
    }
}
