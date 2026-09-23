using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;

namespace Dotty.Runtime.Scripting;

internal enum LuaValueKind
{
    Nil,
    Boolean,
    Number,
    String,
    Tab,
    Pane
}

internal readonly struct LuaValue
{
    private readonly string? _string;
    private readonly bool _boolean;
    private readonly double _number;
    private readonly TerminalTab? _tab;
    private readonly LeafPane? _pane;

    private LuaValue(
        LuaValueKind kind,
        string? text = null,
        bool boolean = false,
        double number = 0,
        TerminalTab? tab = null,
        LeafPane? pane = null)
    {
        Kind = kind;
        _string = text;
        _boolean = boolean;
        _number = number;
        _tab = tab;
        _pane = pane;
    }

    internal LuaValueKind Kind { get; }

    internal static LuaValue Nil => default;

    internal static LuaValue From(bool value)
    {
        return new LuaValue(LuaValueKind.Boolean, boolean: value);
    }

    internal static LuaValue From(double value)
    {
        return new LuaValue(LuaValueKind.Number, number: value);
    }

    internal static LuaValue From(string? value)
    {
        return value == null ? Nil : new LuaValue(LuaValueKind.String, text: value);
    }

    internal static LuaValue StringResult => new(LuaValueKind.String);

    internal static LuaValue FromTab(TerminalTab tab)
    {
        return new LuaValue(LuaValueKind.Tab, tab: tab);
    }

    internal static LuaValue FromPane(LeafPane pane, TerminalTab tab)
    {
        return new LuaValue(LuaValueKind.Pane, tab: tab, pane: pane);
    }

    internal bool IsNil => Kind == LuaValueKind.Nil;

    internal bool TryGetString(out string? value)
    {
        value = _string;
        return Kind == LuaValueKind.String;
    }

    internal bool TryGetBoolean(out bool value)
    {
        value = _boolean;
        return Kind == LuaValueKind.Boolean;
    }

    internal bool TryGetNumber(out double value)
    {
        value = _number;
        return Kind == LuaValueKind.Number;
    }

    internal bool TryGetTab(out TerminalTab tab)
    {
        tab = _tab!;
        return Kind == LuaValueKind.Tab;
    }

    internal bool TryGetPane(out LeafPane pane, out TerminalTab tab)
    {
        pane = _pane!;
        tab = _tab!;
        return Kind == LuaValueKind.Pane;
    }
}
