using System;
using Dotty.Abstractions.Adapter;

namespace Dotty.Terminal.Adapter;

// Terminal status/capability queries and key-reporting negotiation:
// DECRQM, XTGETTCAP and modifyOtherKeys.
public partial class TerminalAdapter
{
    public void OnRequestMode(int mode, bool isPrivate)
    {
        // DECRQM: 1=set, 2=reset, 3=permanently set, 4=permanently reset, 0=not recognized.
        // Only modes Dotty actually tracks report set/reset; everything else is 0.
        int state = (mode, isPrivate) switch
        {
            (1, true) => ApplicationCursorKeysEnabled ? 1 : 2, // DECCKM
            (6, true) => _buffer.OriginMode ? 1 : 2, // DECOM
            (7, true) => _buffer.AutoWrap ? 1 : 2, // DECAWM
            (25, true) => _buffer.CursorVisible ? 1 : 2, // DECTCEM
            (1000, true) => CurrentMouseMode == MouseMode.Normal ? 1 : 2,
            (1002, true) => CurrentMouseMode == MouseMode.ButtonEvent ? 1 : 2,
            (1003, true) => CurrentMouseMode == MouseMode.AnyEvent ? 1 : 2,
            (1004, true) => FocusReportingEnabled ? 1 : 2,
            (1006, true) => CurrentMouseEncoding == MouseEncoding.SGR ? 1 : 2,
            (1049, true) => _buffer.IsAlternateScreenActive ? 1 : 2,
            (2004, true) => _buffer.BracketedPasteMode ? 1 : 2,
            (2026, true) => SynchronizedUpdateActive ? 1 : 2,
            _ => 0,
        };
        var handler = ReplyRequested;
        if (handler is null)
            return;
        Span<char> reply = stackalloc char[32];
        int length = 0;
        if (isPrivate)
        {
            "\x1b[?".AsSpan().CopyTo(reply);
            length = 3;
        }
        else
        {
            "\x1b[".AsSpan().CopyTo(reply);
            length = 2;
        }
        mode.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        state.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = '$';
        reply[length++] = 'y';
        handler(reply[..length]);
    }

    public void OnQueryCapability(string requestHex)
    {
        // XTGETTCAP: answer only capabilities Dotty actually implements.
        // Unknown keys get no reply so applications fall back instead of
        // trusting a claim this terminal cannot honor.
        OnQueryCapability(requestHex.AsSpan());
    }

    internal void OnQueryCapability(ReadOnlySpan<char> requestHex)
    {
        var handler = ReplyRequested;
        if (handler is null || requestHex.IsEmpty) return;
        Span<char> valueHex = stackalloc char[64];
        int valueLength = GetCapabilityValueHex(requestHex, valueHex);
        if (valueLength < 0) return;
        int total = 5 + requestHex.Length + 1 + valueLength + 2;
        Span<char> reply = total <= 128 ? stackalloc char[128] : new char[total];
        "\x1bP1+r".AsSpan().CopyTo(reply);
        int length = 5;
        requestHex.CopyTo(reply[length..]);
        length += requestHex.Length;
        reply[length++] = '=';
        valueHex[..valueLength].CopyTo(reply[length..]);
        length += valueLength;
        reply[length++] = '\x1b';
        reply[length++] = '\\';
        handler(reply[..length]);
    }

    // Writes the hex-encoded capability value into destination. Returns the
    // length, or -1 for unknown keys / malformed requests (no reply).
    private int GetCapabilityValueHex(ReadOnlySpan<char> requestHex, Span<char> destination)
    {
        if (requestHex.IsEmpty || (requestHex.Length & 1) != 0)
            return -1;
        Span<char> key = stackalloc char[32];
        if (requestHex.Length / 2 > key.Length)
            return -1;
        for (int i = 0; i < requestHex.Length / 2; i++)
        {
            int hi = HexNibble(requestHex[2 * i]);
            int lo = HexNibble(requestHex[2 * i + 1]);
            if (hi < 0 || lo < 0)
                return -1;
            key[i] = (char)((hi << 4) | lo);
        }
        ReadOnlySpan<char> decoded = key[..(requestHex.Length / 2)];
        ReadOnlySpan<char> value = decoded switch
        {
            _ when decoded.SequenceEqual("TN") => "dotty",
            _ when decoded.SequenceEqual("Co") => "256",
            _ when decoded.SequenceEqual("RGB") => "8/8/8",
            _ => ReadOnlySpan<char>.Empty,
        };
        bool isKittyKeyboard = decoded.SequenceEqual("kitty-keyboard");
        if (value.IsEmpty && !isKittyKeyboard)
            return -1;
        if (isKittyKeyboard)
        {
            Span<char> decimalValue = stackalloc char[16];
            if (!KittyKeyboardFlags.TryFormat(decimalValue, out int written)) return -1;
            const string hexDigits = "0123456789ABCDEF";
            if (destination.Length < written * 2) return -1;
            for (int i = 0; i < written; i++)
            {
                destination[i * 2] = hexDigits[(decimalValue[i] >> 4) & 0xF];
                destination[i * 2 + 1] = hexDigits[decimalValue[i] & 0xF];
            }
            return written * 2;
        }
        const string digits = "0123456789ABCDEF";
        if (destination.Length < value.Length * 2)
            return -1;
        for (int i = 0; i < value.Length; i++)
        {
            destination[2 * i] = digits[(value[i] >> 4) & 0xF];
            destination[2 * i + 1] = digits[value[i] & 0xF];
        }
        return value.Length * 2;
    }

    public void OnSetModifyOtherKeys(int level)
    {
        ModifyOtherKeysLevel = level is >= 0 and <= 2 ? level : ModifyOtherKeysLevel;
    }

    public int ModifyOtherKeysLevel { get; private set; }

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
