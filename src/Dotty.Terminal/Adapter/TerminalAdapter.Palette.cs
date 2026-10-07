using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    private void HandleOscPalette(ReadOnlySpan<char> payload)
    {
        lock (_paletteSync) HandleOscPaletteLocked(payload);
    }

    private void HandleOscPaletteLocked(ReadOnlySpan<char> payload)
    {
        Span<char> color = stackalloc char[18];
        Span<uint> previous = stackalloc uint[16];
        TerminalPalette palette = Volatile.Read(ref _palette);
        int pos = 0;
        while (pos < payload.Length)
        {
            int semi = payload[pos..].IndexOf(';');
            if (semi < 0) return;
            ReadOnlySpan<char> indexField = payload.Slice(pos, semi);
            pos += semi + 1;
            semi = payload[pos..].IndexOf(';');
            ReadOnlySpan<char> spec = semi < 0 ? payload[pos..] : payload.Slice(pos, semi);
            pos = semi < 0 ? payload.Length : pos + semi + 1;
            if (!TryParsePaletteIndex(indexField, out int index)) continue;
            if (spec.SequenceEqual("?"))
            {
                WriteRgb(palette[index], color);
                SendColorReply(4, index, color);
                continue;
            }
            if (!TryParseColorSpec(spec, out uint argb)) continue;
            bool remap = index < 16 && palette[index] != argb;
            if (remap) palette.EffectiveAnsi16.CopyTo(previous);
            if (!palette.Set(index, argb)) continue;
            if (remap) RemapAnsiStyles(previous);
            NotifyPaletteChanged();
        }
    }

    private void HandleOscPaletteReset(ReadOnlySpan<char> payload)
    {
        lock (_paletteSync) HandleOscPaletteResetLocked(payload);
    }

    private void HandleOscPaletteResetLocked(ReadOnlySpan<char> payload)
    {
        Span<uint> previous = stackalloc uint[16];
        TerminalPalette palette = Volatile.Read(ref _palette);
        palette.EffectiveAnsi16.CopyTo(previous);
        bool changed = false;
        if (payload.IsEmpty)
        {
            changed = palette.ResetAll();
        }
        else
        {
            int pos = 0;
            while (pos <= payload.Length)
            {
                int semi = payload[pos..].IndexOf(';');
                ReadOnlySpan<char> token = semi < 0 ? payload[pos..] : payload.Slice(pos, semi);
                pos = semi < 0 ? payload.Length + 1 : pos + semi + 1;
                if (token.IsEmpty || !TryParsePaletteIndex(token, out int index)) continue;
                changed |= palette.Reset(index);
            }
        }
        if (!changed) return;
        RemapAnsiStyles(previous);
        NotifyPaletteChanged();
    }

    private void HandleOscDynamicColor(int code, ReadOnlySpan<char> payload)
    {
        if (payload.SequenceEqual("?"))
        {
            uint value = code switch
            {
                10 => DefaultForegroundOverrideArgb ?? ParseHexDefault(DefaultForegroundThemeHex),
                11 => DefaultBackgroundOverrideArgb ?? ParseHexDefault(DefaultBackgroundThemeHex),
                _ => 0xFFFFFFFFu,
            };
            Span<char> color = stackalloc char[18];
            WriteRgb(value, color);
            SendColorReply(code, color);
            return;
        }
        if (code == 12 || !TryParseColorSpec(payload, out uint argb)) return;
        if (code == 10)
        {
            if (DefaultForegroundOverrideArgb == argb) return;
            Volatile.Write(ref _defaultForegroundOverrideArgb, unchecked((int)argb));
        }
        else
        {
            if (DefaultBackgroundOverrideArgb == argb) return;
            Volatile.Write(ref _defaultBackgroundOverrideArgb, unchecked((int)argb));
        }
        NotifyPaletteChanged();
    }

    private void ClearDynamicColorOverride(int code)
    {
        if (code == 10)
        {
            if (DefaultForegroundOverrideArgb is null) return;
            Volatile.Write(ref _defaultForegroundOverrideArgb, 0);
        }
        else
        {
            if (DefaultBackgroundOverrideArgb is null) return;
            Volatile.Write(ref _defaultBackgroundOverrideArgb, 0);
        }
        NotifyPaletteChanged();
    }

    private void ResetPaletteForRis()
    {
        lock (_paletteSync) ResetPaletteForRisLocked();
    }

    private void ResetPaletteForRisLocked()
    {
        Span<uint> previous = stackalloc uint[16];
        TerminalPalette palette = Volatile.Read(ref _palette);
        palette.EffectiveAnsi16.CopyTo(previous);
        bool changed = palette.ResetAll();
        changed |= DefaultForegroundOverrideArgb.HasValue || DefaultBackgroundOverrideArgb.HasValue;
        Volatile.Write(ref _defaultForegroundOverrideArgb, 0);
        Volatile.Write(ref _defaultBackgroundOverrideArgb, 0);
        if (!changed) return;
        RemapAnsiStyles(previous);
        NotifyPaletteChanged();
    }

    private void RemapAnsiStyles(ReadOnlySpan<uint> previous)
    {
        Span<uint> current = stackalloc uint[16];
        Volatile.Read(ref _palette).EffectiveAnsi16.CopyTo(current);
        _buffer.StyleSet.RemapAnsiPalette(previous, current);
    }

    private void NotifyPaletteChanged()
    {
        _buffer.InvalidateRowsForPaletteChange();
        PaletteChanged?.Invoke();
        RequestRender();
    }

    private void SendColorReply(int code, ReadOnlySpan<char> color) => SendColorReply(code, -1, color);

    private void SendColorReply(int code, int index, ReadOnlySpan<char> color)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[48];
        int length = 0;
        reply[length++] = '\x1b'; reply[length++] = ']';
        code.TryFormat(reply[length..], out int written); length += written;
        reply[length++] = ';';
        if (index >= 0)
        {
            index.TryFormat(reply[length..], out written); length += written;
            reply[length++] = ';';
        }
        color.CopyTo(reply[length..]); length += color.Length;
        reply[length++] = '\x1b'; reply[length++] = '\\';
        handler(reply[..length]);
    }

    private static void WriteRgb(uint argb, Span<char> destination)
    {
        destination[0] = 'r'; destination[1] = 'g'; destination[2] = 'b'; destination[3] = ':';
        WriteRgbComponent((byte)(argb >> 16), destination[4..]);
        destination[8] = '/';
        WriteRgbComponent((byte)(argb >> 8), destination[9..]);
        destination[13] = '/';
        WriteRgbComponent((byte)argb, destination[14..]);
    }

    private static void WriteRgbComponent(byte value, Span<char> output)
    {
        const string digits = "0123456789ABCDEF";
        char high = digits[value >> 4], low = digits[value & 0xF];
        output[0] = high; output[1] = low; output[2] = high; output[3] = low;
    }

    private static bool TryParsePaletteIndex(ReadOnlySpan<char> field, out int index)
    {
        index = 0;
        if (field.IsEmpty || field.Length > 3) return false;
        foreach (char c in field)
        {
            if (c is < '0' or > '9') return false;
            index = index * 10 + c - '0';
        }
        return index < 256;
    }

    private static bool TryParseColorSpec(ReadOnlySpan<char> spec, out uint argb)
    {
        argb = 0;
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> body = spec[4..];
            int first = body.IndexOf('/');
            if (first <= 0) return false;
            int offset = body[(first + 1)..].IndexOf('/');
            if (offset <= 0) return false;
            int second = first + 1 + offset;
            if (body[(second + 1)..].IndexOf('/') >= 0 ||
                !TryParseHexComponent(body[..first], out byte r) ||
                !TryParseHexComponent(body.Slice(first + 1, second - first - 1), out byte g) ||
                !TryParseHexComponent(body[(second + 1)..], out byte b)) return false;
            argb = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
            return true;
        }
        ReadOnlySpan<char> hex = spec;
        if (!hex.IsEmpty && hex[0] == '#') hex = hex[1..];
        if (hex.Length is not (3 or 6)) return false;
        int stride = hex.Length / 3;
        if (!TryParseHexComponent(hex[..stride], out byte red) ||
            !TryParseHexComponent(hex.Slice(stride, stride), out byte green) ||
            !TryParseHexComponent(hex[(stride * 2)..], out byte blue)) return false;
        argb = 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
        return true;
    }

    private static bool TryParseHexComponent(ReadOnlySpan<char> input, out byte value)
    {
        value = 0;
        if (input.IsEmpty || input.Length > 4) return false;
        uint parsed = 0;
        foreach (char c in input)
        {
            int nibble = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (nibble < 0) return false;
            parsed = (parsed << 4) | (uint)nibble;
        }
        uint scaled = input.Length switch { 1 => parsed * 17, 2 => parsed, 3 => parsed >> 4, _ => parsed >> 8 };
        value = (byte)scaled;
        return true;
    }

    private static uint ParseHexDefault(string color) =>
        TryParseColorSpec(color.AsSpan(), out uint value) ? value : 0xFFFFFFFFu;
}
