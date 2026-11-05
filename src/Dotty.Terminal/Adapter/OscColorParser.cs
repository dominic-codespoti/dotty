// Xcms conversions and fallback profile adapted from libX11 src/xcms (LRGB.c,
// Lab.c, Luv.c, xyY.c, uvY.c, HVC.c, HVCMxC.c, HVCGcC.c, cmsMath.c, cmsTrig.c).
/*
 * Code and supporting documentation (c) Copyright 1990 1991 Tektronix, Inc.
 * All Rights Reserved.
 *
 * This file is based on an X Window System-specific implementation of Xcms
 * based on the TekColor Color Management System. Permission is hereby granted
 * to use, copy, modify, sell, and otherwise distribute this software and its
 * documentation for any purpose and without fee, provided that this copyright,
 * permission, and disclaimer notice is reproduced in all copies of this
 * software and in supporting documentation. TekColor is a trademark of
 * Tektronix, Inc.
 *
 * Tektronix makes no representation about the suitability of this software
 * for any purpose. It is provided "as is" and with all faults.
 *
 * TEKTRONIX DISCLAIMS ALL WARRANTIES APPLICABLE TO THIS SOFTWARE,
 * INCLUDING THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
 * PARTICULAR PURPOSE. IN NO EVENT SHALL TEKTRONIX BE LIABLE FOR ANY
 * SPECIAL, INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER
 * RESULTING FROM LOSS OF USE, DATA, OR PROFITS, WHETHER IN AN ACTION OF
 * CONTRACT, NEGLIGENCE, OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
 * CONNECTION WITH THE USE OR THE PERFORMANCE OF THIS SOFTWARE.
 *
 * Tektronix Color Management System permission conditions for TekHVC:
 * 1. This copyright, permission, and disclaimer notice is reproduced in all
 *    copies of this software and any modification thereof and in supporting
 *    documentation;
 * 2. Any color-handling application which displays TekHVC color cooordinates
 *    identifies these as TekHVC color coordinates in any interface that
 *    displays these coordinates and in any associated documentation;
 * 3. The term "TekHVC" is always used, and is only used, in association with
 *    the mathematical derivations of the TekHVC Color Space, including those
 *    provided in this file and any equivalent pathways and mathematical
 *    derivations, regardless of digital (e.g., floating point or integer)
 *    representation.
 */

/*
 * Copyright 1990, 1998 The Open Group
 *
 * Permission to use, copy, modify, distribute, and sell this software and its
 * documentation for any purpose is hereby granted without fee, provided that
 * the above copyright notice appear in all copies and that both that
 * copyright notice and this permission notice appear in supporting
 * documentation.
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
 * THE OPEN GROUP BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF
 * OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 *
 * Except as contained in this notice, the name of The Open Group shall not be
 * used in advertising or otherwise to promote the sale, use or other dealings
 * in this Software without prior written authorization from The Open Group.
 */
using System.Globalization;

namespace Dotty.Terminal.Adapter;

internal static class OscColorParser
{
    private const double Epsilon = 0.001;
    private const double ChromaScale = 7.50725;
    private const double WhiteX = 0.9496643999999999;
    private const double WhiteY = 1.0;
    private const double WhiteZ = 0.02133944350088028 + 0.14297193020246480 + 1.24172892629665500;
    private static readonly double WhiteU = 4 * WhiteX / (WhiteX + 15 * WhiteY + 3 * WhiteZ);
    private static readonly double WhiteV = 9 * WhiteY / (WhiteX + 15 * WhiteY + 3 * WhiteZ);

    private static ReadOnlySpan<double> RedResponse =>
    [
        0,.000000,.000936,.001481,.002329,.003529,.005127,.007169,.009699,.012759,.016392,.020637,.025533,.031119,.037431,.044504,.052373,.061069,.070624,.081070,.092433,.104744,.118026,.132307,.147610,.163958,.181371,.199871,.219475,.240202,.262069,.285089,.309278,.334647,.361208,.388971,.417945,.448138,.479555,.512202,.546082,.581199,.617552,.655144,.693971,.734031,.775322,.817837,.861571,.906515,.952662,1
    ];

    private static ReadOnlySpan<double> GreenResponse =>
    [
        0,0,.000832,.001998,.003612,.005736,.008428,.011745,.015740,.020463,.025960,.032275,.039449,.047519,.056520,.066484,.077439,.089409,.102418,.116485,.131625,.147853,.165176,.183604,.203140,.223783,.245533,.268384,.292327,.317351,.343441,.370580,.398747,.427919,.458068,.489165,.521176,.554067,.587797,.622324,.657604,.693588,.730225,.767459,.805235,.843491,.882164,.921187,.960490,1
    ];

    private static ReadOnlySpan<double> BlueResponse =>
    [
        0,0,.001341,.002080,.003188,.004729,.006766,.009357,.012559,.016424,.021004,.026344,.032489,.039481,.047357,.056154,.065903,.076634,.088373,.101145,.114968,.129862,.145841,.162915,.181095,.200386,.220791,.242309,.264937,.288670,.313499,.339410,.366390,.394421,.423481,.453547,.484592,.516587,.549498,.583291,.617925,.653361,.689553,.726454,.764013,.802178,.840891,.880093,.919723,.959715,1
    ];

    public static bool TryParse(ReadOnlySpan<char> spec, out uint argb)
    {
        argb = 0;
        if (spec.IsEmpty) return false;
        if (spec[0] == '#') return TryHex(spec[1..], out argb);
        int colon = spec.IndexOf(':');
        if (colon <= 0) return false;
        ReadOnlySpan<char> format = spec[..colon];
        ReadOnlySpan<char> body = spec[(colon + 1)..];
        if (format.Equals("rgb", StringComparison.OrdinalIgnoreCase)) return TryRgb(body, out argb);
        if (!TryTriple(body, out double a, out double b, out double c)) return false;
        double x, y, z;
        if (format.SequenceEqual("rgbi"))
        {
            if (!Unit(a) || !Unit(b) || !Unit(c)) return false;
            x = a; y = b; z = c;
            argb = 0xff000000u | (uint)Lookup(0, a) << 16 | (uint)Lookup(1, b) << 8 | Lookup(2, c);
            return true;
        }
        else if (format.SequenceEqual("CIEXYZ"))
        {
            if (!Unit(b)) return false;
            (x, y, z) = (a, b, c);
        }
        else if (format.SequenceEqual("CIExyY"))
        {
            if (!Unit(a) || !Unit(b) || !Unit(c)) return false;
            double xyDiv = -2 * a + 12 * b + 3;
            if (xyDiv == 0) { x = y = z = 0; }
            else
            {
                double u = 4 * a / xyDiv, v = 9 * b / xyDiv;
                double uvDiv = 6 * u - 16 * v + 12;
                if (uvDiv == 0) (x, y, z) = (WhiteX * c, c, WhiteZ * c);
                else
                {
                    double smallX = 9 * u / uvDiv, smallY = 4 * v / uvDiv;
                    double smallZ = 1 - smallX - smallY;
                    if (smallY == 0) smallY = 0.00001;
                    x = smallX * c / smallY;
                    y = c;
                    z = smallZ * c / smallY;
                }
            }
        }
        else if (format.SequenceEqual("CIEuvY"))
        {
            if (!Unit(c)) return false;
            double div = 6 * a - 16 * b + 12;
            if (div == 0) (x, y, z) = (WhiteX * c, c, WhiteZ * c);
            else
            {
                double smallX = 9 * a / div, smallY = 4 * b / div;
                double smallZ = 1 - smallX - smallY;
                if (smallY == 0) (x, y, z) = (smallX, c, smallZ);
                else (x, y, z) = (smallX * c / smallY, c, smallZ * c / smallY);
            }
        }
        else if (format.SequenceEqual("CIELab"))
        {
            if (a < 0 || a > 100 || !double.IsFinite(b) || !double.IsFinite(c)) return false;
            double t = (a + 16) / 116;
            if (t * t * t < 0.008856)
            {
                double low = a / 9.03292;
                x = WhiteX * (low + b / 3893.5);
                y = low;
                z = WhiteZ * (low - c / 1557.4);
            }
            else
            {
                double tX = t + b / 5.0;
                double tZ = t - c / 2.0;
                x = WhiteX * tX * tX * tX;
                y = t * t * t;
                z = WhiteZ * tZ * tZ * tZ;
            }
        }
        else if (format.SequenceEqual("CIELuv"))
        {
            if (a < 0 || a > 100 || !double.IsFinite(b) || !double.IsFinite(c)) return false;
            double t = (a + 16) / 116.0;
            double yy = a < 7.99953624 ? a / 903.29 : t * t * t;
            double un = WhiteU, vn = WhiteV;
            double uvScale = 13.0 * (a / 100.0);
            double up = a == 0 ? un : b / uvScale + un;
            double vp = a == 0 ? vn : c / uvScale + vn;
            double div = 6 * up - 16 * vp + 12;
            if (div == 0) (x, y, z) = (WhiteX * yy, yy, WhiteZ * yy);
            else
            {
                double smallX = 9 * up / div, smallY = 4 * vp / div;
                if (smallY == 0) (x, y, z) = (smallX, yy, 1 - smallX - smallY);
                else (x, y, z) = (smallX * yy / smallY, yy, (1 - smallX - smallY) * yy / smallY);
            }
        }
        else if (format.SequenceEqual("TekHVC"))
        {
            if (b < 0 || b > 100 || c < 0) return false;
            a %= 360;
            if (a < 0) a += 360;
            (x, y, z) = TekHvcToXyz(a, b, c);
        }
        else return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return false;
        if (!ToDevice(x, y, z, out byte r, out byte g, out byte bl)) return false;
        argb = 0xff000000u | (uint)r << 16 | (uint)g << 8 | bl;
        return true;
    }

    private static bool TryHex(ReadOnlySpan<char> s, out uint argb)
    {
        argb = 0;
        if (s.Length is not (3 or 6 or 9 or 12)) return false;
        int n = s.Length / 3, shift = 16 - n * 4;
        uint rgb = 0;
        for (int i = 0; i < 3; i++)
        {
            int v = 0;
            for (int j = 0; j < n; j++)
            {
                int h = Hex(s[i * n + j]);
                if (h < 0) return false;
                v = (v << 4) | h;
            }
            rgb = (rgb << 8) | (uint)(v << shift >> 8);
        }
        argb = 0xff000000u | rgb;
        return true;
    }

    private static bool TryRgb(ReadOnlySpan<char> s, out uint argb)
    {
        argb = 0;
        int first = s.IndexOf('/');
        if (first <= 0) return false;
        int secondRel = s[(first + 1)..].IndexOf('/');
        if (secondRel <= 0) return false;
        int second = first + 1 + secondRel;
        ReadOnlySpan<char> r = s[..first], g = s[(first + 1)..second], b = s[(second + 1)..];
        if (b.IsEmpty || b.IndexOf('/') >= 0 || r.Length > 4 || g.Length > 4 || b.Length > 4) return false;
        if (!RgbPart(r, out byte rr) || !RgbPart(g, out byte gg) || !RgbPart(b, out byte bb)) return false;
        argb = 0xff000000u | (uint)rr << 16 | (uint)gg << 8 | bb;
        return true;
    }

    private static bool RgbPart(ReadOnlySpan<char> s, out byte value)
    {
        value = 0;
        if (s.IsEmpty || s.Length > 4) return false;
        int n = 0;
        foreach (char ch in s) { int d = Hex(ch); if (d < 0) return false; n = (n << 4) | d; }
        int max = (1 << (s.Length * 4)) - 1;
        value = (byte)((n * 255) / max);
        return true;
    }

    private static bool TryTriple(ReadOnlySpan<char> s, out double a, out double b, out double c)
    {
        a = b = c = 0;
        int i = s.IndexOf('/'); if (i < 1) return false;
        int jrel = s[(i + 1)..].IndexOf('/'); if (jrel < 1) return false;
        int j = i + 1 + jrel;
        return Number(s[..i], out a) && Number(s[(i + 1)..j], out b) && Number(s[(j + 1)..], out c);
    }

    private static bool Number(ReadOnlySpan<char> text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static bool Unit(double v) => double.IsFinite(v) && v >= 0 && v <= 1;
    private static int Hex(char ch) => ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;

    private static bool ToDevice(double x, double y, double z, out byte r, out byte g, out byte b)
    {
        double ri = 3.48340481253539000 * x - 1.52176374927285200 * y - .55923133354049780 * z;
        double gi = -1.07152751306193600 * x + 1.96593795204372400 * y + .03673691339553462 * z;
        double bi = .06351179790497788 * x - .20020501000496480 * y + .81070942031648220 * z;
        if (Math.Min(ri, Math.Min(gi, bi)) < -Epsilon || Math.Max(ri, Math.Max(gi, bi)) > 1 + Epsilon)
        {
            // XcmsTekHVCClipC maps zero-lightness colors to screen black.
            if (y == 0)
            {
                r = g = b = 0;
                return true;
            }
            if (!Compress(ref x, ref y, ref z)) { r = g = b = 0; return false; }
            XyzToRgbIntensity(x, y, z, out ri, out gi, out bi);
            if (Math.Min(ri, Math.Min(gi, bi)) < -Epsilon ||
                Math.Max(ri, Math.Max(gi, bi)) > 1 + Epsilon)
            {
                r = g = b = 0;
                return false;
            }
        }
        if (!double.IsFinite(ri) || !double.IsFinite(gi) || !double.IsFinite(bi)) { r = g = b = 0; return false; }
        r = Lookup(0, Math.Clamp(ri, 0, 1)); g = Lookup(1, Math.Clamp(gi, 0, 1)); b = Lookup(2, Math.Clamp(bi, 0, 1));
        return true;
    }

    private static bool Compress(ref double x, ref double y, ref double z)
    {
        if (!XyzToHvc(x, y, z, out double hue, out double value, out _)) return false;
        if (!QueryMaxChroma(hue, value, out double chroma, out double compressedValue)) return false;
        (x, y, z) = TekHvcToXyz(hue, compressedValue, chroma);
        return true;
    }

    private static bool QueryMaxChroma(double hue, double value, out double chroma, out double outputValue)
    {
        chroma = 0;
        outputValue = value;
        if (!QueryMaxValueChroma(hue, out double maxValue, out double maxChroma,
                out double savedRed, out double savedGreen, out double savedBlue))
            return false;

        if (value <= maxValue)
        {
            chroma = maxValue == 0 ? 0 : value * maxChroma / maxValue;
            return true;
        }

        double nValue = value, savedValue = value, lastValue = -1, lastChroma = -1;
        double previousValue, maxDistance = 100 - maxValue, factor = 1;
        double currentHue = hue, currentValue = value, currentChroma = 100;
        for (int count = 0; count < 100; count++)
        {
            previousValue = lastValue;
            lastValue = currentValue;
            lastChroma = currentChroma;
            double t = (nValue - maxValue) / maxDistance * factor;
            double red = savedRed * (1 - t) + t;
            double green = savedGreen * (1 - t) + t;
            double blue = savedBlue * (1 - t) + t;
            RgbIntensityToXyz(red, green, blue, out double xyzX, out double xyzY, out double xyzZ);
            if (!XyzToHvc(xyzX, xyzY, xyzZ, out currentHue, out currentValue, out currentChroma))
                return false;
            if (currentValue <= savedValue + Epsilon && currentValue >= savedValue - Epsilon)
            {
                chroma = currentChroma;
                outputValue = currentValue;
                return true;
            }

            nValue += savedValue - currentValue;
            if (nValue < maxValue)
            {
                nValue = maxValue;
                factor *= 0.5;
            }
            else if (nValue > 100)
            {
                if (Math.Abs(lastValue - savedValue) < Math.Abs(currentValue - savedValue))
                {
                    outputValue = lastValue;
                    chroma = lastChroma;
                }
                else
                {
                    outputValue = currentValue;
                    chroma = currentChroma;
                }
                return true;
            }
            else if (currentValue <= previousValue + Epsilon && currentValue >= previousValue - Epsilon)
                factor *= 0.5;
        }
        if (Math.Abs(lastValue - savedValue) < Math.Abs(currentValue - savedValue))
        {
            outputValue = lastValue;
            chroma = lastChroma;
        }
        else
        {
            outputValue = currentValue;
            chroma = currentChroma;
        }
        return true;
    }

    private static bool QueryMaxValueChroma(double hue, out double value, out double chroma,
        out double savedRed, out double savedGreen, out double savedBlue)
    {
        (double x, double y, double z) = TekHvcToXyz(hue, 40, 120);
        XyzToRgbIntensity(x, y, z, out double red, out double green, out double blue);
        double smallest = Math.Min(red, Math.Min(green, blue));
        red -= smallest; green -= smallest; blue -= smallest;
        double largest = Math.Max(red, Math.Max(green, blue));
        if (largest == 0 || !double.IsFinite(largest))
        {
            value = chroma = savedRed = savedGreen = savedBlue = 0;
            return false;
        }
        savedRed = red / largest;
        savedGreen = green / largest;
        savedBlue = blue / largest;
        RgbIntensityToXyz(savedRed, savedGreen, savedBlue, out x, out y, out z);
        return XyzToHvc(x, y, z, out _, out value, out chroma);
    }

    private static bool XyzToHvc(double x, double y, double z,
        out double hue, out double value, out double chroma)
    {
        double denominator = x + 15 * y + 3 * z;
        double up, vp;
        if (denominator == 0) { up = WhiteU; vp = WhiteV; }
        else { up = 4 * x / denominator; vp = 9 * y / denominator; }
        double du = up - WhiteU, dv = vp - WhiteV;
        double thetaOffset = XcmsAtan((0.4931 - WhiteV) / (0.7127 - WhiteU)) * 180 / Math.PI;
        double theta;
        if (du == 0) theta = 0;
        else
        {
            theta = XcmsAtan(dv / du) * 180 / Math.PI;
            double low = 0, high = 360;
            if (du < 0 && dv > 0) { low = 90; high = 180; }
            else if (du < 0 && dv < 0) { low = 180; high = 270; }
            else if (du > 0 && dv < 0) { low = 270; high = 360; }
            while (theta < low) theta += 90;
            while (theta >= high) theta -= 90;
        }
        value = y < 0.008856 ? y * 903.29 : XcmsCubeRoot(y) * 116 - 16;
        chroma = value * ChromaScale * XcmsSquareRoot(du * du + dv * dv);
        hue = theta - thetaOffset;
        while (hue < 0) hue += 360;
        while (hue >= 360) hue -= 360;
        return double.IsFinite(hue) && double.IsFinite(value) && double.IsFinite(chroma);
    }

    private static void XyzToRgbIntensity(double x, double y, double z,
        out double red, out double green, out double blue)
    {
        red = 3.48340481253539000 * x - 1.52176374927285200 * y - .55923133354049780 * z;
        green = -1.07152751306193600 * x + 1.96593795204372400 * y + .03673691339553462 * z;
        blue = .06351179790497788 * x - .20020501000496480 * y + .81070942031648220 * z;
    }

    private static void RgbIntensityToXyz(double red, double green, double blue,
        out double x, out double y, out double z)
    {
        x = .38106149108714790 * red + .32025712365352110 * green + .24834578525933100 * blue;
        y = .20729745115140850 * red + .68054638776373240 * green + .11215616108485920 * blue;
        z = .02133944350088028 * red + .14297193020246480 * green + 1.24172892629665500 * blue;
    }

    private static (double X, double Y, double Z) TekHvcToXyz(double hue, double value, double chroma)
    {
        double t = (value + 16) / 116.0;
        double yy = value < 7.99953624 ? value / 903.29 : t * t * t;
        if (value == 0 || value == 100)
            return (WhiteX * yy, yy, WhiteZ * yy);
        double thetaOffset = XcmsAtan((.4931 - WhiteV) / (.7127 - WhiteU)) * 180.0 / Math.PI;
        double tempHue = hue + thetaOffset;
        while (tempHue < 0) tempHue += 360;
        while (tempHue >= 360) tempHue -= 360;
        double theta = tempHue * Math.PI / 180.0;
        double u = WhiteU + XcmsCosine(theta) * chroma / (value * ChromaScale);
        double v = WhiteV + XcmsSine(theta) * chroma / (value * ChromaScale);
        double div = 6 * u - 16 * v + 12;
        if (div == 0) return (0, yy, 0);
        double smallX = 9 * u / div, smallY = 4 * v / div;
        if (smallY == 0) return (0, yy, 0);
        return (smallX * yy / smallY, yy, (1 - smallX - smallY) * yy / smallY);
    }

    private static byte Lookup(int channel, double intensity)
    {
        ReadOnlySpan<double> table = Response(channel);
        int n = table.Length, hi = 1;
        while (hi < n && intensity > table[hi]) hi++;
        if (intensity <= table[0]) return 0;
        if (hi == n) return 255;

        int lo = hi - 1;
        double ratio = (intensity - table[lo]) / (table[hi] - table[lo]);
        int loValue = ResponseValue(channel, lo);
        int target = (int)((ResponseValue(channel, hi) - loValue) * ratio) + loValue;

        // Xcms rounds to the closest representable value at bits_per_rgb=8.
        int up = ((target >> 8) * 0xffff) / 255;
        int down;
        if (up < target)
        {
            down = up;
            up = (Math.Min((down >> 8) + 1, 255) * 0xffff) / 255;
        }
        else
        {
            down = (Math.Max((up >> 8) - 1, 0) * 0xffff) / 255;
        }
        int selected = (up - target) < (target - down) ? up : down;
        return (byte)((selected & 0xff00) >> 8);
    }

    private static ReadOnlySpan<double> Response(int channel) =>
        channel switch { 0 => RedResponse, 1 => GreenResponse, _ => BlueResponse };

    private static int ResponseValue(int channel, int index)
    {
        int last = channel switch { 0 => 51, 1 => 49, _ => 50 };
        if (index == 0) return 0;
        if (index == last) return 0xffff;
        int zero = channel switch { 0 => 0x0909, 1 => 0x1313, _ => 0x0e0e };
        if (index == 1) return zero;
        int first = channel switch { 0 => 0x0a0a, 1 => 0x1414, _ => 0x0f0f };
        return first + (index - 2) * 0x0505;
    }

    // Xcms uses its portable Newton and Hart-approximation math rather than libm.
    private const double MachineEpsilon = 2.2204460492503131e-16;
    private const double Pi = 3.14159265358979323846264338327950;

    private static double XcmsSquareRoot(double value)
    {
        if (value == 0) return 0;
        if (value < 0) return 0;
        double guess = value > 1 ? value / 4 : value * 4;
        while (true)
        {
            double delta = (guess - value / guess) / 2;
            guess -= delta;
            if (delta < 0) delta = -delta;
            if (delta < guess * MachineEpsilon) return guess;
        }
    }

    private static double XcmsCubeRoot(double value)
    {
        if (value == 0) return 0;
        double abs = value < 0 ? -value : value;
        double guess = abs > 1 ? abs / 8 : abs * 8;
        while (true)
        {
            double delta = (guess - abs / (guess * guess)) / 3;
            guess -= delta;
            if (delta < 0) delta = -delta;
            if (delta < guess * MachineEpsilon) return value < 0 ? -guess : guess;
        }
    }

    private static double XcmsAtan(double value)
    {
        if (value == 0) return 0;
        double maxError = value < 1 ? value * 0.000001 : 0.000001;
        double ai = XcmsSquareRoot(1 / (1 + value * value));
        double bi = 1, a1 = 0, b1 = 0;
        for (int i = 0; i < 10000; i++)
        {
            a1 = (ai + bi) / 2;
            b1 = XcmsSquareRoot(a1 * bi);
            if (a1 == b1) break;
            double d = a1 - b1;
            if (d < 0) d = -d;
            if (d < maxError) break;
            ai = a1;
            bi = b1;
        }
        double low = a1 > b1 ? b1 : a1;
        double root = XcmsSquareRoot(1 + value * value);
        return value / (root * low);
    }

    private static double XcmsCosine(double x)
    {
        if (x < -Pi || x > Pi)
        {
            x = XcmsModulo(x, 2 * Pi);
            if (x > Pi) x -= 2 * Pi;
            else if (x < -Pi) x += 2 * Pi;
        }
        if (x > Pi / 2) return -XcmsCosine(x - Pi);
        if (x < -Pi / 2) return -XcmsCosine(x + Pi);
        if (x > Pi / 4) return XcmsSine(Pi / 2 - x);
        if (x < -Pi / 4) return XcmsSine(Pi / 2 + x);
        if (x < 4.209340e-52 && x > -4.209340e-52)
            return XcmsSquareRoot(1 - x * x);
        double y = x / (Pi / 4), y2 = y * y;
        double p = 1290539.4659037374438 + y2 * (-374567.03915723204710 + y2 * (13432.300986539084285 + y2 * -112.31450823340933092));
        double q = 1290539.4659037373590 + y2 * (23467.773107245835052 + y2 * (209.69518196726306286 + y2));
        return p / q;
    }

    private static double XcmsSine(double x)
    {
        if (x < -Pi || x > Pi)
        {
            x = XcmsModulo(x, 2 * Pi);
            if (x > Pi) x -= 2 * Pi;
            else if (x < -Pi) x += 2 * Pi;
        }
        if (x > Pi / 2) return -XcmsSine(x - Pi);
        if (x < -Pi / 2) return -XcmsSine(x + Pi);
        if (x > Pi / 4) return XcmsCosine(Pi / 2 - x);
        if (x < -Pi / 4) return -XcmsCosine(Pi / 2 + x);
        if (x < 4.209340e-52 && x > -4.209340e-52) return x;
        double y = x / (Pi / 4), y2 = y * y;
        double p = 2066434.3336995858240 + y2 * (-181603.98797407332550 + y2 * (3599.9306949636188317 + y2 * -20.107483294588615719));
        double q = 2631065.9102647698963 + y2 * (39270.242774649000308 + y2 * (278.11919481083844087 + y2));
        return y * (p / q);
    }

    private static double XcmsModulo(double value, double basis)
    {
        double quotient = value / basis;
        double fraction = quotient - Math.Truncate(quotient);
        return fraction * basis;
    }
}
