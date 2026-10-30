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
            "Font size in pixels before display scaling (not points); clamped to 1-512, non-finite values use the built-in default."),
        Opt("font.lineHeight", "font.line_height", "number", null, c => c.Font.LineHeight,
            "Line-height multiplier; values below 0.1 are clamped."),
        Opt("font.symbolMap", "font.symbol_map", "string[]", null, c => c.Font.SymbolMap,
            "Per-range font overrides, one per entry: 'U+2500-U+257F: Preferred Font, Fallback Font'; the first installed family containing the glyph wins."),

        Opt("window.decorations", "window.decorations", "string", ["custom", "native"], c => c.Window.Decorations,
            "Windows only: 'custom' draws the tab-bar caption controls instead of the native title bar; other platforms always keep native decorations."),
        Opt("window.opacity", "window.opacity", "number", null, c => c.Window.Opacity,
            "Window opacity. Accepted but not yet applied: the window is always opaque."),
        Opt("window.title", "window.title", "string", null, c => c.Window.Title,
            "Window title. Accepted but not yet applied: the title follows the active tab."),
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
            "Split-pane divider thickness. Accepted but not yet applied: splits use a fixed 2-pixel gap and no divider is drawn."),
        Opt("panes.activeBorder", "panes.active_border", "boolean", null, c => c.Panes.ActiveBorder,
            "Draw a border around the active pane. Accepted but not yet applied: no active-pane border is drawn."),

        Opt("keybindings", "keybindings", "map", null, c => c.Keybindings,
            "Map of chord (ctrl/shift/alt/super plus a key, e.g. 'ctrl+shift+t') to a TerminalAction name such as NewTab or Copy; 'None' unbinds a chord. Entries are added to the built-in bindings, which are not part of this default."),

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
        Opt("mouse.middleClickAction", "mouse.middle_click_action", "string", ["primary-paste", "clipboard-paste", "ignore"], c => c.Mouse.MiddleClickAction,
            "What a middle-click in a pane does when the application is not capturing the mouse: paste the primary selection, paste the clipboard, or nothing. Unknown values mean clipboard-paste.",
            "Read live; takes effect for the next click."),

        Opt("shellIntegration", "shell_integration", "string", ["detect", "none"], c => c.ShellIntegration,
            "Automatically inject Dotty's shell-integration scripts (OSC 7 / OSC 133) into recognized interactive shells: 'detect' injects, 'none' never does; any other value is treated as none. An explicit command and the DOTTY_SHELL_INTEGRATION opt-out are never overridden.",
            "Applies to new tabs and panes only."),

        Opt("scrollback.limitLines", "scrollback.limit_lines", "integer", null, c => c.Scrollback.LimitLines,
            "Maximum history lines kept per session; 0 keeps no history. Clamped to at most 1000000; negative values fall back to the default. The first of this and limitBytes to be reached applies.",
            "Applies to sessions created after the change; existing tabs keep their capacity."),
        Opt("scrollback.limitBytes", "scrollback.limit_bytes", "integer", null, c => c.Scrollback.LimitBytes,
            "Maximum raw history ring bytes per session, counted as (lines + rows) * columns * 12; 0 means no byte cap. Non-zero values below 1048576 and negative values fall back to the default. The first of this and limitLines to be reached applies.",
            "Applies to sessions created after the change; existing tabs keep their capacity."),

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
        Opt("vt.windowResizeAllowed", "vt.window_resize_allowed", "boolean", null, c => c.Vt.WindowResizeAllowed,
            "Let applications resize the window with CSI 8 ; rows ; cols t (and CSI Ps t with Ps >= 24). Ignored with several tabs, split panes, or a maximized, fullscreen or minimized window.",
            "Read live; takes effect for the next request."),
    ];
}
