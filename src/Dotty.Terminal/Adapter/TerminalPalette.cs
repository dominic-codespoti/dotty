using System;

namespace Dotty.Terminal.Adapter;

/// <summary>Adapter-local ANSI and xterm 256-color palette.</summary>
public sealed class TerminalPalette
{
    private readonly uint[] _baseline = new uint[16];
    private readonly uint[] _effective;
    private readonly bool[] _overridden = new bool[256];

    public TerminalPalette()
    {
        _effective = SgrColorArgb.CreateStockPalette();
        Array.Copy(_effective, _baseline, 16);
    }

    public ReadOnlySpan<uint> BaselineAnsi16 => _baseline;
    public ReadOnlySpan<uint> EffectiveAnsi16 => _effective.AsSpan(0, 16);
    public uint this[int index] => (uint)index < 256 ? _effective[index] : 0;
    public bool IsOverridden(int index) => (uint)index < 256 && _overridden[index];

    public bool SetBaseline(ReadOnlySpan<uint> ansi16)
    {
        if (ansi16.Length != 16)
            throw new ArgumentException("ANSI palette must have exactly 16 colors", nameof(ansi16));
        bool same = true;
        for (int i = 0; i < 16; i++) same &= _baseline[i] == ansi16[i];
        if (same) return false;
        ansi16.CopyTo(_baseline);
        ansi16.CopyTo(_effective);
        Array.Clear(_overridden);
        for (int i = 16; i < 256; i++) _effective[i] = SgrColorArgb.StockColorAt(i);
        return true;
    }

    public bool Set(int index, uint argb)
    {
        if ((uint)index >= 256) return false;
        if (_overridden[index] && _effective[index] == argb) return false;
        _overridden[index] = true;
        if (_effective[index] == argb) return false;
        _effective[index] = argb;
        return true;
    }

    public bool Reset(int index)
    {
        if ((uint)index >= 256 || !_overridden[index]) return false;
        uint resetColor = index < 16 ? _baseline[index] : SgrColorArgb.StockColorAt(index);
        bool changed = _effective[index] != resetColor;
        _effective[index] = resetColor;
        _overridden[index] = false;
        return changed;
    }

    public bool ResetAll()
    {
        bool changed = false;
        for (int i = 0; i < 256; i++)
        {
            if (!_overridden[i]) continue;
            uint resetColor = i < 16 ? _baseline[i] : SgrColorArgb.StockColorAt(i);
            changed |= _effective[i] != resetColor;
            _effective[i] = resetColor;
            _overridden[i] = false;
        }
        return changed;
    }
}
