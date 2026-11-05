using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dotty.Runtime.Config;

/// <summary>
/// One documented configuration option.
/// </summary>
/// <param name="JsonPath">Dotted path in config.json, e.g. <c>mouse.copyOnSelect</c>.</param>
/// <param name="LuaKey">Dotted key under <c>dotty.config</c>, e.g. <c>mouse.copy_on_select</c>.</param>
/// <param name="Type">Human-readable value type (<c>string</c>, <c>number</c>, <c>integer</c>, <c>boolean</c>, <c>string[]</c>, <c>map</c>).</param>
/// <param name="Values">Allowed values for enumerated options, otherwise null.</param>
/// <param name="Default">Formats the default; live entries read it from <c>new DottyUserConfig()</c>.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Scope">Optional note on when a change takes effect.</param>
public sealed record ConfigOption(
    string JsonPath,
    string LuaKey,
    string Type,
    IReadOnlyList<string>? Values,
    Func<string> Default,
    string Description,
    string? Scope = null);

/// <summary>
/// The single catalog of user-facing configuration options behind <c>dotty +explain-config</c>.
/// Defaults of ordinary entries are read from a live <see cref="DottyUserConfig"/> through getter
/// delegates, so the printed default cannot drift from the code. Adding a property to
/// <see cref="DottyUserConfig"/> (or a key to the Lua registry) without an entry here fails
/// <c>ConfigCatalogTests</c>. Everything here is reflection-free (NativeAOT safe).
/// </summary>
public static class ConfigOptionCatalog
{
    // Must stay the first static field: the Opt(...) getters in CreateLive() read it.
    private static readonly DottyUserConfig DefaultConfig = new();

    private static readonly IReadOnlyList<ConfigOption> Live = CreateLive();

    /// <summary>Every documented option, ordered by JSON path (ordinal).</summary>
    public static readonly IReadOnlyList<ConfigOption> All = CreateAll();

    /// <summary>
    /// Finds one option by JSON path, Lua key, or an unambiguous last path segment
    /// (<c>copy-on-select</c> for <c>mouse.copyOnSelect</c>). Case, <c>-</c> and <c>_</c> are ignored.
    /// </summary>
    public static ConfigOption? Find(string query)
    {
        string wanted = Normalize(query);
        if (wanted.Length == 0)
            return null;

        foreach (ConfigOption option in All)
        {
            if (Normalize(option.JsonPath) == wanted || Normalize(option.LuaKey) == wanted)
                return option;
        }

        ConfigOption? match = null;
        foreach (ConfigOption option in All)
        {
            if (Normalize(LastSegment(option.JsonPath)) != wanted)
                continue;
            if (match is not null)
                return null; // ambiguous
            match = option;
        }
        return match;
    }

    /// <summary>All options under a group path such as <c>mouse</c> or <c>window.padding</c>.</summary>
    public static IReadOnlyList<ConfigOption> FindGroup(string query)
    {
        string prefix = Normalize(query).TrimEnd('.');
        var result = new List<ConfigOption>();
        if (prefix.Length == 0)
            return result;
        prefix += ".";
        foreach (ConfigOption option in All)
        {
            if (Normalize(option.JsonPath).StartsWith(prefix, StringComparison.Ordinal)
                || Normalize(option.LuaKey).StartsWith(prefix, StringComparison.Ordinal))
                result.Add(option);
        }
        return result;
    }

    /// <summary>Up to <paramref name="max"/> options whose path contains the query (or the reverse); used for "did you mean".</summary>
    public static IReadOnlyList<ConfigOption> Suggest(string query, int max = 5)
    {
        string wanted = Normalize(query);
        var result = new List<ConfigOption>();
        if (wanted.Length == 0)
            return result;
        foreach (ConfigOption option in All)
        {
            string path = Normalize(option.JsonPath);
            string leaf = Normalize(LastSegment(option.JsonPath));
            if (path.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(leaf, StringComparison.Ordinal))
            {
                result.Add(option);
                if (result.Count == max)
                    break;
            }
        }
        return result;
    }

    /// <summary>Full documentation for one option. Lines are separated by <c>\n</c>; no trailing newline.</summary>
    public static string Render(ConfigOption option)
    {
        var sb = new StringBuilder();
        sb.Append(option.JsonPath).Append('\n');
        AppendField(sb, "Lua key", option.LuaKey);
        AppendField(sb, "Type", option.Type);
        AppendField(sb, "Default", option.Default());
        if (option.Values is { Count: > 0 })
            AppendField(sb, "Values", string.Join(", ", option.Values));
        if (option.Scope is not null)
            AppendField(sb, "Scope", option.Scope);
        sb.Append('\n').Append(option.Description);
        return sb.ToString();
    }

    /// <summary>One line per option: path, then description. Every line ends with <c>\n</c>.</summary>
    public static string RenderList(IReadOnlyList<ConfigOption> options)
    {
        int width = 0;
        foreach (ConfigOption option in options)
            width = Math.Max(width, option.JsonPath.Length);

        var sb = new StringBuilder();
        foreach (ConfigOption option in options)
            sb.Append(option.JsonPath.PadRight(width)).Append("  ").Append(option.Description).Append('\n');
        return sb.ToString();
    }

    /// <summary>Formats a default value the way the catalog prints it.</summary>
    public static string FormatDefault(object? value) => value switch
    {
        null => "(unset)",
        string s => Quote(s),
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        List<string> list => FormatList(list),
        Dictionary<string, string> map => FormatMap(map),
        _ => throw new ArgumentException($"Unsupported config value type '{value.GetType().Name}'.", nameof(value)),
    };

    private static void AppendField(StringBuilder sb, string label, string value) =>
        sb.Append((label + ":").PadRight(9)).Append(value).Append('\n');

    private static string LastSegment(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Trim())
        {
            if (c is '-' or '_')
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string FormatList(List<string> list)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(Quote(list[i]));
        }
        return sb.Append(']').ToString();
    }

    private static string FormatMap(Dictionary<string, string> map)
    {
        if (map.Count == 0)
            return "{}";
        var sb = new StringBuilder("{ ");
        bool first = true;
        foreach (KeyValuePair<string, string> pair in map)
        {
            if (!first)
                sb.Append(", ");
            first = false;
            sb.Append(Quote(pair.Key)).Append(": ").Append(Quote(pair.Value));
        }
        return sb.Append(" }").ToString();
    }

    private static IReadOnlyList<ConfigOption> CreateAll()
    {
        var all = new List<ConfigOption>(Live);
        all.Sort(static (a, b) => string.CompareOrdinal(a.JsonPath, b.JsonPath));
        return all;
    }

    private static ConfigOption Opt<T>(
        string jsonPath,
        string luaKey,
        string type,
        string[]? values,
        Func<DottyUserConfig, T> read,
        string description,
        string? scope = null) =>
        new(jsonPath, luaKey, type, values, () => FormatDefault(read(DefaultConfig)), description, scope);

    private static IReadOnlyList<ConfigOption> CreateLive() =>
    [
        Opt("theme", "theme", "string", null, c => c.Theme,
            "Built-in or user theme name (user themes load from <config-directory>/themes); the dark fallback when themeAuto is true. The DOTTY_THEME environment variable overrides it."),
        Opt("themeLight", "theme_light", "string", null, c => c.ThemeLight,
            "Theme used when themeAuto is true and the OS reports a light color scheme; LightPlus when unset."),
        Opt("themeDark", "theme_dark", "string", null, c => c.ThemeDark,
            "Theme used when themeAuto is true and the OS reports a dark color scheme; falls back to theme when unset."),
        Opt("themeAuto", "theme_auto", "boolean", null, c => c.ThemeAuto,
            "Follow the OS light/dark color scheme, selecting themeDark (or theme) or themeLight."),
        Opt("selectionColor", "selection_color", "string", null, c => c.SelectionColor,
            "Selection highlight color as a hex string (for example #3385DB); the built-in translucent blue when unset or invalid."),

        Opt("font.family", "font.family", "string", null, c => c.Font.Family,
            "Comma-separated font family names; the first installed family wins."),
        Opt("font.size", "font.size", "number", null, c => c.Font.Size,
            "Font size in points (pixels = points x 96/72 x display scale; default 11.25 = 15 px at 100 %); clamped to 1-512, non-finite values use the default."),
        Opt("font.lineHeight", "font.line_height", "number", null, c => c.Font.LineHeight,
            "Line-height multiplier; values below 0.1 are clamped."),
        Opt("font.adjustCellWidth", "font.adjust_cell_width", "string", null, c => c.Font.AdjustCellWidth,
            "Cell width adjustment: signed logical pixels ('1', '-0.5') or percent ('10%'), at most 100 either way; empty or malformed keeps the measured width."),
        Opt("font.adjustCellHeight", "font.adjust_cell_height", "string", null, c => c.Font.AdjustCellHeight,
            "Cell height adjustment: signed logical pixels ('2') or percent ('-5%'), at most 100 either way; the change is split above and below the text."),
        Opt("font.symbolMap", "font.symbol_map", "string[]", null, c => c.Font.SymbolMap,
            "Per-range font overrides, one per entry: 'U+2500-U+257F: Preferred Font, Fallback Font'; the first installed family containing the glyph wins."),
        Opt("font.faintOpacity", "font.faint_opacity", "number", null, c => c.Font.FaintOpacity,
            "Share of its own colour that faint (SGR 2) text keeps when mixed toward the cell background; clamped to 0-1, non-finite values use 0.5."),
        Opt("font.builtinGlyphs", "font.builtin_glyphs", "boolean", null, c => c.Font.BuiltinGlyphs,
            "Draw box drawing (U+2500-257F), block elements (U+2580-259F), braille (U+2800-28FF), sextants (U+1FB00-1FB3B), octants (U+1CD00-1CDE5) and Powerline (U+E0B0-E0BF) as pixel-exact built-in sprites instead of font glyphs."),
        Opt("font.boldFamily", "font.bold_family", "string", null, c => c.Font.BoldFamily,
            "Comma-separated families for bold text in the primary font; empty uses the primary family's bold face, synthetic bold when it has none."),
        Opt("font.italicFamily", "font.italic_family", "string", null, c => c.Font.ItalicFamily,
            "Comma-separated families for italic text in the primary font; empty uses the primary family's italic face, synthetic slant when it has none."),
        Opt("font.boldItalicFamily", "font.bold_italic_family", "string", null, c => c.Font.BoldItalicFamily,
            "Comma-separated families for bold italic text in the primary font; empty uses the primary family's bold italic face, synthesized when missing."),
        Opt("font.weight", "font.weight", "string", null, c => c.Font.Weight,
            "Weight of regular text: thin, extralight, light, regular, medium, semibold, bold, extrabold, black or a number 1-1000; bold text uses 700 (900 from semibold up)."),
        Opt("font.features", "font.features", "string[]", null, c => c.Font.Features,
            "OpenType feature settings for shaping: '+calt', '-liga', 'ss01', 'cv01=2'; a tag is four letters or digits, later settings of a tag win, invalid entries are ignored."),
        Opt("font.ligatures", "font.ligatures", "string", ["never", "cursor", "always"], c => c.Font.Ligatures,
            "Programming ligatures and contextual alternates for runs of ASCII punctuation: 'never', 'cursor' (default; the cell under the cursor is drawn unshaped) or 'always'. Unknown values mean cursor."),
        Opt("font.hinting", "font.hinting", "string", ["none", "slight", "normal", "full"], c => c.Font.Hinting,
            "Outline hinting for cell metrics and glyph rasterisation (one shared policy); 'full' is the historical raster. Case-insensitive; unknown values mean full."),
        Opt("font.antialias", "font.antialias", "string", ["gray", "none"], c => c.Font.Antialias,
            "Glyph coverage: 'gray' (antialiased, the historical raster) or 'none' (aliased, hard edges). Subpixel (LCD) antialiasing is not supported. Unknown values mean gray."),

        Opt("window.decorations", "window.decorations", "string", ["custom", "native"], c => c.Window.Decorations,
            "Windows only: 'custom' draws the tab-bar caption controls instead of the native title bar; other platforms always keep native decorations."),
        Opt("window.opacity", "window.opacity", "number", null, c => c.Window.Opacity,
            "Default-background opacity 0-1, multiplied by the theme opacity; text, chrome and images stay opaque. Going from 1 to below 1 needs a restart."),
        Opt("window.title", "window.title", "string", null, c => c.Window.Title,
            "Initial window title; the title then follows the active tab. Read at startup."),
        Opt("window.columns", "window.columns", "integer", null, c => c.Window.Columns,
            "Initial grid width in cells; 0 uses 80. Read at startup."),
        Opt("window.rows", "window.rows", "integer", null, c => c.Window.Rows,
            "Initial grid height in cells; 0 uses 24. Read at startup."),
        Opt("window.startMaximized", "window.start_maximized", "boolean", null, c => c.Window.StartMaximized,
            "Maximize the window when it is first shown. Read at startup."),
        Opt("window.startFullscreen", "window.start_fullscreen", "boolean", null, c => c.Window.StartFullscreen,
            "Enter fullscreen when the window is first shown; wins over startMaximized. Read at startup."),
        Opt("window.floating", "window.floating", "boolean", null, c => c.Window.Floating,
            "Keep the window above other windows (always on top); unsupported on Wayland, where the compositor controls stacking."),
        Opt("window.padding.left", "window.padding.left", "number", null, c => c.Window.Padding.Left,
            "Left padding around the terminal content."),
        Opt("window.padding.top", "window.padding.top", "number", null, c => c.Window.Padding.Top,
            "Top padding around the terminal content."),
        Opt("window.padding.right", "window.padding.right", "number", null, c => c.Window.Padding.Right,
            "Right padding around the terminal content."),
        Opt("window.padding.bottom", "window.padding.bottom", "number", null, c => c.Window.Padding.Bottom,
            "Bottom padding around the terminal content."),

        Opt("tabBar.show", "tab_bar.show", "boolean", null, c => c.TabBar.Show,
            "Show the tab bar."),
        Opt("tabBar.height", "tab_bar.height", "number", null, c => c.TabBar.Height,
            "Tab strip height."),
        Opt("tabBar.style", "tab_bar.style", "string", ["Pill", "Compact", "Minimal"], c => c.TabBar.Style,
            "Tab bar style; the renderer currently uses one chrome style, so the stored value does not change the appearance."),

        Opt("cursor.shape", "cursor.shape", "string", ["Block", "Beam", "Underline"], c => c.Cursor.Shape,
            "Default cursor shape, used until the application selects one with DECSCUSR (CSI Ps SP q); DECSCUSR 0, DECSTR and RIS return to it."),
        Opt("cursor.blink", "cursor.blink", "boolean", null, c => c.Cursor.Blink,
            "Default cursor blinking, used until the application selects a style with DECSCUSR; blinking stops while the window is unfocused."),
        Opt("cursor.blinkIntervalMs", "cursor.blink_interval_ms", "integer", null, c => c.Cursor.BlinkIntervalMs,
            "Blink interval in milliseconds for the cursor and SGR 5/6 blinking text; values below 100 are raised to 100."),
        Opt("cursor.unfocusedStyle", "cursor.unfocused_style", "string", ["hollow", "same", "hidden"], c => c.Cursor.UnfocusedStyle,
            "Cursor while the window is unfocused: an outline of the cursor cell ('hollow'), the focused shape ('same'), or none ('hidden')."),
        Opt("cursor.text", "cursor.text", "string", ["auto", "cell-foreground"], c => c.Cursor.Text,
            "Text colour inside a block cursor: 'auto' uses the cell background (the cell foreground when the background matches the cursor), 'cell-foreground' keeps the text colour."),

        Opt("panes.dividerThickness", "panes.divider_thickness", "number", null, c => c.Panes.DividerThickness,
            "Gap between split panes in logical pixels, scaled and rounded to whole device pixels and filled with the theme's border colour; 0 removes it, values above 32 are clamped. Read every frame."),
        Opt("panes.activeBorder", "panes.active_border", "boolean", null, c => c.Panes.ActiveBorder,
            "Paint the divider gap around the active pane in the theme's accent colour while the tab has splits. Read every frame."),

        Opt("bell.attention", "bell.attention", "boolean", null, c => c.Bell.Attention,
            "On a terminal bell, request window attention (taskbar flash, dock bounce, urgency hint) while the window is not focused.",
            "Read live; takes effect at the next bell."),
        Opt("bell.visual", "bell.visual", "boolean", null, c => c.Bell.Visual,
            "On a terminal bell, briefly flash the window contents (about 150 ms fade).",
            "Read live; takes effect at the next bell."),
        Opt("bell.audible", "bell.audible", "boolean", null, c => c.Bell.Audible,
            "Play a sound on a terminal bell. Accepted but not yet implemented on any platform: no sound is played."),

        Opt("search.regex", "search.regex", "boolean", null, c => c.Search.Regex,
            "Search overlay starts in regular-expression mode (.NET syntax; an invalid pattern is searched literally and marked). Alt+R toggles it while the overlay is open.",
            "Read when the overlay opens."),
        Opt("search.caseSensitive", "search.case_sensitive", "boolean", null, c => c.Search.CaseSensitive,
            "Search overlay starts case-sensitive. Alt+C toggles it while the overlay is open.",
            "Read when the overlay opens."),
        Opt("search.wholeWord", "search.whole_word", "boolean", null, c => c.Search.WholeWord,
            "Search overlay starts matching whole words only (neither end touches a letter, digit, or underscore). Alt+W toggles it while the overlay is open.",
            "Read when the overlay opens."),

        Opt("drop.mode", "drop.mode", "string", ["paste-path", "ignore"], c => c.Drop.Mode,
            "What dropping files on the window does: 'paste-path' pastes the shell-escaped paths, space-separated, into the active pane through the paste router (bracketed paste and paste confirmation apply); 'ignore' does nothing. Paths with control characters are never pasted. Unknown values mean paste-path.",
            "Read live; takes effect at the next drop."),
        Opt("drop.quote", "drop.quote", "string", ["auto", "posix", "fish", "powershell", "cmd"], c => c.Drop.Quote,
            "Quoting for dropped paths. 'auto' picks from the pane's launch shell (fish, pwsh/powershell, cmd, otherwise posix; cmd on Windows without a shell). posix: bare when safe, else single quotes; fish: single quotes with \\ and \' escapes; powershell: single quotes doubled; cmd: double quotes, and paths containing \", % or ! are refused. Unknown values mean auto.",
            "Read live; takes effect at the next drop."),
        Opt("text.blending", "text.blending", "string", ["native", "linear", "linear-corrected"], c => c.Text.Blending,
            "Glyph coverage blending: 'native' blends in gamma space (the historical look), 'linear' matches linear-light luminance, 'linear-corrected' is linear plus the text.contrast boost. 'native' stays the default until an A/B on real displays; unknown values mean native.",
            "Read every frame."),
        Opt("text.contrast", "text.contrast", "number", null, c => c.Text.Contrast,
            "Coverage boost of 'linear-corrected' blending, 0-1 (stronger for dark text on light backgrounds); ignored by the other modes. Clamped; non-finite values use 0.5.",
            "Read every frame."),
        Opt("text.minimumContrast", "text.minimum_contrast", "number", null, c => c.Text.MinimumContrast,
            "Minimum WCAG contrast ratio of text against its visible background (after inverse, faint and selection), 1-21; the foreground moves toward black or white until it is met. 1 turns it off.",
            "Read every frame."),

        Opt("keybindings", "keybindings", "map", null, c => c.Keybindings,
            "Map of chord ('ctrl+shift+t', 'cmd+k', 'physical:ctrl+shift+c') to an action name such as new_tab, copy or send_text:\\x01; 'none' unbinds a chord. Entries are added to the built-in bindings, which are not part of this default; rejected entries are logged and listed by +validate-config."),
        Opt("keyboard.leftAlt", "keyboard.left_alt", "string", ["auto", "meta", "compose"], c => c.Keyboard.LeftAlt,
            "Left Alt/Option: 'meta' sends Alt chords as ESC-prefixed input and matches alt+ bindings, 'compose' types the character the layout composes (Option on macOS), 'auto' is meta on Linux/Windows and compose on macOS. Case-insensitive; unknown values mean auto.",
            "Read live; takes effect at the next key press."),
        Opt("keyboard.rightAlt", "keyboard.right_alt", "string", ["auto", "meta", "compose"], c => c.Keyboard.RightAlt,
            "Right Alt/Option (AltGr): 'auto' is compose on Linux/Windows when the layout has AltGr and meta when it does not, and compose on macOS; 'meta' and 'compose' force one behavior. Case-insensitive; unknown values mean auto.",
            "Read live; takes effect at the next key press."),

        Opt("clipboard.allowOsc52Write", "clipboard.allow_osc52_write", "boolean", null, c => c.Clipboard.AllowOsc52Write,
            "Let terminal applications write to the system clipboard with OSC 52; a Lua clipboard_write hook can veto but never grant.",
            "Read live; takes effect for the next request."),
        Opt("clipboard.osc52Read", "clipboard.osc52_read", "string", ["deny", "ask", "allow"], c => c.Clipboard.Osc52Read,
            "Policy for OSC 52 clipboard read requests; case-insensitive, any other value is treated as deny. 'allow' lets any program that prints to the terminal read your clipboard.",
            "Read live; takes effect for the next request."),
        Opt("clipboard.osc52ReadLimitBytes", "clipboard.osc52_read_limit_bytes", "integer", null, c => c.Clipboard.Osc52ReadLimitBytes,
            "Largest clipboard (UTF-8 bytes) an OSC 52 read may return; larger clipboards are dropped without a reply. Clamped to 1 through 8388608.",
            "Read live; takes effect for the next request."),
        Opt("clipboard.confirmPaste", "clipboard.confirm_paste", "boolean", null, c => c.Clipboard.ConfirmPaste,
            "Ask before pasting text that contains line breaks, control characters, or a bracketed-paste end marker.",
            "Read live; takes effect for the next paste."),
        Opt("clipboard.bracketedPasteIsSafe", "clipboard.bracketed_paste_is_safe", "boolean", null, c => c.Clipboard.BracketedPasteIsSafe,
            "When the application enabled bracketed paste, multi-line text alone does not need confirmation; control characters and end markers still do.",
            "Read live; takes effect for the next paste."),

        Opt("hyperlinks.osc8", "hyperlinks.osc8", "boolean", null, c => c.Hyperlinks.Osc8,
            "Honor OSC 8 explicit hyperlinks; when false they are ignored at ingest. Detected plain URLs are unaffected."),
        Opt("hyperlinks.confirm", "hyperlinks.confirm", "string", ["risky", "always", "never"], c => c.Hyperlinks.Confirm,
            "When to ask before opening a link; unknown values mean risky. Hard-blocked schemes are refused in every mode."),
        Opt("hyperlinks.allowedSchemes", "hyperlinks.allowed_schemes", "string[]", null, c => c.Hyperlinks.AllowedSchemes,
            "URI schemes that may be opened (case-insensitive); hard-blocked schemes cannot be enabled by listing them."),

        Opt("mouse.copyOnSelect", "mouse.copy_on_select", "string", ["none", "primary", "clipboard", "both"], c => c.Mouse.CopyOnSelect,
            "Where a finished local selection is copied automatically; it does not clear the selection and does not affect middle-click. Case-insensitive; the legacy spellings \"true\" (primary on Linux, clipboard elsewhere) and \"false\" (none) are also accepted as strings, and unknown values mean none.",
            "Selections made after the change."),
        Opt("mouse.scrollMultiplier", "mouse.scroll_multiplier", "number", null, c => c.Mouse.ScrollMultiplier,
            "Lines scrolled per wheel notch, for local scrollback and for alternate-scroll cursor keys; fractional touchpad deltas accumulate until a whole line. Applications that track the mouse get one wheel report per notch regardless. Clamped to 0.01 through 100.",
            "Read live; takes effect for the next wheel event."),
        Opt("mouse.alternateScroll", "mouse.alternate_scroll", "string", ["auto", "off"], c => c.Mouse.AlternateScroll,
            "'auto' sends cursor up/down for the wheel while the alternate screen is shown, no mouse reporting is active and the application has not reset mode 1007 (less, man, editors without mouse support); 'off' never does. Unknown values mean auto.",
            "Read live; takes effect for the next wheel event."),
        Opt("mouse.reportBypassModifier", "mouse.report_bypass_modifier", "string", ["shift", "ctrl", "alt", "super", "none"], c => c.Mouse.ReportBypassModifier,
            "Modifier that, while held, keeps clicks, drags and the wheel local (selection, scrollback) when an application tracks the mouse; 'none' always reports. Unknown values mean shift.",
            "Read live; takes effect for the next pointer event."),
        Opt("mouse.hideWhileTyping", "mouse.hide_while_typing", "boolean", null, c => c.Mouse.HideWhileTyping,
            "Hide the pointer when a key sends input to a pane; it reappears as soon as the mouse moves.",
            "Read live; takes effect at the next key."),
        Opt("mouse.middleClickAction", "mouse.middle_click_action", "string", ["primary-paste", "clipboard-paste", "ignore"], c => c.Mouse.MiddleClickAction,
            "What a middle-click in a pane does when the application is not capturing the mouse: paste the primary selection, paste the clipboard, or nothing. Unknown values mean clipboard-paste.",
            "Read live; takes effect for the next click."),

        Opt("selection.wordChars", "selection.word_chars", "string", null, c => c.Selection.WordChars,
            "Punctuation a double-click keeps inside a word, besides letters, digits, combining marks, '_' and emoji clusters (CJK included). Other punctuation selects as runs of its own; an empty string keeps no punctuation.",
            "Read live; takes effect for the next double-click."),
        Opt("selection.clearOnCopy", "selection.clear_on_copy", "boolean", null, c => c.Selection.ClearOnCopy,
            "Clear the selection after the Copy action put it on the clipboard.",
            "Read live; takes effect for the next copy."),
        Opt("selection.clearOnTyping", "selection.clear_on_typing", "boolean", null, c => c.Selection.ClearOnTyping,
            "Clear the selection when typed text or a key is sent to the pane.",
            "Read live; takes effect for the next key."),
        Opt("selection.rectangleModifier", "selection.rectangle_modifier", "string", ["alt", "ctrl", "shift", "super", "none"], c => c.Selection.RectangleModifier,
            "Modifier that, held when a drag starts, makes a rectangular (block) selection; 'none' disables block selection. Ctrl and Shift also have click meanings (links, extend), which then lose to the block gesture. Unknown values mean alt.",
            "Read live; takes effect for the next drag."),

        Opt("shellIntegration", "shell_integration", "string", ["detect", "none"], c => c.ShellIntegration,
            "Automatically inject Dotty's shell-integration scripts (OSC 7 / OSC 133) into recognized interactive shells: 'detect' injects, 'none' never does; any other value is treated as none. An explicit command and the DOTTY_SHELL_INTEGRATION opt-out are never overridden.",
            "Applies to new tabs and panes only."),
        Opt("term", "term", "string", null, c => c.Term,
            "TERM value for shells and commands Dotty starts: 'xterm-256color', or a terminfo entry such as 'xterm-dotty' (shipped in the packages). When the entry is not found in any terminfo directory (TERMINFO, ~/.terminfo, TERMINFO_DIRS, the system directories, or the bundled terminfo directory), xterm-256color is used. XTGETTCAP TN reports the value actually used.",
            "Applies to new tabs and panes only."),

        Opt("scrollback.limitLines", "scrollback.limit_lines", "integer", null, c => c.Scrollback.LimitLines,
            "Maximum history lines kept per session; 0 keeps no history. Clamped to at most 1000000; negative values fall back to the default. The first of this and limitBytes to be reached applies.",
            "Applies to sessions created after the change; existing tabs keep their capacity."),
        Opt("scrollback.limitBytes", "scrollback.limit_bytes", "integer", null, c => c.Scrollback.LimitBytes,
            "Maximum raw history ring bytes per session, counted as (lines + rows) * columns * 12; 0 means no byte cap. Non-zero values below 1048576 and negative values fall back to the default. The first of this and limitLines to be reached applies.",
            "Applies to sessions created after the change; existing tabs keep their capacity."),

        Opt("printing.outputFile", "printing.output_file", "string", null, c => c.Printing.OutputFile,
            "Append printed terminal text to this file. Null or empty disables printing. CSI 0 i prints the screen or scrolling region according to DECPEX, optionally followed by a form feed according to DECPFF. No external command is executed.",
            "Applied to sessions at creation and on config reload; accepted jobs finish on their original destination."),

        Opt("graphics.kittyProtocol", "graphics.kitty_protocol", "boolean", null, c => c.Graphics.KittyProtocol,
            "Enable the Kitty graphics protocol (APC G): programs may transmit, place and delete inline images. When false the sequences are ignored.",
            "Applied to sessions at creation and on config reload (existing panes included)."),
        Opt("graphics.imageStorageLimitMiB", "graphics.image_storage_limit_mib", "integer", null, c => c.Graphics.ImageStorageLimitMiB,
            "Per-session budget for stored image pixels, in MiB. Non-positive values fall back to the default; clamped to at most 4096.",
            "Applied to sessions at creation and on config reload (existing panes included)."),
        Opt("graphics.maxPayloadMiB", "graphics.max_payload_mib", "integer", null, c => c.Graphics.MaxPayloadMiB,
            "Largest single image transmission accepted, in MiB. Non-positive values fall back to the default; clamped to at most 1024.",
            "Applied to sessions at creation and on config reload (existing panes included)."),
        Opt("graphics.allowFileTransfer", "graphics.allow_file_transfer", "boolean", null, c => c.Graphics.AllowFileTransfer,
            "Let graphics clients transmit images by reading a regular file path (t=f).",
            "Applied to sessions at creation and on config reload (existing panes included)."),
        Opt("graphics.allowTempFileTransfer", "graphics.allow_temp_file_transfer", "boolean", null, c => c.Graphics.AllowTempFileTransfer,
            "Let graphics clients transmit images through a temporary file that the terminal deletes after reading (t=t).",
            "Applied to sessions at creation and on config reload (existing panes included)."),
        Opt("graphics.allowSharedMemory", "graphics.allow_shared_memory", "boolean", null, c => c.Graphics.AllowSharedMemory,
            "Let graphics clients transmit images through a POSIX shared-memory object (t=s).",
            "Applied to sessions at creation and on config reload (existing panes included)."),
        Opt("graphics.windowsPassthrough", "graphics.windows_passthrough", "boolean", null, c => c.Graphics.WindowsPassthrough,
            "Windows only, experimental: create the ConPTY in passthrough mode so graphics sequences are not dropped. May change how ConPTY forwards VT output; verify before enabling. Ignored on other platforms.",
            "Applies to sessions created after the change."),

        Opt("vt.checksumReportEnabled", "vt.checksum_report_enabled", "boolean", null, c => c.Vt.ChecksumReportEnabled,
            "Answer DECRQCRA (CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y) rectangular-area checksum requests; off by default because the reply lets a program read back screen content. When false no reply is sent.",
            "Applied to sessions at creation and on config reload."),
        Opt("vt.titleReportEnabled", "vt.title_report_enabled", "boolean", null, c => c.Vt.TitleReportEnabled,
            "Answer icon-title (CSI 20 t) and window-title (CSI 21 t) report queries; off by default.",
            "Applied to sessions at creation and on config reload."),
        Opt("vt.windowResizeAllowed", "vt.window_resize_allowed", "boolean", null, c => c.Vt.WindowResizeAllowed,
            "Let applications resize the window with CSI 8 ; rows ; cols t (and CSI Ps t with Ps >= 24). Ignored with several tabs, split panes, or a maximized, fullscreen or minimized window.",
            "Read live; takes effect for the next request."),
        Opt("vt.windowControlAllowed", "vt.window_control_allowed", "boolean", null, c => c.Vt.WindowControlAllowed,
            "Let applications move, minimize and restore the native window with CSI 3 ; x ; y t, CSI 2 t and CSI 1 t. Off by default; independent of resize and title-report permissions.",
            "Read live; takes effect for the next request."),
    ];
}
