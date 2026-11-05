using System;

namespace Dotty.Terminal.Graphics;

/// <summary>Kitty graphics action (key <c>a</c>). Unknown letters are preserved by cast and must be rejected by the controller.</summary>
internal enum KittyAction : byte
{
    Transmit = (byte)'t',
    TransmitAndPut = (byte)'T',
    Query = (byte)'q',
    Put = (byte)'p',
    Delete = (byte)'d',
    Frame = (byte)'f',
    Animate = (byte)'a',
    Compose = (byte)'c',
}

internal enum KittyParseResult
{
    Ok,
    /// <summary>
    /// At least one key had an unusable value (overflow, empty, non-ASCII/control byte, malformed char key).
    /// The out command still carries every key that did parse, in particular <c>i</c>/<c>I</c>/<c>p</c>/<c>q</c>,
    /// so the caller can answer <c>EINVAL:Invalid key value</c> when an id is known.
    /// </summary>
    Invalid,
}

/// <summary>
/// Neutral key bag for one kitty graphics control block. Keys are overloaded per action
/// (<c>c r s v x y z X Y C</c>); every key is parsed into its field regardless of action and each
/// handler reads the field under the name matching its action. Alias names read the same storage:
/// <list type="bullet">
/// <item>Placement (<c>a=p/T</c>): <see cref="SrcX"/>=x, <see cref="SrcY"/>=y, <see cref="SrcW"/>=w, <see cref="SrcH"/>=h, <see cref="CellXOff"/>=X, <see cref="CellYOff"/>=Y, <see cref="Cols"/>=c, <see cref="Rows"/>=r, <see cref="Z"/>=z, <see cref="CursorPolicy"/>=C, <see cref="Unicode"/>=U, <see cref="ParentImage"/>=P, <see cref="ParentPlacement"/>=Q, <see cref="ParentH"/>=H, <see cref="ParentV"/>=V.</item>
/// <item>Transmit: <see cref="Width"/>=s, <see cref="Height"/>=v, <see cref="DataSize"/>=S, <see cref="DataOffset"/>=O, <see cref="Format"/>=f, <see cref="Medium"/>=t, <see cref="Compression"/>=o, <see cref="More"/>=m, <see cref="Hints"/>=N.</item>
/// <item>Frame (<c>a=f</c>): x,y,s,v are the data rectangle (<see cref="SrcX"/>, <see cref="SrcY"/>, <see cref="Width"/>, <see cref="Height"/>); <see cref="OtherFrame"/>=c (base frame); <see cref="FrameNumber"/>=r (frame to edit); <see cref="Gap"/>=z; <see cref="Blend"/>=X (1 = overwrite); <see cref="BgColor"/>=Y (RGBA).</item>
/// <item>Animate (<c>a=a</c>): <see cref="AnimState"/>=s, <see cref="Loops"/>=v, <see cref="FrameNumber"/>=r, <see cref="OtherFrame"/>=c (current frame), <see cref="Gap"/>=z.</item>
/// <item>Compose (<c>a=c</c>): <see cref="FrameNumber"/>=r (source frame), <see cref="OtherFrame"/>=c (destination frame), destination offset x,y (<see cref="SrcX"/>, <see cref="SrcY"/>), source rect left/top X,Y (<see cref="CellXOff"/>, <see cref="CellYOff"/>), size w,h (<see cref="SrcW"/>, <see cref="SrcH"/>), <see cref="CursorPolicy"/>=C (1 = overwrite).</item>
/// <item>Delete (<c>a=d</c>): <see cref="DeleteWhat"/>=d; coordinates x,y,z as <see cref="SrcX"/>, <see cref="SrcY"/>, <see cref="Z"/>; <see cref="Cols"/>/<see cref="Rows"/>=c/r.</item>
/// </list>
/// Defaults when absent: a='t', d='a', f=32, t='d', everything else 0. <see cref="Present"/> has one bit per
/// key letter (see <see cref="Has"/>) so handlers can distinguish absent from 0.
/// </summary>
internal readonly struct KittyGraphicsCommand
{
    /// <summary>Mutable storage filled by <see cref="KittyControlParser"/>.</summary>
    internal struct Raw
    {
        public byte A, Q, M, C, U, D, E, T, O;
        public uint F, S, V, BigS, BigO, I, BigI, P, X, Y, W, H, BigX, BigY, Cc, R, BigP, BigQ, N;
        public int Z, BigH, BigV;
        public ulong Present;

        public static Raw CreateDefault() => new Raw
        {
            A = (byte)'t',
            D = (byte)'a',
            F = 32,
            T = (byte)'d',
        };
    }

    private readonly Raw _r;

    internal KittyGraphicsCommand(in Raw raw) => _r = raw;

    /// <summary>Bit index of a key letter inside <see cref="Present"/>: 'A'..'Z' → 0..25, 'a'..'z' → 26..51; -1 otherwise.</summary>
    internal static int BitIndex(char key) =>
        key >= 'A' && key <= 'Z' ? key - 'A' :
        key >= 'a' && key <= 'z' ? 26 + (key - 'a') : -1;

    public ulong Present => _r.Present;

    /// <summary>True when <paramref name="key"/> (ASCII letter) was given with a valid value.</summary>
    public bool Has(char key)
    {
        int b = BitIndex(key);
        return b >= 0 && (_r.Present & (1UL << b)) != 0;
    }

    /// <summary>Both <c>i</c> and <c>I</c> non-zero (the controller answers EINVAL).</summary>
    public bool HasBothIdAndNumber => _r.I != 0 && _r.BigI != 0;

    public KittyAction Action => (KittyAction)_r.A;
    /// <summary>q, clamped to 0..2.</summary>
    public byte Quiet => _r.Q;
    /// <summary>f (default 32).</summary>
    public uint Format => _r.F;
    /// <summary>s as pixel width for transmit and frame data.</summary>
    public uint Width => _r.S;
    /// <summary>v as pixel height for transmit and frame data.</summary>
    public uint Height => _r.V;
    /// <summary>S.</summary>
    public uint DataSize => _r.BigS;
    /// <summary>O.</summary>
    public uint DataOffset => _r.BigO;
    /// <summary>t (default 'd').</summary>
    public char Medium => (char)_r.T;
    /// <summary>o ('\0' when absent).</summary>
    public char Compression => (char)_r.O;
    /// <summary>m, saturated to 255.</summary>
    public byte More => _r.M;
    /// <summary>i.</summary>
    public uint ImageId => _r.I;
    /// <summary>I.</summary>
    public uint ImageNumber => _r.BigI;
    /// <summary>p.</summary>
    public uint PlacementId => _r.P;
    /// <summary>N (usage hint, e.g. 1 = transient).</summary>
    public uint Hints => _r.N;
    /// <summary>x.</summary>
    public uint SrcX => _r.X;
    /// <summary>y.</summary>
    public uint SrcY => _r.Y;
    /// <summary>w.</summary>
    public uint SrcW => _r.W;
    /// <summary>h.</summary>
    public uint SrcH => _r.H;
    /// <summary>X as unsigned pixel offset.</summary>
    public uint CellXOff => _r.BigX;
    /// <summary>Y as unsigned pixel offset.</summary>
    public uint CellYOff => _r.BigY;
    /// <summary>c as column count.</summary>
    public uint Cols => _r.Cc;
    /// <summary>r as row count.</summary>
    public uint Rows => _r.R;
    /// <summary>C, saturated to 255.</summary>
    public byte CursorPolicy => _r.C;
    /// <summary>U, saturated to 255.</summary>
    public byte Unicode => _r.U;
    /// <summary>z (signed).</summary>
    public int Z => _r.Z;
    /// <summary>P.</summary>
    public uint ParentImage => _r.BigP;
    /// <summary>Q.</summary>
    public uint ParentPlacement => _r.BigQ;
    /// <summary>H (signed).</summary>
    public int ParentH => _r.BigH;
    /// <summary>V (signed).</summary>
    public int ParentV => _r.BigV;
    /// <summary>d (default 'a').</summary>
    public char DeleteWhat => (char)_r.D;
    /// <summary>r read as frame number.</summary>
    public uint FrameNumber => _r.R;
    /// <summary>c read as other/base/destination frame number.</summary>
    public uint OtherFrame => _r.Cc;
    /// <summary>s read as animation state.</summary>
    public uint AnimState => _r.S;
    /// <summary>v read as loop count.</summary>
    public uint Loops => _r.V;
    /// <summary>Y read as RGBA background colour.</summary>
    public uint BgColor => _r.BigY;
    /// <summary>z read as frame gap in ms (negative = gapless).</summary>
    public int Gap => _r.Z;
    /// <summary>X saturated to 255 (a=f: 1 = overwrite instead of alpha-blend).</summary>
    public byte Blend => (byte)Math.Min(_r.BigX, 255u);
    /// <summary>e ('\0' when absent).</summary>
    public char FeatureAction => (char)_r.E;
}
