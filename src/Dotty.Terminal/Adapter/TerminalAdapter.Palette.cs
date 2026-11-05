using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    private void HandleOscPalette(int code, ReadOnlySpan<char> payload)
    {
        lock (_paletteSync)
        {
            Span<char> color = stackalloc char[18];
            TerminalPalette palette = Volatile.Read(ref _palette);
            bool paletteChanged = false, specialChanged = false;
            int position = 0;
            while (position < payload.Length)
            {
                int separator = payload[position..].IndexOf(';');
                if (separator < 0) break;
                ReadOnlySpan<char> field = payload.Slice(position, separator);
                position += separator + 1;
                separator = payload[position..].IndexOf(';');
                ReadOnlySpan<char> spec = separator < 0 ? payload[position..] : payload.Slice(position, separator);
                position = separator < 0 ? payload.Length : position + separator + 1;
                if (!TryParseColorNumber(field, code == 5 ? 4 : 260, out int index)) continue;
                int specialIndex = code == 5 ? index : index - 256;
                if (spec.SequenceEqual("?"))
                {
                    WriteRgb(specialIndex >= 0 ? _specialColors.Get(specialIndex) : palette[index], color);
                    SendColorReply(code, index, color);
                }
                else if (OscColorParser.TryParse(spec, out uint argb))
                {
                    if (specialIndex >= 0) specialChanged |= SetSpecialColor(specialIndex, argb);
                    else paletteChanged |= palette.Set(index, argb);
                }
            }
            if (paletteChanged) RemapPaletteStyles();
            if (paletteChanged || specialChanged) NotifyPaletteChanged();
        }
    }

    private void HandleOscPaletteReset(int code, ReadOnlySpan<char> payload)
    {
        lock (_paletteSync)
        {
            TerminalPalette palette = Volatile.Read(ref _palette);
            bool paletteChanged = false, specialChanged = false;
            if (payload.IsEmpty)
            {
                specialChanged = ResetAllSpecialColors();
                if (code == 104) paletteChanged = palette.ResetAll();
            }
            else
            {
                int position = 0;
                while (position <= payload.Length)
                {
                    int separator = payload[position..].IndexOf(';');
                    ReadOnlySpan<char> field = separator < 0 ? payload[position..] : payload.Slice(position, separator);
                    position = separator < 0 ? payload.Length + 1 : position + separator + 1;
                    if (!TryParseColorNumber(field, code == 105 ? 4 : 260, out int index)) continue;
                    int specialIndex = code == 105 ? index : index - 256;
                    if (specialIndex >= 0) specialChanged |= ResetSpecialColor(specialIndex);
                    else paletteChanged |= palette.Reset(index);
                }
            }
            if (paletteChanged) RemapPaletteStyles();
            if (paletteChanged || specialChanged) NotifyPaletteChanged();
        }
    }

    private void HandleOscSpecialMode(ReadOnlySpan<char> payload)
    {
        int separator = payload.IndexOf(';');
        if (separator <= 0 || separator == payload.Length - 1 ||
            !TryParseColorNumber(payload[..separator], 5, out int index)) return;
        bool enabled = false;
        foreach (char digit in payload[(separator + 1)..])
        {
            if (digit is < '0' or > '9') return;
            enabled |= digit != '0';
        }
        lock (_paletteSync)
        {
            byte mask = (byte)(1 << index);
            byte modes = enabled ? (byte)(_specialColors.Modes | mask) : (byte)(_specialColors.Modes & ~mask);
            if (modes == _specialColors.Modes) return;
            _specialColors = _specialColors with { Modes = modes };
            NotifyPaletteChanged();
        }
    }

    private bool SetSpecialColor(int index, uint value)
    {
        _specialColorOverrides |= (byte)(1 << index);
        if (_specialColors.Get(index) == value) return false;
        _specialColors = _specialColors.Set(index, value);
        return true;
    }

    private bool ResetSpecialColor(int index)
    {
        _specialColorOverrides &= (byte)~(1 << index);
        if (_specialColors.Get(index) == _specialColorDefaultArgb) return false;
        _specialColors = _specialColors.Set(index, _specialColorDefaultArgb);
        return true;
    }

    private bool ResetAllSpecialColors()
    {
        bool changed = false;
        for (int index = 0; index < 5; index++) changed |= ResetSpecialColor(index);
        return changed;
    }

    private bool RefreshSpecialColorDefaults(uint foreground)
    {
        if (_specialColorDefaultArgb == foreground) return false;
        _specialColorDefaultArgb = foreground;
        bool changed = false;
        for (int index = 0; index < 5; index++)
        {
            if ((_specialColorOverrides & (1 << index)) != 0 || _specialColors.Get(index) == foreground) continue;
            _specialColors = _specialColors.Set(index, foreground);
            changed = true;
        }
        return changed;
    }

    private static bool TryParseColorNumber(ReadOnlySpan<char> field, int maximum, out int value)
    {
        value = 0;
        if (field.IsEmpty) return false;
        foreach (char digit in field)
        {
            if (digit is < '0' or > '9') return false;
            value = value * 10 + digit - '0';
            if (value > maximum) return false;
        }
        return true;
    }

    private void HandleOscDynamicColor(int code, ReadOnlySpan<char> payload)
    {
        lock (_paletteSync)
        {
            Span<char> color = stackalloc char[18];
            bool changed = false;
            int position = 0;
            while (position <= payload.Length && code <= 12)
            {
                int separator = payload[position..].IndexOf(';');
                ReadOnlySpan<char> spec = separator < 0 ? payload[position..] : payload.Slice(position, separator);
                position = separator < 0 ? payload.Length + 1 : position + separator + 1;
                if (spec.SequenceEqual("?"))
                {
                    uint value = code switch
                    {
                        10 => DefaultForegroundOverrideArgb ?? ParseHexDefault(DefaultForegroundThemeHex),
                        11 => DefaultBackgroundOverrideArgb ?? ParseHexDefault(DefaultBackgroundThemeHex),
                        _ => 0xFFFFFFFFu,
                    };
                    WriteRgb(value, color);
                    SendColorReply(code, color);
                }
                else if (code != 12 && OscColorParser.TryParse(spec, out uint argb))
                {
                    if (code == 10 && DefaultForegroundOverrideArgb != argb)
                    {
                        Volatile.Write(ref _defaultForegroundOverrideArgb, unchecked((int)argb));
                        changed = true;
                    }
                    else if (code == 11 && DefaultBackgroundOverrideArgb != argb)
                    {
                        Volatile.Write(ref _defaultBackgroundOverrideArgb, unchecked((int)argb));
                        changed = true;
                    }
                }
                code++;
            }
            if (changed) NotifyPaletteChanged();
        }
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
        lock (_paletteSync)
        {
            bool paletteChanged = Volatile.Read(ref _palette).ResetAll();
            bool changed = ResetAllSpecialColors();
            changed |= DefaultForegroundOverrideArgb.HasValue || DefaultBackgroundOverrideArgb.HasValue;
            Volatile.Write(ref _defaultForegroundOverrideArgb, 0);
            Volatile.Write(ref _defaultBackgroundOverrideArgb, 0);
            if (paletteChanged) RemapPaletteStyles();
            if (paletteChanged || changed) NotifyPaletteChanged();
        }
    }

    private void RemapPaletteStyles()
    {
        ReadOnlySpan<uint> colors = Volatile.Read(ref _palette).EffectiveColors;
        _buffer.StyleSet.RemapPalette(colors);
        _currentAttributes.RemapPalette(colors);
        _savedMainAttributes.RemapPalette(colors);
        _savedAlternateAttributes.RemapPalette(colors);
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
        const string digits = "0123456789abcdef";
        char high = digits[value >> 4], low = digits[value & 0xF];
        output[0] = high; output[1] = low; output[2] = high; output[3] = low;
    }


    private static bool TryParseHexComponent(ReadOnlySpan<char> input, out byte value)
    {
        value = 0;
        if (input.IsEmpty || input.Length > 2) return false;
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
        uint scaled = input.Length == 1 ? parsed * 17 : parsed;
        value = (byte)scaled;
        return true;
    }

    private static uint ParseHexDefault(string color)
    {
        ReadOnlySpan<char> hex = color.AsSpan();
        if (!hex.IsEmpty && hex[0] == '#') hex = hex[1..];
        if (hex.Length is not (3 or 6)) return 0xFFFFFFFFu;
        int stride = hex.Length / 3;
        if (!TryParseHexComponent(hex[..stride], out byte red) ||
            !TryParseHexComponent(hex.Slice(stride, stride), out byte green) ||
            !TryParseHexComponent(hex[(stride * 2)..], out byte blue)) return 0xFFFFFFFFu;
        return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }
}
