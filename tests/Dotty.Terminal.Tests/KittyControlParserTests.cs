using System.Text;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection("Allocation-sensitive tests")]
public sealed class KittyControlParserTests
{
    private static (KittyParseResult Result, KittyGraphicsCommand Cmd) Parse(string control)
    {
        var r = KittyControlParser.TryParse(Encoding.Latin1.GetBytes(control), out var cmd);
        return (r, cmd);
    }

    private static KittyGraphicsCommand ParseOk(string control)
    {
        var (r, cmd) = Parse(control);
        Assert.Equal(KittyParseResult.Ok, r);
        return cmd;
    }

    [Fact]
    public void Defaults_AreSpecDefaults()
    {
        var c = ParseOk("");
        Assert.Equal(KittyAction.Transmit, c.Action);
        Assert.Equal((uint)32, c.Format);
        Assert.Equal('d', c.Medium);
        Assert.Equal('a', c.DeleteWhat);
        Assert.Equal('\0', c.Compression);
        Assert.Equal('\0', c.FeatureAction);
        Assert.Equal(0UL, c.Present);
        Assert.Equal((byte)0, c.Quiet);
        Assert.Equal(0, c.Z);
        Assert.Equal(0u, c.ImageId);
        Assert.False(c.HasBothIdAndNumber);
        Assert.False(c.Has('a'));
        Assert.False(c.Has('f'));
    }

    [Fact]
    public void ExplicitDefaults_ArePresent()
    {
        var c = ParseOk("a=t,f=32,t=d,d=a");
        Assert.True(c.Has('a'));
        Assert.True(c.Has('f'));
        Assert.True(c.Has('t'));
        Assert.True(c.Has('d'));
        Assert.False(c.Has('i'));
    }

    [Fact]
    public void AllKeys_ParseIntoTheirFields()
    {
        var c = ParseOk(
            "a=T,q=1,f=100,t=f,o=z,m=1,i=11,I=12,p=13,S=14,O=15,s=16,v=17,x=18,y=19,w=20,h=21," +
            "X=22,Y=23,c=24,r=25,z=-26,C=1,U=1,P=27,Q=28,H=-29,V=30,d=A,N=1,e=x");
        Assert.Equal(KittyAction.TransmitAndPut, c.Action);
        Assert.Equal((byte)1, c.Quiet);
        Assert.Equal(100u, c.Format);
        Assert.Equal('f', c.Medium);
        Assert.Equal('z', c.Compression);
        Assert.Equal((byte)1, c.More);
        Assert.Equal(11u, c.ImageId);
        Assert.Equal(12u, c.ImageNumber);
        Assert.Equal(13u, c.PlacementId);
        Assert.Equal(14u, c.DataSize);
        Assert.Equal(15u, c.DataOffset);
        Assert.Equal(16u, c.Width);
        Assert.Equal(17u, c.Height);
        Assert.Equal(18u, c.SrcX);
        Assert.Equal(19u, c.SrcY);
        Assert.Equal(20u, c.SrcW);
        Assert.Equal(21u, c.SrcH);
        Assert.Equal(22u, c.CellXOff);
        Assert.Equal(23u, c.CellYOff);
        Assert.Equal(24u, c.Cols);
        Assert.Equal(25u, c.Rows);
        Assert.Equal(-26, c.Z);
        Assert.Equal((byte)1, c.CursorPolicy);
        Assert.Equal((byte)1, c.Unicode);
        Assert.Equal(27u, c.ParentImage);
        Assert.Equal(28u, c.ParentPlacement);
        Assert.Equal(-29, c.ParentH);
        Assert.Equal(30, c.ParentV);
        Assert.Equal('A', c.DeleteWhat);
        Assert.Equal(1u, c.Hints);
        Assert.Equal('x', c.FeatureAction);
        foreach (char k in "aqftomiIpSOsvxywhXYcrzCUPQHVdNe")
            Assert.True(c.Has(k), k.ToString());
        Assert.False(c.Has('A'));
        Assert.False(c.Has('b'));
    }

    [Fact]
    public void OverloadedKeys_AreExposedUnderPerActionAliases()
    {
        var c = ParseOk("a=f,r=3,c=2,s=5,v=6,z=-1,X=1,Y=4278190335");
        Assert.Equal(3u, c.FrameNumber);
        Assert.Equal(3u, c.Rows);
        Assert.Equal(2u, c.OtherFrame);
        Assert.Equal(2u, c.Cols);
        Assert.Equal(5u, c.AnimState);
        Assert.Equal(5u, c.Width);
        Assert.Equal(6u, c.Loops);
        Assert.Equal(6u, c.Height);
        Assert.Equal(-1, c.Gap);
        Assert.Equal((byte)1, c.Blend);
        Assert.Equal(1u, c.CellXOff);
        Assert.Equal(0xFF0000FFu, c.BgColor);
        Assert.Equal(0xFF0000FFu, c.CellYOff);
    }

    [Fact]
    public void BlendSaturates_ButCellXOffKeepsFullValue()
    {
        var c = ParseOk("X=300");
        Assert.Equal((byte)255, c.Blend);
        Assert.Equal(300u, c.CellXOff);
    }

    [Fact]
    public void SpecExample_TransmitAndDisplayRgb()
    {
        var c = ParseOk("a=T,f=100,i=31,s=1,v=1");
        Assert.Equal(KittyAction.TransmitAndPut, c.Action);
        Assert.Equal(100u, c.Format);
        Assert.Equal(31u, c.ImageId);
        Assert.Equal(1u, c.Width);
        Assert.Equal(1u, c.Height);
        Assert.Equal('d', c.Medium);
    }

    [Fact]
    public void SpecExample_DeleteByIdAndPlacement()
    {
        var c = ParseOk("a=d,d=i,i=7,p=3");
        Assert.Equal(KittyAction.Delete, c.Action);
        Assert.Equal('i', c.DeleteWhat);
        Assert.Equal(7u, c.ImageId);
        Assert.Equal(3u, c.PlacementId);
    }

    [Fact]
    public void SpecExample_VirtualPlacement()
    {
        var c = ParseOk("a=p,i=1,U=1,c=10,r=5");
        Assert.Equal(KittyAction.Put, c.Action);
        Assert.Equal((byte)1, c.Unicode);
        Assert.Equal(10u, c.Cols);
        Assert.Equal(5u, c.Rows);
    }

    [Fact]
    public void SpecExample_RelativePlacementWithNegativeOffset()
    {
        var c = ParseOk("a=p,i=9,P=1,Q=2,H=3,V=-4");
        Assert.Equal(1u, c.ParentImage);
        Assert.Equal(2u, c.ParentPlacement);
        Assert.Equal(3, c.ParentH);
        Assert.Equal(-4, c.ParentV);
    }

    [Fact]
    public void SpecExample_AnimationControl()
    {
        var c = ParseOk("a=a,i=2,s=3,v=2,r=1,z=100,c=4");
        Assert.Equal(KittyAction.Animate, c.Action);
        Assert.Equal(3u, c.AnimState);
        Assert.Equal(2u, c.Loops);
        Assert.Equal(1u, c.FrameNumber);
        Assert.Equal(100, c.Gap);
        Assert.Equal(4u, c.OtherFrame);
    }

    [Fact]
    public void SpecExample_QueryAndChunkContinuation()
    {
        var q = ParseOk("i=31,s=1,v=1,a=q,t=d,f=24");
        Assert.Equal(KittyAction.Query, q.Action);
        Assert.Equal(24u, q.Format);
        var chunk = ParseOk("m=1");
        Assert.Equal((byte)1, chunk.More);
        Assert.Equal(KittyAction.Transmit, chunk.Action);
        Assert.False(chunk.Has('i'));
    }

    [Theory]
    [InlineData('t')] [InlineData('T')] [InlineData('q')] [InlineData('p')]
    [InlineData('d')] [InlineData('f')] [InlineData('a')] [InlineData('c')]
    public void AllActionLetters_MapToEnum(char a)
    {
        var c = ParseOk("a=" + a);
        Assert.Equal((KittyAction)a, c.Action);
    }

    [Fact]
    public void UnknownActionLetter_IsPreservedForController()
    {
        var c = ParseOk("a=z");
        Assert.Equal((byte)'z', (byte)c.Action);
        Assert.False(System.Enum.IsDefined(c.Action));
    }

    [Theory]
    [InlineData("i", 'i')] [InlineData("I", 'I')] [InlineData("p", 'p')] [InlineData("S", 'S')]
    [InlineData("O", 'O')] [InlineData("s", 's')] [InlineData("v", 'v')] [InlineData("x", 'x')]
    [InlineData("y", 'y')] [InlineData("w", 'w')] [InlineData("h", 'h')] [InlineData("X", 'X')]
    [InlineData("Y", 'Y')] [InlineData("c", 'c')] [InlineData("r", 'r')] [InlineData("P", 'P')]
    [InlineData("Q", 'Q')] [InlineData("f", 'f')] [InlineData("N", 'N')]
    public void UnsignedKeys_AcceptUintMax_AndRejectOneMore(string key, char letter)
    {
        var ok = ParseOk(key + "=4294967295");
        Assert.True(ok.Has(letter));

        var (r, bad) = Parse(key + "=4294967296");
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.False(bad.Has(letter));
    }

    [Fact]
    public void UnsignedKeyValues_AtUintMax_AreExact()
    {
        var c = ParseOk("i=4294967295,I=4294967295,p=4294967295,Y=4294967295,S=4294967295");
        Assert.Equal(uint.MaxValue, c.ImageId);
        Assert.Equal(uint.MaxValue, c.ImageNumber);
        Assert.Equal(uint.MaxValue, c.PlacementId);
        Assert.Equal(uint.MaxValue, c.BgColor);
        Assert.Equal(uint.MaxValue, c.DataSize);
    }

    [Fact]
    public void VeryLongDigitRun_IsInvalidNotWrapped()
    {
        var (r, c) = Parse("i=" + new string('9', 40));
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.Equal(0u, c.ImageId);
        // leading zeros are fine regardless of length
        Assert.Equal(7u, ParseOk("i=" + new string('0', 40) + "7").ImageId);
    }

    [Theory]
    [InlineData("z")] [InlineData("H")] [InlineData("V")]
    public void SignedKeys_FullIntRange(string key)
    {
        char l = key[0];
        Assert.True(ParseOk(key + "=2147483647").Has(l));
        Assert.True(ParseOk(key + "=-2147483648").Has(l));
        Assert.True(ParseOk(key + "=-0").Has(l));
        Assert.Equal(KittyParseResult.Invalid, Parse(key + "=2147483648").Result);
        Assert.Equal(KittyParseResult.Invalid, Parse(key + "=-2147483649").Result);
        Assert.Equal(KittyParseResult.Invalid, Parse(key + "=4294967295").Result);
        Assert.Equal(KittyParseResult.Invalid, Parse(key + "=-").Result);
        Assert.Equal(KittyParseResult.Invalid, Parse(key + "=--1").Result);
        Assert.Equal(KittyParseResult.Invalid, Parse(key + "=+1").Result);
    }

    [Fact]
    public void SignedValues_ExtremesAreExact()
    {
        var c = ParseOk("z=-2147483648,H=2147483647,V=-2147483648");
        Assert.Equal(int.MinValue, c.Z);
        Assert.Equal(int.MaxValue, c.ParentH);
        Assert.Equal(int.MinValue, c.ParentV);
        Assert.Equal(int.MinValue, c.Gap);
    }

    [Theory]
    [InlineData("i=-1")]
    [InlineData("i=+1")]
    [InlineData("i=1.5")]
    [InlineData("i=0x10")]
    [InlineData("i=1e3")]
    [InlineData("i=")]
    [InlineData("i")]
    [InlineData("i=a")]
    public void MalformedUnsignedValues_AreInvalid(string control)
    {
        var (r, c) = Parse(control);
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.False(c.Has('i'));
        Assert.Equal(0u, c.ImageId);
    }

    [Theory]
    [InlineData("a=")]
    [InlineData("a=tt")]
    [InlineData("t=dd")]
    [InlineData("o=zz")]
    [InlineData("d=ii")]
    public void CharKeys_RequireExactlyOneCharacter(string control)
    {
        Assert.Equal(KittyParseResult.Invalid, Parse(control).Result);
    }

    [Fact]
    public void InvalidCharKey_KeepsDefault()
    {
        var (_, c) = Parse("a=tt,i=1");
        Assert.Equal(KittyAction.Transmit, c.Action);
        Assert.False(c.Has('a'));
        Assert.Equal(1u, c.ImageId);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(255, 2)]
    [InlineData(4294967295, 2)]
    public void Quiet_ClampsToTwo(long input, int expected)
    {
        var c = ParseOk("q=" + input);
        Assert.Equal((byte)expected, c.Quiet);
        Assert.True(c.Has('q'));
    }

    [Theory]
    [InlineData("m", 0)] [InlineData("m", 1)] [InlineData("m", 255)] [InlineData("m", 256)]
    [InlineData("C", 1)] [InlineData("C", 300)] [InlineData("U", 1)] [InlineData("U", 70000)]
    public void ByteKeys_SaturateAt255(string key, long value)
    {
        var c = ParseOk(key + "=" + value);
        byte expected = (byte)System.Math.Min(value, 255L);
        byte actual = key switch { "m" => c.More, "C" => c.CursorPolicy, _ => c.Unicode };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptySegments_AreTolerated()
    {
        var c = ParseOk(",,a=p,,i=5,,");
        Assert.Equal(KittyAction.Put, c.Action);
        Assert.Equal(5u, c.ImageId);
        Assert.Equal(KittyParseResult.Ok, KittyControlParser.TryParse(Encoding.ASCII.GetBytes(","), out _));
        Assert.Equal(KittyParseResult.Ok, KittyControlParser.TryParse(Encoding.ASCII.GetBytes(",,,,"), out _));
    }

    [Fact]
    public void TrailingComma_DoesNotInventAKey()
    {
        var c = ParseOk("i=5,");
        Assert.Equal(5u, c.ImageId);
        Assert.Equal(1UL << KittyGraphicsCommand.BitIndex('i'), c.Present);
    }

    [Fact]
    public void DuplicateKey_LastWins()
    {
        var c = ParseOk("i=1,i=2,a=T,a=p");
        Assert.Equal(2u, c.ImageId);
        Assert.Equal(KittyAction.Put, c.Action);
    }

    [Fact]
    public void DuplicateKey_InvalidLaterValue_KeepsEarlierValueButReportsInvalid()
    {
        var (r, c) = Parse("i=1,i=zzz");
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.Equal(1u, c.ImageId);
    }

    [Fact]
    public void UnknownKeys_IgnoredAndDoNotSetPresent()
    {
        var c = ParseOk("b=5,i=3,k=abc,A=1,B,zz=1,5=5,L=-9,Z");
        Assert.Equal(3u, c.ImageId);
        Assert.Equal(1UL << KittyGraphicsCommand.BitIndex('i'), c.Present);
        Assert.False(c.Has('b'));
        Assert.False(c.Has('A'));
    }

    [Fact]
    public void MultiLetterKeyDoesNotAliasItsFirstLetter()
    {
        var c = ParseOk("id=5,ab=1");
        Assert.Equal(0u, c.ImageId);
        Assert.Equal(KittyAction.Transmit, c.Action);
        Assert.Equal(0UL, c.Present);
    }

    [Fact]
    public void EqualsInsideValue_IsInvalid()
    {
        var (r, c) = Parse("i==5,p=2");
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.Equal(2u, c.PlacementId);
    }

    [Theory]
    [InlineData("i=1, p=2")]
    [InlineData("i=1 ,p=2")]
    [InlineData(" i=1")]
    [InlineData("i=\t1")]
    [InlineData("i=1\n")]
    [InlineData("i=1\0")]
    [InlineData("i=1\u007f")]
    [InlineData("i=1\u00e9")]
    [InlineData("a=\u00e9")]
    [InlineData("i=\u0001")]
    public void WhitespaceControlAndNonAscii_AreInvalid(string control)
    {
        Assert.Equal(KittyParseResult.Invalid, Parse(control).Result);
    }

    [Fact]
    public void NonPrintableByteInUnknownKey_IsStillInvalid_ButOtherKeysSurvive()
    {
        var (r, c) = Parse("i=9,b=\u0080,p=4");
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.Equal(9u, c.ImageId);
        Assert.Equal(4u, c.PlacementId);
    }

    [Fact]
    public void InvalidValue_DoesNotStopLaterIdsFromBeingRecovered()
    {
        var (r, c) = Parse("s=99999999999,f=100,i=42,I=7,p=3,q=1");
        Assert.Equal(KittyParseResult.Invalid, r);
        Assert.Equal(42u, c.ImageId);
        Assert.Equal(7u, c.ImageNumber);
        Assert.Equal(3u, c.PlacementId);
        Assert.Equal((byte)1, c.Quiet);
        Assert.Equal(100u, c.Format);
        Assert.False(c.Has('s'));
        Assert.Equal(0u, c.Width);
    }

    [Fact]
    public void IdAndNumberTogether_AreNotAParseError_ButFlagged()
    {
        var (r, c) = Parse("i=1,I=2");
        Assert.Equal(KittyParseResult.Ok, r);
        Assert.True(c.HasBothIdAndNumber);
        Assert.False(ParseOk("i=1,I=0").HasBothIdAndNumber);
        Assert.False(ParseOk("i=0,I=2").HasBothIdAndNumber);
        Assert.False(ParseOk("I=2").HasBothIdAndNumber);
    }

    [Fact]
    public void ExplicitZero_IsPresentButEqualsAbsentValue()
    {
        var c = ParseOk("i=0,z=0,x=0");
        Assert.True(c.Has('i'));
        Assert.True(c.Has('z'));
        Assert.True(c.Has('x'));
        Assert.False(c.Has('y'));
        Assert.Equal(0u, c.ImageId);
    }

    [Fact]
    public void PresentBits_AreOnePerLetter_CaseSensitive()
    {
        var c = ParseOk("i=1,I=1,S=1,s=1");
        ulong expected = (1UL << KittyGraphicsCommand.BitIndex('i')) | (1UL << KittyGraphicsCommand.BitIndex('I'))
                       | (1UL << KittyGraphicsCommand.BitIndex('S')) | (1UL << KittyGraphicsCommand.BitIndex('s'));
        Assert.Equal(expected, c.Present);
        Assert.Equal(4, System.Numerics.BitOperations.PopCount(c.Present));
        Assert.Equal(0, KittyGraphicsCommand.BitIndex('A'));
        Assert.Equal(25, KittyGraphicsCommand.BitIndex('Z'));
        Assert.Equal(26, KittyGraphicsCommand.BitIndex('a'));
        Assert.Equal(51, KittyGraphicsCommand.BitIndex('z'));
        Assert.Equal(-1, KittyGraphicsCommand.BitIndex('1'));
    }

    [Fact]
    public void Has_NonLetter_IsFalse()
    {
        var c = ParseOk("i=1");
        Assert.False(c.Has('1'));
        Assert.False(c.Has('\0'));
        Assert.False(c.Has('é'));
    }

    [Fact]
    public void LongControl_UpTo4096Bytes_Parses()
    {
        var sb = new StringBuilder("i=5");
        while (sb.Length < 4090)
            sb.Append(",b=1");
        sb.Append(",p=6");
        Assert.True(sb.Length <= 4096);
        var c = ParseOk(sb.ToString());
        Assert.Equal(5u, c.ImageId);
        Assert.Equal(6u, c.PlacementId);
    }

    [Fact]
    public void DefaultCommand_IsHarmless()
    {
        var c = default(KittyGraphicsCommand);
        Assert.False(c.Has('a'));
        Assert.Equal(0UL, c.Present);
    }

    [Fact]
    public void TryParse_DoesNotAllocate()
    {
        var bytes = Encoding.ASCII.GetBytes("a=T,f=100,i=31,s=1,v=1,z=-5,c=10,r=5,q=2,b=1,,");
        AllocationAssert.NoAllocations(() =>
        {
            KittyControlParser.TryParse(bytes, out var cmd);
            if (cmd.ImageId != 31) throw new System.InvalidOperationException();
        });
        var bad = Encoding.ASCII.GetBytes("i=1,s=99999999999");
        AllocationAssert.NoAllocations(() => KittyControlParser.TryParse(bad, out _));
    }
}
