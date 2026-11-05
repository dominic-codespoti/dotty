using System;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// Single-pass, allocation-free parser of kitty graphics control data (<c>key=value,key=value</c>, the bytes
/// between <c>ESC _ G</c> and the first <c>;</c>). The overall length cap is enforced by the APC layer; this
/// parser accepts any length.
/// </summary>
/// <remarks>
/// Rules: empty segments are skipped; unknown or multi-letter keys are ignored; a duplicate key keeps the last
/// value; every byte of the control data must be printable ASCII (0x21..0x7E) else the result is
/// <see cref="KittyParseResult.Invalid"/>. A known key with a missing/empty/overflowing/malformed value is
/// skipped (not recorded in <see cref="KittyGraphicsCommand.Present"/>) and makes the result Invalid, but parsing
/// continues so that ids that appear later in the block are still available for an error reply.
/// Unsigned keys accept digits only up to <see cref="uint.MaxValue"/>; signed keys (<c>z H V</c>) accept an
/// optional leading '-' and must fit in <see cref="int"/>. Char keys (<c>a t o d e</c>) take exactly one character.
/// </remarks>
internal static class KittyControlParser
{
    public static KittyParseResult TryParse(ReadOnlySpan<byte> control, out KittyGraphicsCommand cmd)
    {
        var raw = KittyGraphicsCommand.Raw.CreateDefault();
        bool ok = true;

        int pos = 0;
        while (pos <= control.Length)
        {
            int end = control.Slice(pos).IndexOf((byte)',');
            end = end < 0 ? control.Length : pos + end;
            var seg = control.Slice(pos, end - pos);
            pos = end + 1;
            if (seg.Length == 0)
                continue;

            bool printable = true;
            for (int i = 0; i < seg.Length; i++)
            {
                if (seg[i] < 0x21 || seg[i] > 0x7E) { printable = false; break; }
            }
            if (!printable)
            {
                ok = false;
                continue;
            }

            char key = (char)seg[0];
            if (seg.Length > 1 && seg[1] != (byte)'=')
                continue; // multi-letter key: unknown
            if (!IsKnownKey(key))
                continue;

            var value = seg.Length > 2 ? seg.Slice(2) : default;
            if (seg.Length < 2 || !Assign(ref raw, key, value))
                ok = false;
        }

        cmd = new KittyGraphicsCommand(in raw);
        return ok ? KittyParseResult.Ok : KittyParseResult.Invalid;
    }

    private static bool IsKnownKey(char k) => k switch
    {
        'a' or 'q' or 'f' or 't' or 'o' or 'm' or 'i' or 'I' or 'p' or 'S' or 'O' or 's' or 'v'
        or 'x' or 'y' or 'w' or 'h' or 'X' or 'Y' or 'c' or 'r' or 'z' or 'C' or 'U' or 'P' or 'Q'
        or 'H' or 'V' or 'd' or 'N' or 'e' => true,
        _ => false,
    };

    private static bool Assign(ref KittyGraphicsCommand.Raw r, char key, ReadOnlySpan<byte> v)
    {
        uint u;
        int s;
        byte b;
        switch (key)
        {
            case 'a': if (!Char(v, out b)) return false; r.A = b; break;
            case 't': if (!Char(v, out b)) return false; r.T = b; break;
            case 'o': if (!Char(v, out b)) return false; r.O = b; break;
            case 'd': if (!Char(v, out b)) return false; r.D = b; break;
            case 'e': if (!Char(v, out b)) return false; r.E = b; break;

            case 'q': if (!Unsigned(v, out u)) return false; r.Q = (byte)Math.Min(u, 2u); break;
            case 'm': if (!Unsigned(v, out u)) return false; r.M = Saturate(u); break;
            case 'C': if (!Unsigned(v, out u)) return false; r.C = Saturate(u); break;
            case 'U': if (!Unsigned(v, out u)) return false; r.U = Saturate(u); break;

            case 'f': if (!Unsigned(v, out u)) return false; r.F = u; break;
            case 's': if (!Unsigned(v, out u)) return false; r.S = u; break;
            case 'v': if (!Unsigned(v, out u)) return false; r.V = u; break;
            case 'S': if (!Unsigned(v, out u)) return false; r.BigS = u; break;
            case 'O': if (!Unsigned(v, out u)) return false; r.BigO = u; break;
            case 'i': if (!Unsigned(v, out u)) return false; r.I = u; break;
            case 'I': if (!Unsigned(v, out u)) return false; r.BigI = u; break;
            case 'p': if (!Unsigned(v, out u)) return false; r.P = u; break;
            case 'x': if (!Unsigned(v, out u)) return false; r.X = u; break;
            case 'y': if (!Unsigned(v, out u)) return false; r.Y = u; break;
            case 'w': if (!Unsigned(v, out u)) return false; r.W = u; break;
            case 'h': if (!Unsigned(v, out u)) return false; r.H = u; break;
            case 'X': if (!Unsigned(v, out u)) return false; r.BigX = u; break;
            case 'Y': if (!Unsigned(v, out u)) return false; r.BigY = u; break;
            case 'c': if (!Unsigned(v, out u)) return false; r.Cc = u; break;
            case 'r': if (!Unsigned(v, out u)) return false; r.R = u; break;
            case 'P': if (!Unsigned(v, out u)) return false; r.BigP = u; break;
            case 'Q': if (!Unsigned(v, out u)) return false; r.BigQ = u; break;
            case 'N': if (!Unsigned(v, out u)) return false; r.N = u; break;

            case 'z': if (!Signed(v, out s)) return false; r.Z = s; break;
            case 'H': if (!Signed(v, out s)) return false; r.BigH = s; break;
            case 'V': if (!Signed(v, out s)) return false; r.BigV = s; break;
            default: return true;
        }
        int bit = KittyGraphicsCommand.BitIndex(key);
        r.Present |= 1UL << bit;
        return true;
    }

    private static byte Saturate(uint u) => u > byte.MaxValue ? byte.MaxValue : (byte)u;

    private static bool Char(ReadOnlySpan<byte> v, out byte c)
    {
        c = 0;
        if (v.Length != 1)
            return false;
        c = v[0];
        return true;
    }

    private static bool Unsigned(ReadOnlySpan<byte> v, out uint result)
    {
        result = 0;
        if (v.Length == 0)
            return false;
        ulong acc = 0;
        for (int i = 0; i < v.Length; i++)
        {
            uint d = (uint)(v[i] - (byte)'0');
            if (d > 9)
                return false;
            acc = acc * 10 + d;
            if (acc > uint.MaxValue)
                return false;
        }
        result = (uint)acc;
        return true;
    }

    private static bool Signed(ReadOnlySpan<byte> v, out int result)
    {
        result = 0;
        bool neg = v.Length > 0 && v[0] == (byte)'-';
        var digits = neg ? v.Slice(1) : v;
        if (digits.Length == 0)
            return false;
        long acc = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            uint d = (uint)(digits[i] - (byte)'0');
            if (d > 9)
                return false;
            acc = acc * 10 + d;
            if (acc > 2147483648L)
                return false;
        }
        if (neg)
            result = (int)-acc;
        else
        {
            if (acc > int.MaxValue)
                return false;
            result = (int)acc;
        }
        return true;
    }
}
