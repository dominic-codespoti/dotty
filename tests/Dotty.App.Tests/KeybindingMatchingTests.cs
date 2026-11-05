using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Abstractions.Config;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Input;
using Dotty.Silk;
using Xunit;
using SilkKey = Silk.NET.Input.Key;

namespace Dotty.App.Tests;

/// <summary>Chord grammar, §5.3 matching across keyboard layouts, diagnostics, platform defaults and menu labels.</summary>
public sealed class KeybindingMatchingTests
{
    // ---- chord grammar ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ctrl+shift+t", "ctrl+shift+t")]
    [InlineData(" Shift + CTRL + T ", "ctrl+shift+t")]
    [InlineData("super+alt+shift+ctrl+x", "ctrl+shift+alt+super+x")]
    [InlineData("cmd+k", "super+k")]
    [InlineData("command+k", "super+k")]
    [InlineData("win+e", "super+e")]
    [InlineData("option+x", "alt+x")]
    [InlineData("opt+x", "alt+x")]
    [InlineData("control+c", "ctrl+c")]
    [InlineData("ctrl+return", "ctrl+enter")]
    [InlineData("esc", "escape")]
    [InlineData("shift+pgup", "shift+pageup")]
    [InlineData("shift+pgdn", "shift+pagedown")]
    [InlineData("ctrl+del", "ctrl+delete")]
    [InlineData("ctrl+ins", "ctrl+insert")]
    [InlineData("ctrl+,", "ctrl+comma")]
    [InlineData("ctrl+.", "ctrl+period")]
    [InlineData("ctrl+/", "ctrl+slash")]
    [InlineData("ctrl+;", "ctrl+semicolon")]
    [InlineData("ctrl+'", "ctrl+apostrophe")]
    [InlineData("ctrl+`", "ctrl+graveaccent")]
    [InlineData("cmd+[", "super+leftbracket")]
    [InlineData("cmd+]", "super+rightbracket")]
    [InlineData("ctrl+\\", "ctrl+backslash")]
    [InlineData("ctrl+-", "ctrl+minus")]
    [InlineData("ctrl+=", "ctrl+equal")]
    [InlineData("ctrl++", "ctrl+plus")]
    [InlineData("+", "plus")]
    [InlineData("ctrl+shift++", "ctrl+shift+plus")]
    [InlineData("ctrl+Number1", "ctrl+1")]
    [InlineData("alt+D7", "alt+7")]
    [InlineData("F11", "f11")]
    [InlineData("ctrl+KeypadAdd", "ctrl+keypadadd")]
    [InlineData("physical:ctrl+shift+c", "physical:ctrl+shift+c")]
    [InlineData("Physical:Shift+Ctrl+C", "physical:ctrl+shift+c")]
    public void TryNormalizeChord_Canonicalizes(string input, string expected)
    {
        Assert.True(KeybindingManager.TryNormalizeChord(input, out string normalized));
        Assert.Equal(expected, normalized);
        // Canonical text is a fixed point.
        Assert.True(KeybindingManager.TryNormalizeChord(normalized, out string again));
        Assert.Equal(normalized, again);
    }

    [Theory]
    [InlineData("", "empty chord")]
    [InlineData("   ", "empty chord")]
    [InlineData("meta+x", "not supported")]
    [InlineData("hyper+x", "not supported")]
    [InlineData("fn+f1", "not supported")]
    [InlineData("mod+x", "not supported")]
    [InlineData("ctrl+foo", "unknown key 'foo'")]
    [InlineData("ctrl+a+b", "two keys")]
    [InlineData("ctrl+shift", "no key")]
    [InlineData("ctrl+ctrl+a", "ctrl written twice")]
    [InlineData("cmd+super+a", "super written twice")]
    [InlineData("alt+option+a", "alt written twice")]
    [InlineData("ctrl++a", "empty part")]
    [InlineData("ctrl+a+", "empty part")]
    [InlineData("+ctrl", "empty part")]
    [InlineData("ctrl+page up", "whitespace")]
    [InlineData("physical:", "empty part")]
    public void TryParseChord_RejectsWithReason(string input, string reason)
    {
        Assert.False(KeybindingManager.TryParseChord(input, out _, out string? error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void FormatChord_RoundTripsEveryKeyNameAndModifierSet()
    {
        foreach (string name in KeybindingManager.KeyNames)
        {
            for (int mask = 0; mask < 32; mask++)
            {
                var key = new TerminalChordKey((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, name, (mask & 16) != 0);
                string text = KeybindingManager.FormatChord(key);
                Assert.True(KeybindingManager.TryParseChord(text, out var parsed, out string? error), $"{text}: {error}");
                Assert.Equal(key, parsed);
                Assert.Equal(text, KeybindingManager.FormatChord(parsed));
            }
        }
    }

    [Fact]
    public void EverySilkKeyName_IsAKnownChordName_AndEveryChordNameIsReachable()
    {
        var reached = new HashSet<string> { "plus" };
        foreach (SilkKey key in Enum.GetValues<SilkKey>())
        {
            if (key == SilkKey.Unknown) continue;
            string silkName = SilkKeyMapper.GetKeyName(key);
            Assert.True(KeybindingManager.TryCanonicalKeyName(KeybindingManager.NormalizeKeyName(silkName), out string canonical), silkName);
            reached.Add(canonical);
        }
        Assert.Empty(KeybindingManager.KeyNames.Except(reached));
    }

    // ---- layout matrix (plan-12 §5.3) -------------------------------------------------------------------------

    /// <summary>Unshifted character each layout gives the US-position key (only keys that differ from QWERTY).</summary>
    private static readonly Dictionary<string, Dictionary<SilkKey, char>> Layouts = new()
    {
        ["qwerty"] = new(),
        ["dvorak"] = Map("Q'", "W,", "E.", "Rp", "Ty", "Yf", "Ug", "Ic", "Or", "Pl", "[/", "]=", "Aa", "So", "De", "Fu", "Gi", "Hd", "Jh", "Kt",
            "Ln", ";s", "'-", "Z;", "Xq", "Cj", "Vk", "Bx", "Nb", "Mm", ",w", ".v", "/z", "-[", "=]"),
        ["azerty"] = Map("Qa", "Wz", "Aq", "Zw", ";m", "M,", "1&", "2é", "3\"", "4'", "5(", "6-", "7è", "8_", "9ç", "0à"),
        ["colemak"] = Map("Ef", "Rp", "Tg", "Yj", "Ul", "Iu", "Oy", "P;", "Sr", "Ds", "Ft", "Gd", "Jn", "Ke", "Li", ";o", "Nk"),
        ["russian"] = Map("Qй", "Wц", "Eу", "Rк", "Tе", "Yн", "Uг", "Iш", "Oщ", "Pз", "Aф", "Sы", "Dв", "Fа", "Gп", "Hр", "Jо", "Kл", "Lд",
            "Zя", "Xч", "Cс", "Vм", "Bи", "Nт", "Mь", ";ж", "'э", ",б", ".ю", "[х", "]ъ"),
    };

    private static Dictionary<SilkKey, char> Map(params string[] pairs)
    {
        var map = new Dictionary<SilkKey, char>();
        foreach (string pair in pairs)
            map[KeyOf(pair[0])] = pair[1];
        return map;
    }

    private static SilkKey KeyOf(char c) => c switch
    {
        >= 'A' and <= 'Z' => Enum.Parse<SilkKey>(c.ToString()),
        >= '0' and <= '9' => Enum.Parse<SilkKey>("Number" + c),
        ';' => SilkKey.Semicolon,
        '\'' => SilkKey.Apostrophe,
        ',' => SilkKey.Comma,
        '.' => SilkKey.Period,
        '/' => SilkKey.Slash,
        '[' => SilkKey.LeftBracket,
        ']' => SilkKey.RightBracket,
        '-' => SilkKey.Minus,
        '=' => SilkKey.Equal,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    private static readonly SilkKey[] MainBlock =
        Enumerable.Range('A', 26).Select(c => KeyOf((char)c))
            .Concat(Enumerable.Range('0', 10).Select(c => KeyOf((char)c)))
            .Concat([SilkKey.Semicolon, SilkKey.Apostrophe, SilkKey.Comma, SilkKey.Period, SilkKey.Slash,
                SilkKey.LeftBracket, SilkKey.RightBracket, SilkKey.Minus, SilkKey.Equal])
            .ToArray();

    private static char LayoutChar(string layout, SilkKey key)
    {
        if (Layouts[layout].TryGetValue(key, out char c)) return c;
        string name = KeybindingManager.NormalizeKeyName(SilkKeyMapper.GetKeyName(key));
        return name.Length == 1 ? char.ToLowerInvariant(name[0]) : key switch
        {
            SilkKey.Semicolon => ';',
            SilkKey.Apostrophe => '\'',
            SilkKey.Comma => ',',
            SilkKey.Period => '.',
            SilkKey.Slash => '/',
            SilkKey.LeftBracket => '[',
            SilkKey.RightBracket => ']',
            SilkKey.Minus => '-',
            SilkKey.Equal => '=',
            _ => '\0',
        };
    }

    public static IEnumerable<object[]> LayoutDefaults()
    {
        foreach (var platform in new[] { KeybindingPlatform.Linux, KeybindingPlatform.MacOS })
        {
            var manager = new KeybindingManager(platform);
            foreach (var (chord, action) in manager.GetBindings())
            {
                KeybindingManager.TryParseChord(chord, out var key, out _);
                if (key.KeyName.Length != 1 || !char.IsAsciiLetterOrDigit(key.KeyName[0])) continue;
                foreach (string layout in Layouts.Keys)
                    yield return [platform, layout, chord, action.Action];
            }
        }
    }

    /// <summary>
    /// Every default letter/digit chord fires from the key that types that Latin character on the layout; when no key
    /// types it (Cyrillic letters, AZERTY's shifted digits) it fires from the US-position key.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayoutDefaults))]
    public void DefaultLetterAndDigitChords_MatchOnEveryLayout(KeybindingPlatform platform, string layout, string chord, TerminalAction expected)
    {
        var manager = new KeybindingManager(platform);
        KeybindingManager.TryParseChord(chord, out var key, out _);
        char wanted = key.KeyName[0];
        SilkKey physical = MainBlock.FirstOrDefault(k => LayoutChar(layout, k) == wanted, SilkKey.Unknown);
        if (physical == SilkKey.Unknown)
            physical = KeyOf(char.ToUpperInvariant(wanted));

        Assert.True(manager.TryGetBinding(key.Control, key.Shift, key.Alt, key.Super, SilkKeyMapper.GetKeyName(physical),
            LayoutChar(layout, physical), out var binding), $"{layout}: {chord} via {physical}");
        Assert.Equal(expected, binding.Action);
    }

    [Theory]
    [InlineData("dvorak", SilkKey.C)]   // types 'j'
    [InlineData("dvorak", SilkKey.V)]   // types 'k'
    [InlineData("colemak", SilkKey.T)]  // types 'g'
    [InlineData("azerty", SilkKey.A)]   // types 'q'
    public void LatinLayout_PhysicalPositionDoesNotFireTheUsLetterChord(string layout, SilkKey physical)
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        manager.ApplyCustomBindings(new Dictionary<string, string> { [$"ctrl+shift+{SilkKeyMapper.GetKeyName(physical).ToLowerInvariant()}"] = "select_all" });
        char typed = LayoutChar(layout, physical);
        bool found = manager.TryGetBinding(true, true, false, false, SilkKeyMapper.GetKeyName(physical), typed, out var binding);
        Assert.False(found && binding.Action == TerminalAction.SelectAll);
    }

    [Fact]
    public void PhysicalPrefix_MatchesThePositionWhateverTheLayoutTypes()
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        manager.ApplyCustomBindings(new Dictionary<string, string> { ["physical:ctrl+shift+c"] = "select_all" });

        // Dvorak: the US-C position types 'j'; the physical chord wins there, the layout C (US-I position) still copies.
        Assert.True(manager.TryGetBinding(true, true, false, false, "C", 'j', out var atPosition));
        Assert.Equal(TerminalAction.SelectAll, atPosition.Action);
        Assert.True(manager.TryGetBinding(true, true, false, false, "I", 'c', out var atLetter));
        Assert.Equal(TerminalAction.Copy, atLetter.Action);
        // The physical chord is checked before the layout match on the same key.
        Assert.True(manager.TryGetBinding(true, true, false, false, "C", 'c', out var qwerty));
        Assert.Equal(TerminalAction.SelectAll, qwerty.Action);
    }

    [Theory]
    [InlineData("Equal", '=', true, TerminalAction.ZoomIn)]          // US Shift+'=' types '+'
    [InlineData("Equal", '=', false, TerminalAction.ZoomIn)]         // ctrl+equal
    [InlineData("RightBracket", '+', false, TerminalAction.ZoomIn)]  // German: '+' unshifted
    [InlineData("Minus", '-', false, TerminalAction.ZoomOut)]
    [InlineData("Number0", '0', false, TerminalAction.ResetZoom)]
    [InlineData("Number0", 'à', false, TerminalAction.ResetZoom)]    // AZERTY: digit is shifted, physical fallback
    public void ZoomChords_MatchAcrossLayouts(string physical, char typed, bool shift, TerminalAction expected)
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        Assert.True(manager.TryGetBinding(true, shift, false, false, physical, typed, out var binding));
        Assert.Equal(expected, binding.Action);
    }

    [Fact]
    public void KeypadDigits_DoNotMatchMainRowDigitChords()
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        Assert.False(manager.TryGetBinding(false, false, true, false, "Keypad1", '1', out _));
        Assert.True(manager.TryGetBinding(false, false, true, false, "Number1", '1', out var tab));
        Assert.Equal(TerminalAction.SwitchTab1, tab.Action);
    }

    [Fact]
    public void Lookup_DoesNotAllocate()
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        manager.TryGetBinding(true, true, false, false, "C", 'c', out _);
        manager.TryGetBinding(true, true, false, false, "C", 'с', out _);
        manager.TryGetBinding(true, false, false, false, "Equal", '+', out _);
        AllocationAssert.NoAllocations(() =>
        {
            manager.TryGetBinding(true, true, false, false, "C", 'c', out _);
            manager.TryGetBinding(true, true, false, false, "C", 'с', out _);
            manager.TryGetBinding(true, false, false, false, "Equal", '+', out _);
            manager.TryGetBinding(false, false, false, false, "Up", 0, out _);
        });
    }

    // ---- diagnostics ------------------------------------------------------------------------------------------

    [Fact]
    public void ApplyCustomBindings_ReportsEveryRejectedEntry_AndAppliesTheRest()
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        manager.ApplyCustomBindings(new Dictionary<string, string>
        {
            ["meta+x"] = "copy",
            ["ctrl+shift+g"] = "NotAnAction",
            ["ctrl+shift+y"] = "copy:extra",
            ["ctrl+shift+u"] = "send_text:\\q",
            ["ctrl+shift+h"] = "send_text",
            ["ctrl+shift+j"] = "paste",
            ["shift+ctrl+j"] = "select_all",
            ["ctrl+shift+k"] = "send_text:\\x01",
        });

        var messages = manager.Diagnostics.Select(d => d.Chord + " => " + d.Message).ToArray();
        Assert.Equal(6, messages.Length);
        Assert.Contains(messages, m => m.StartsWith("meta+x => modifier 'meta' is not supported", StringComparison.Ordinal));
        Assert.Contains(messages, m => m == "ctrl+shift+g => unknown action 'NotAnAction'");
        Assert.Contains(messages, m => m == "ctrl+shift+y => action 'copy' takes no argument");
        Assert.Contains(messages, m => m == "ctrl+shift+u => unknown escape '\\q'");
        Assert.Contains(messages, m => m.StartsWith("ctrl+shift+h => action 'send_text' needs an argument", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("shift+ctrl+j => same chord as \"ctrl+shift+j\"", StringComparison.Ordinal));

        Assert.True(manager.TryGetBinding(true, true, false, false, "J", 'j', out var later));
        Assert.Equal(TerminalAction.SelectAll, later.Action);
        Assert.True(manager.TryGetBinding(true, true, false, false, "K", 'k', out var send));
        Assert.Equal(new BoundAction(TerminalAction.SendText, "\x01"), send);
        Assert.False(manager.TryGetBinding(true, true, false, false, "G", 'g', out _));
    }

    [Fact]
    public void RegisterDefaults_ClearsDiagnostics_AndDefaultsHaveNone()
    {
        var manager = new KeybindingManager(KeybindingPlatform.MacOS);
        Assert.Empty(manager.Diagnostics);
        manager.ApplyCustomBindings(new Dictionary<string, string> { ["meta+x"] = "copy" });
        Assert.Single(manager.Diagnostics);
        manager.RegisterDefaults();
        Assert.Empty(manager.Diagnostics);
    }

    [Fact]
    public void Validate_DoesNotTouchALiveManager()
    {
        var diagnostics = KeybindingManager.Validate(new Dictionary<string, string> { ["ctrl+foo"] = "copy", ["ctrl+a"] = "copy" });
        Assert.Equal("keybindings: \"ctrl+foo\": \"copy\": unknown key 'foo'", Assert.Single(diagnostics).ToString());
    }

    [Fact]
    public void ConfigFileValidator_ReportsKeybindingProblemsWithPositions()
    {
        const string json = "{\n  \"keybindings\": {\n    \"meta+x\": \"copy\",\n    \"ctrl+shift+g\": \"nope\",\n    \"ctrl+a\": \"send_text:\\\\x01\"\n  }\n}";
        var diagnostics = Dotty.Runtime.Config.ConfigFileValidator.ValidateJson("config.json", System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(3, diagnostics[0].Line);
        Assert.Contains("invalid chord \"meta+x\"", diagnostics[0].Message);
        Assert.Equal(4, diagnostics[1].Line);
        Assert.Contains("unknown action 'nope'", diagnostics[1].Message);
    }

    // ---- platform defaults and labels -------------------------------------------------------------------------

    [Theory]
    [InlineData("cmd+c", TerminalAction.Copy)]
    [InlineData("cmd+v", TerminalAction.Paste)]
    [InlineData("cmd+t", TerminalAction.NewTab)]
    [InlineData("cmd+w", TerminalAction.ClosePane)]
    [InlineData("cmd+q", TerminalAction.Quit)]
    [InlineData("cmd+f", TerminalAction.Search)]
    [InlineData("cmd+k", TerminalAction.ClearScrollback)]
    [InlineData("cmd+plus", TerminalAction.ZoomIn)]
    [InlineData("cmd+minus", TerminalAction.ZoomOut)]
    [InlineData("cmd+0", TerminalAction.ResetZoom)]
    [InlineData("cmd+[", TerminalAction.PreviousTab)]
    [InlineData("cmd+]", TerminalAction.NextTab)]
    [InlineData("cmd+1", TerminalAction.SwitchTab1)]
    [InlineData("cmd+9", TerminalAction.SwitchTab9)]
    [InlineData("cmd+enter", TerminalAction.ToggleFullscreen)]
    [InlineData("cmd+d", TerminalAction.SplitVertical)]
    [InlineData("cmd+shift+d", TerminalAction.SplitHorizontal)]
    public void MacDefaults_AddTheCommandSet_OnlyOnMac(string chord, TerminalAction expected)
    {
        Assert.Contains(new KeybindingManager(KeybindingPlatform.MacOS).GetBindings(),
            b => b.Key == KeybindingManager.NormalizeChord(chord) && b.Value.Action == expected);
        Assert.DoesNotContain(new KeybindingManager(KeybindingPlatform.Linux).GetBindings(),
            b => b.Key == KeybindingManager.NormalizeChord(chord));
    }

    [Fact]
    public void MacDefaults_AreAdditive()
    {
        var linux = new KeybindingManager(KeybindingPlatform.Linux).GetBindings();
        var mac = new KeybindingManager(KeybindingPlatform.MacOS).GetBindings();
        Assert.Empty(linux.Except(mac));
    }

    [Theory]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.Copy, "Ctrl+Shift+C")]
    [InlineData(KeybindingPlatform.MacOS, TerminalAction.Copy, "Cmd+C")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.SplitVertical, "Ctrl+Shift+D")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.SplitHorizontal, "Ctrl+Shift+S")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.SelectAll, "Ctrl+Shift+A")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.PreviousPrompt, "Ctrl+Shift+PageUp")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.CopyCommandOutput, "Ctrl+Alt+O")]
    [InlineData(KeybindingPlatform.MacOS, TerminalAction.CopyCommandOutput, "Ctrl+Option+O")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.ZoomIn, "Ctrl+=")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.ToggleFullscreen, "F11")]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.Clear, null)]
    [InlineData(KeybindingPlatform.Linux, TerminalAction.CloseTab, null)]
    public void GetShortcutLabel_PicksThePreferredChord(KeybindingPlatform platform, TerminalAction action, string? expected)
    {
        Assert.Equal(expected, new KeybindingManager(platform).GetShortcutLabel(action));
    }

    [Fact]
    public void GetShortcutLabel_FollowsCustomBindings()
    {
        var manager = new KeybindingManager(KeybindingPlatform.Linux);
        manager.ApplyCustomBindings(new Dictionary<string, string> { ["ctrl+shift+c"] = "none", ["ctrl+insert"] = "copy", ["ctrl+k"] = "clear" });
        Assert.Equal("Ctrl+Insert", manager.GetShortcutLabel(TerminalAction.Copy));
        Assert.Equal("Ctrl+K", manager.GetShortcutLabel(TerminalAction.Clear));
    }

    /// <summary>Which action each default menu item runs (the menus' click handlers are wired to these actions).</summary>
    private static readonly Dictionary<string, TerminalAction> MenuActions = new()
    {
        ["tab.split_right"] = TerminalAction.SplitVertical,
        ["tab.split_down"] = TerminalAction.SplitHorizontal,
        ["tab.new"] = TerminalAction.NewTab,
        ["tab.close"] = TerminalAction.CloseTab,
        ["terminal.copy"] = TerminalAction.Copy,
        ["terminal.copy_command_output"] = TerminalAction.CopyCommandOutput,
        ["terminal.previous_prompt"] = TerminalAction.PreviousPrompt,
        ["terminal.next_prompt"] = TerminalAction.NextPrompt,
        ["terminal.paste"] = TerminalAction.Paste,
        ["terminal.select_all"] = TerminalAction.SelectAll,
        ["terminal.split_right"] = TerminalAction.SplitVertical,
        ["terminal.split_down"] = TerminalAction.SplitHorizontal,
        ["terminal.clear"] = TerminalAction.Clear,
    };

    private static IEnumerable<ContextMenuItem> AllMenuItems(Func<TerminalAction, string?> shortcutFor) =>
        DefaultContextMenus.BuildTabMenu(() => { }, () => { }, () => { }, () => { }, shortcutFor)
            .Concat(DefaultContextMenus.BuildTerminalMenu(true, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                true, () => { }, true, () => { }, () => { }, shortcutFor))
            .Where(item => !item.IsSeparator);

    [Theory]
    [InlineData(KeybindingPlatform.Linux)]
    [InlineData(KeybindingPlatform.MacOS)]
    public void MenuShortcuts_EqualTheLiveBindingLabels(KeybindingPlatform platform)
    {
        var manager = new KeybindingManager(platform);
        manager.ApplyCustomBindings(new Dictionary<string, string> { ["ctrl+k"] = "clear", ["ctrl+shift+d"] = "none" });
        var items = AllMenuItems(manager.GetShortcutLabel).ToArray();
        Assert.Equal(MenuActions.Count, items.Length);
        foreach (var item in items)
            Assert.Equal(manager.GetShortcutLabel(MenuActions[item.Id]), item.Shortcut);
        // The four labels that used to be literals (D17) now follow the table.
        Assert.Equal("Ctrl+K", items.First(i => i.Id == "terminal.clear").Shortcut);
        Assert.Equal(platform == KeybindingPlatform.MacOS ? "Cmd+A" : "Ctrl+Shift+A", items.First(i => i.Id == "terminal.select_all").Shortcut);
        Assert.Equal(platform == KeybindingPlatform.MacOS ? "Cmd+D" : null, items.First(i => i.Id == "terminal.split_right").Shortcut);
    }

    [Fact]
    public void MenuItems_AskForTheActionTheyRun()
    {
        var asked = new List<TerminalAction>();
        var items = AllMenuItems(action => { asked.Add(action); return "#" + action; }).ToArray();
        foreach (var item in items)
            Assert.Equal("#" + MenuActions[item.Id], item.Shortcut);
    }
}
