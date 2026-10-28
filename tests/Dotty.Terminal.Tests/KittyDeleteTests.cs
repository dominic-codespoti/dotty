using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Terminal.Graphics;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Every <c>d</c> value of <c>a=d</c> against one fixed layout (10 visible rows, 8x16 px cells, ViewTop 0):
/// <code>
/// PA1  image A (i=1)        id 1  rows 0-1  cols 0-1    z 0
/// PA2  image A              id 2  row 5     cols 10-13  z 5
/// PA3  image A              id 3  row -4 (history) col 0 z 0
/// PB   image B (i=2, I=7)   id 1  rows 2-4  cols 4-6    z -1
/// PC   image C (anonymous)  -     row 9     col 0       z 5
/// PV   image E (i=4, I=9)   id 1  virtual 5x2 grid
/// </code>
/// Image F (i=5) has no placement.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyDeleteTests
{
    private const int Rows = 10;

    private sealed class Fixture : IDisposable
    {
        public KittyImageStore Store { get; } = new(100_000_000);
        public KittyImage A, B, C, E, F;
        public Dictionary<KittyPlacement, string> Names { get; } = new();

        public Fixture()
        {
            A = Store.AddImage(40, 20, 1);
            B = Store.AddImage(40, 20, 2, number: 7);
            E = Store.AddImage(40, 20, 4, number: 9);
            F = Store.AddImage(40, 20, 5);
            C = Store.AddImage(40, 20); // last: adding another image would drop it (anonymous and not yet placed)
            Name("PA1", Store.Place(A, "p=1,c=2,r=2,z=0", 0, 0));
            Name("PA2", Store.Place(A, "p=2,c=4,r=1,z=5", 5, 10));
            Name("PA3", Store.Place(A, "p=3,c=1,r=1,z=0", -4, 0));
            Name("PB", Store.Place(B, "p=1,c=3,r=3,z=-1", 2, 4));
            Name("PC", Store.Place(C, "c=1,r=1,z=5", 9, 0));
            Name("PV", Store.Place(E, "U=1,p=1,c=5,r=2"));
        }

        private void Name(string name, KittyPlacement p) => Names[p] = name;

        public int Delete(string keys, int cursorRow = 0, int cursorCol = 0) =>
            Store.ApplyDelete(Parse("a=d," + keys), cursorRow, cursorCol, Rows);

        public string Left() => string.Join(",", Store.Placements.Select(p => Names[p]).OrderBy(n => n, StringComparer.Ordinal));

        public string Images() => string.Join(",", Store.Images.Select(i => i.ClientId == 0 ? "anon" : i.ClientId.ToString()).OrderBy(n => n, StringComparer.Ordinal));

        public void Dispose() => Store.Dispose();
    }

    private const string All = "PA1,PA2,PA3,PB,PC,PV";

    // ---------------------------------------------------------------------------------- placement selection

    [Theory]
    // a: placements intersecting the visible screen (normal kind only)
    [InlineData("d=a", 0, 0, "PA3,PV")]
    // i / n by id or number, optionally one placement
    [InlineData("d=i,i=1", 0, 0, "PB,PC,PV")]
    [InlineData("d=i,i=1,p=2", 0, 0, "PA1,PA3,PB,PC,PV")]
    [InlineData("d=i,i=1,p=9", 0, 0, All)]
    [InlineData("d=i,i=0", 0, 0, All)]
    [InlineData("d=i", 0, 0, All)]
    [InlineData("d=i,i=99", 0, 0, All)]
    [InlineData("d=i,i=4", 0, 0, "PA1,PA2,PA3,PB,PC")]    // virtual placements are reachable by id
    [InlineData("d=i,i=5", 0, 0, All)]                    // image F has no placement
    [InlineData("d=n,I=7", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=n,I=7,p=1", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=n,I=7,p=2", 0, 0, All)]
    [InlineData("d=n,I=99", 0, 0, All)]
    [InlineData("d=n", 0, 0, All)]
    [InlineData("d=n,I=9", 0, 0, "PA1,PA2,PA3,PB,PC")]    // virtual placement, by number
    // c: the cursor cell (0-based cursor, bounds inclusive)
    [InlineData("d=c", 3, 5, "PA1,PA2,PA3,PC,PV")]        // PB covers rows 2-4, cols 4-6
    [InlineData("d=c", 0, 0, "PA2,PA3,PB,PC,PV")]         // PA1 only: the virtual placement is not at a cell
    [InlineData("d=c", 1, 1, "PA2,PA3,PB,PC,PV")]         // bottom-right cell of PA1
    [InlineData("d=c", 2, 1, All)]
    [InlineData("d=c", 5, 9, All)]                        // one left of PA2
    [InlineData("d=c", 5, 10, "PA1,PA3,PB,PC,PV")]
    [InlineData("d=c", 5, 13, "PA1,PA3,PB,PC,PV")]
    [InlineData("d=c", 5, 14, All)]                       // one right of PA2
    [InlineData("d=c", 9, 0, "PA1,PA2,PA3,PB,PV")]
    // p: cell (x, y), 1-based
    [InlineData("d=p,x=5,y=3", 0, 0, "PA1,PA2,PA3,PC,PV")] // top-left cell of PB
    [InlineData("d=p,x=4,y=3", 0, 0, All)]
    [InlineData("d=p,x=7,y=5", 0, 0, "PA1,PA2,PA3,PC,PV")] // bottom-right cell of PB
    [InlineData("d=p,x=8,y=5", 0, 0, All)]
    [InlineData("d=p,x=7,y=6", 0, 0, All)]
    [InlineData("d=p,x=1,y=1", 0, 0, "PA2,PA3,PB,PC,PV")]
    [InlineData("d=p,x=0,y=1", 0, 0, All)]                 // 1-based: 0 matches nothing
    [InlineData("d=p,x=1,y=0", 0, 0, All)]
    [InlineData("d=p", 0, 0, All)]
    // q: cell and z-index
    [InlineData("d=q,x=5,y=3,z=-1", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=q,x=5,y=3,z=0", 0, 0, All)]
    [InlineData("d=q,x=1,y=1,z=0", 0, 0, "PA2,PA3,PB,PC,PV")]
    [InlineData("d=q,x=11,y=6,z=5", 0, 0, "PA1,PA3,PB,PC,PV")]
    // x: column (any row), 1-based
    [InlineData("d=x,x=1", 0, 0, "PA2,PB,PV")]           // col 0: PA1, PC and the history placement PA3
    [InlineData("d=x,x=2", 0, 0, "PA2,PA3,PB,PC,PV")]    // col 1: PA1
    [InlineData("d=x,x=4", 0, 0, All)]
    [InlineData("d=x,x=5", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=x,x=14", 0, 0, "PA1,PA3,PB,PC,PV")]
    [InlineData("d=x,x=15", 0, 0, All)]
    [InlineData("d=x,x=0", 0, 0, All)]
    // y: visible row, 1-based
    [InlineData("d=y,y=1", 0, 0, "PA2,PA3,PB,PC,PV")]
    [InlineData("d=y,y=2", 0, 0, "PA2,PA3,PB,PC,PV")]
    [InlineData("d=y,y=3", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=y,y=5", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=y,y=6", 0, 0, "PA1,PA3,PB,PC,PV")]
    [InlineData("d=y,y=10", 0, 0, "PA1,PA2,PA3,PB,PV")]
    [InlineData("d=y,y=11", 0, 0, All)]
    [InlineData("d=y,y=0", 0, 0, All)]
    // z: z-index, history placements included, virtual ones never
    [InlineData("d=z,z=5", 0, 0, "PA1,PA3,PB,PV")]
    [InlineData("d=z,z=-1", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=z,z=0", 0, 0, "PA2,PB,PC,PV")]        // PV has z 0 too and stays
    [InlineData("d=z,z=3", 0, 0, All)]
    // r: inclusive id range, empty allowed
    [InlineData("d=r,x=1,y=2", 0, 0, "PC,PV")]
    [InlineData("d=r,x=2,y=1", 0, 0, All)]
    [InlineData("d=r,x=2,y=2", 0, 0, "PA1,PA2,PA3,PC,PV")]
    [InlineData("d=r,x=1,y=1", 0, 0, "PB,PC,PV")]
    [InlineData("d=r,x=3,y=3", 0, 0, All)]
    [InlineData("d=r,x=4,y=4", 0, 0, "PA1,PA2,PA3,PB,PC")]
    [InlineData("d=r,x=0,y=100", 0, 0, "PC")]
    [InlineData("d=r,x=5,y=5", 0, 0, All)]
    [InlineData("d=r", 0, 0, All)]                        // x=0,y=0: no image has id 0
    // frames and unknown values
    [InlineData("d=f", 0, 0, All)]
    [InlineData("d=F,i=1", 0, 0, All)]
    [InlineData("d=k", 0, 0, All)]
    [InlineData("d=K,i=1", 0, 0, All)]
    public void LowerCaseDeletesExactlyTheMatchingPlacements(string keys, int cursorRow, int cursorCol, string remaining)
    {
        using var f = new Fixture();
        f.Delete(keys, cursorRow, cursorCol);
        Assert.Equal(remaining, f.Left());
        f.Store.ValidateInvariants();
    }

    [Fact]
    public void WithoutADKeyTheDefaultIsAllVisiblePlacements()
    {
        using var f = new Fixture();
        f.Delete("");
        Assert.Equal("PA3,PV", f.Left());
    }

    [Theory]
    [InlineData("d=a")]
    [InlineData("d=i,i=1")]
    [InlineData("d=i,i=1,p=2")]
    [InlineData("d=n,I=7")]
    [InlineData("d=c")]
    [InlineData("d=p,x=5,y=3")]
    [InlineData("d=q,x=5,y=3,z=-1")]
    [InlineData("d=x,x=1")]
    [InlineData("d=y,y=3")]
    [InlineData("d=z,z=5")]
    [InlineData("d=r,x=1,y=2")]
    public void UpperCaseDeletesTheSamePlacementsAsLowerCase(string lowerKeys)
    {
        string upperKeys = "d=" + char.ToUpperInvariant(lowerKeys[2]) + lowerKeys.Substring(3);
        using var lower = new Fixture();
        using var upper = new Fixture();
        lower.Delete(lowerKeys, 3, 5);
        upper.Delete(upperKeys, 3, 5);
        Assert.Equal(lower.Left(), upper.Left());
        upper.Store.ValidateInvariants();
    }

    // ------------------------------------------------------------------------------------- freeing images

    [Fact]
    public void LowerCaseKeepsImagesAndDropsAnonymousOnesWithTheirLastPlacement()
    {
        using var f = new Fixture();
        f.Delete("d=a");
        Assert.Equal("1,2,4,5", f.Images()); // image C (anonymous) went with PC, B and F have ids and stay
    }

    [Fact]
    public void UpperCaseFreesOnlyMatchedImagesLeftWithoutPlacements()
    {
        using var f = new Fixture();
        f.Delete("d=A");
        // A matched but keeps its history placement PA3; B matched and is empty -> freed; C anonymous -> gone;
        // E (virtual only) and F (never placed) did not match.
        Assert.Equal("1,4,5", f.Images());
        Assert.True(f.B.IsDeleted);
        Assert.False(f.A.IsDeleted);
        Assert.False(f.F.IsDeleted);
        Assert.False(f.E.IsDeleted);
        f.Store.ValidateInvariants();
    }

    [Theory]
    [InlineData("d=I,i=1", "2,4,5,anon")]                    // all of A's placements and A itself
    [InlineData("d=I,i=1,p=2", "1,2,4,5,anon")]              // A keeps PA1 and PA3
    [InlineData("d=I,i=1,p=9", "1,2,4,5,anon")]              // no such placement: nothing matched, nothing freed
    [InlineData("d=I,i=2,p=1", "1,4,5,anon")]                // B's only placement: freed
    [InlineData("d=I,i=5", "1,2,4,anon")]                    // matched by id although it never had a placement
    [InlineData("d=i,i=5", "1,2,4,5,anon")]
    [InlineData("d=I,i=0", "1,2,4,5,anon")]
    [InlineData("d=I,i=99", "1,2,4,5,anon")]
    [InlineData("d=N,I=7", "1,4,5,anon")]
    [InlineData("d=N,I=7,p=2", "1,2,4,5,anon")]
    [InlineData("d=N,I=9", "1,2,5,anon")]
    [InlineData("d=Z,z=-1", "1,4,5,anon")]
    [InlineData("d=Z,z=5", "1,2,4,5")]                      // PA2 (A keeps others) and PC (anonymous image gone)
    [InlineData("d=Y,y=6", "1,2,4,5,anon")]
    [InlineData("d=X,x=1", "1,2,4,5")]                       // PA1/PA3 (A keeps PA2) and PC
    [InlineData("d=R,x=1,y=2", "4,5,anon")]
    [InlineData("d=R,x=5,y=5", "1,2,4,anon")]
    [InlineData("d=r,x=5,y=5", "1,2,4,5,anon")]
    [InlineData("d=R,x=2,y=1", "1,2,4,5,anon")]              // empty range
    [InlineData("d=F,i=1", "1,2,4,5,anon")]
    public void UpperCaseFreeingRules(string keys, string imagesLeft)
    {
        using var f = new Fixture();
        f.Delete(keys);
        Assert.Equal(imagesLeft, f.Images());
        f.Store.ValidateInvariants();
    }

    [Fact]
    public void CursorAndCellVariantsFreeTheMatchedImage()
    {
        using var c = new Fixture();
        c.Delete("d=C", 3, 5);
        Assert.True(c.B.IsDeleted);

        using var p = new Fixture();
        p.Delete("d=P,x=5,y=3");
        Assert.True(p.B.IsDeleted);

        using var q = new Fixture();
        q.Delete("d=Q,x=5,y=3,z=-1");
        Assert.True(q.B.IsDeleted);

        using var miss = new Fixture();
        miss.Delete("d=Q,x=5,y=3,z=0");
        Assert.False(miss.B.IsDeleted);
    }

    [Fact]
    public void ADeleteReportsWhatItChanged()
    {
        using var f = new Fixture();
        Assert.Equal(0, f.Delete("d=i,i=99"));
        Assert.Equal(0, f.Delete("d=f"));
        Assert.Equal(3, f.Delete("d=i,i=1"));           // three placements
        Assert.Equal(2, f.Delete("d=I,i=2"));           // PB and the image itself
        Assert.Equal(1, f.Delete("d=I,i=5"));           // image only
    }

    [Fact]
    public void AnAnonymousImageLivesUntilItsLastPlacementIsDeleted()
    {
        using var store = new KittyImageStore(1_000_000);
        var anonymous = store.AddImage(40, 20);
        store.Place(anonymous, "c=1,r=1", anchorRow: 0);
        store.Place(anonymous, "c=1,r=1", anchorRow: 5);

        Assert.Equal(1, store.ApplyDelete(Parse("a=d,d=y,y=1"), 0, 0, Rows));
        Assert.False(anonymous.IsDeleted);
        Assert.Equal(1, store.PlacementCount);

        Assert.Equal(1, store.ApplyDelete(Parse("a=d,d=y,y=6"), 0, 0, Rows)); // the sweep of the image is not counted
        Assert.True(anonymous.IsDeleted);
        Assert.Equal(0, store.Count);
        store.ValidateInvariants();
    }

    // --------------------------------------------------------------------------------- virtual placements

    [Theory]
    [InlineData("d=a")]
    [InlineData("d=A")]
    [InlineData("d=c")]
    [InlineData("d=C")]
    [InlineData("d=p,x=1,y=1")]
    [InlineData("d=P,x=1,y=1")]
    [InlineData("d=q,x=1,y=1,z=0")]
    [InlineData("d=x,x=1")]
    [InlineData("d=X,x=1")]
    [InlineData("d=y,y=1")]
    [InlineData("d=Y,y=1")]
    [InlineData("d=z,z=0")]
    [InlineData("d=Z,z=0")]
    public void VirtualPlacementsSurviveEveryPositionalDelete(string keys)
    {
        using var f = new Fixture();
        f.Delete(keys, 0, 0);
        Assert.Contains("PV", f.Left());
        Assert.False(f.E.IsDeleted);
    }

    [Theory]
    [InlineData("d=i,i=4", false)]
    [InlineData("d=I,i=4", true)]
    [InlineData("d=n,I=9", false)]
    [InlineData("d=N,I=9", true)]
    [InlineData("d=r,x=4,y=4", false)]
    [InlineData("d=R,x=4,y=4", true)]
    public void VirtualPlacementsGoByIdNumberAndRangeOnly(string keys, bool imageFreed)
    {
        using var f = new Fixture();
        f.Delete(keys);
        Assert.DoesNotContain("PV", f.Left());
        Assert.Equal(imageFreed, f.E.IsDeleted);
    }

    // ------------------------------------------------------------------------------------ visibility edges

    [Fact]
    public void APlacementShowingOneRowAtTheTopIsVisibleAndOneAboveIsNot()
    {
        // PB occupies absolute rows 2..4. With ViewTop 4 its last row is the first visible row; with ViewTop 5 it is
        // wholly above the screen, so d=a leaves it alone.
        using var shows = new Fixture();
        shows.Store.OnRingRotatedUp(4, 100);
        shows.Delete("d=a");
        Assert.DoesNotContain("PB", shows.Left());

        using var hidden = new Fixture();
        hidden.Store.OnRingRotatedUp(5, 100);
        hidden.Delete("d=a");
        Assert.Contains("PB", hidden.Left());
    }

    [Fact]
    public void ARowJustBelowTheScreenIsNotVisible()
    {
        using var f = new Fixture();
        var below = f.Store.Place(f.F, "c=1,r=1", anchorRow: Rows);
        var last = f.Store.Place(f.F, "c=1,r=1", anchorRow: Rows - 1);
        f.Names[below] = "below";
        f.Names[last] = "last";
        f.Delete("d=a");
        Assert.Contains("below", f.Left());
        Assert.DoesNotContain("last", f.Left());
    }

    [Fact]
    public void PositionalDeletesUseVisibleScreenCoordinatesAfterScrolling()
    {
        using var f = new Fixture();
        f.Store.OnRingRotatedUp(6, 100); // ViewTop 6: PC (absolute row 9) is on screen row 3
        f.Delete("d=y,y=4");
        Assert.DoesNotContain("PC", f.Left());
        Assert.Contains("PB", f.Left());

        using var g = new Fixture();
        g.Store.OnRingRotatedUp(6, 100);
        g.Delete("d=c", 3, 0);
        Assert.DoesNotContain("PC", g.Left());
    }

    [Fact]
    public void DeleteVisibleOnlyTouchesWhatIsOnScreenAfterScrolling()
    {
        using var f = new Fixture();
        f.Store.OnRingRotatedUp(6, 100);
        f.Delete("d=a");
        Assert.Equal("PA1,PA2,PA3,PB,PV", f.Left()); // only PC is on screen
    }

    // ------------------------------------------------------------------------------------ via the controller

    [Fact]
    public void DeletesNeverAnswer()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=1,s=1,v=1,f=24,c=2,r=2", "AAAA");
        rig.Replies.Clear();
        rig.Apc("a=d,d=I,i=1");
        rig.Apc("a=d,d=i,i=1,q=0");
        rig.Apc("a=d,d=a");
        rig.Apc("a=d,d=i,i=404");
        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void DeleteAbortsAnOpenPartialUpload()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=2,v=1,f=24,m=1", B64(new byte[] { 1, 2, 3 }));
        Assert.True(rig.Controller.HasOpenTransfer);

        rig.Apc("a=d,d=i,i=0");           // deletes nothing, still aborts
        Assert.False(rig.Controller.HasOpenTransfer);

        rig.Apc("m=0", B64(new byte[] { 4, 5, 6 })); // the stray continuation is dropped silently
        Assert.Empty(rig.Replies);
        Assert.Equal(0, rig.Store.Count);
    }

    [Fact]
    public void DeleteByCursorUsesTheHostCursor()
    {
        using var rig = new KittyRig();
        rig.Apc("i=1,s=40,v=20,f=24", B64(new byte[40 * 20 * 3]));
        rig.Host.CursorRow = 2;
        rig.Host.CursorCol = 4;
        rig.Apc("a=p,i=1,c=3,r=3"); // cells rows 2-4, cols 4-6; the fake cursor moves to (4, 7)
        Assert.Equal(1, rig.Store.PlacementCount);

        rig.Host.CursorRow = 5;
        rig.Host.CursorCol = 5;
        rig.Apc("a=d,d=c");
        Assert.Equal(1, rig.Store.PlacementCount);   // row 5 is below the placement

        rig.Host.CursorRow = 4;
        rig.Host.CursorCol = 6;
        rig.Apc("a=d,d=c");
        Assert.Equal(0, rig.Store.PlacementCount);
        Assert.NotNull(rig.Store.FindByClientId(1)); // lower case keeps the image
    }

    [Fact]
    public void DeleteVisibleUsesTheHostRowCount()
    {
        using var rig = new KittyRig();
        rig.Host.Rows = 4;
        rig.Apc("i=1,s=1,v=1,f=24", "AAAA");
        rig.Host.CursorRow = 3;
        rig.Apc("a=p,i=1,p=1,C=1");
        rig.Host.CursorRow = 4; // one past the 4-row screen
        rig.Apc("a=p,i=1,p=2,C=1");
        rig.Apc("a=d,d=a");
        Assert.NotNull(rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 2));
        Assert.Null(rig.Store.FindPlacement(rig.Store.FindByClientId(1)!, 1));
    }

    [Fact]
    public void DeleteOnTheAlternateScreenLeavesTheMainScreenAlone()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=1,s=1,v=1,f=24,C=1", "AAAA");
        rig.Host.AlternateActive = true;
        rig.Apc("a=T,i=1,s=1,v=1,f=24,C=1", "AAAA");
        rig.Apc("a=d,d=A");
        Assert.Equal(0, rig.Host.AltStore.Count);
        Assert.Equal(1, rig.Host.MainStore.PlacementCount);
        Assert.Equal(1, rig.Host.MainStore.Count);
    }

    [Fact]
    public void DeletedPlacementsAnnounceAGraphicsChangeOnlyWhenSomethingChanged()
    {
        using var rig = new KittyRig();
        rig.Apc("a=T,i=1,s=1,v=1,f=24", "AAAA");
        int before = rig.Host.GraphicsChangedCalls;
        rig.Apc("a=d,d=i,i=404");
        Assert.Equal(before, rig.Host.GraphicsChangedCalls);
        rig.Apc("a=d,d=i,i=1");
        Assert.True(rig.Host.GraphicsChangedCalls > before);
    }
}
