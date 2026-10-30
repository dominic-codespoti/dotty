# Keyboard Protocol and Compatibility

## Negotiated Kitty keyboard protocol

Dotty accepts Kitty keyboard negotiation in the CSI `=`, `>`, `<`, and `?` forms. Negotiation state is scoped independently to the main and alternate screens and is reset by RIS (`ESC c`). The bounded push stack stores the current flags; a pop restores the prior setting (or flags zero when empty).

- `CSI = flags ; mode u` sets flags. Mode 1 replaces the flag set, mode 2 adds selected flags, and mode 3 removes selected flags. An omitted mode is 1; other modes leave the current setting unchanged.
- `CSI > flags u` pushes the current state and sets the supplied flags.
- `CSI < count u` pops `count` states; an omitted count is 1.
- `CSI ? u` queries the active screen and replies `CSI ? flags u`.

Supported flags are 1 (disambiguate keys), 2 (report press/repeat/release event types), 4 (report shifted/base alternate key data when available), 8 (report all keys as escape sequences), and 16 (associate committed text with encoded press/repeat events). Kitty fields are emitted in key-code, modifier/phase, then text order; associated text is not repeated on key release. Unselected ordinary text remains UTF-8 text input; unselected keys continue using legacy terminal sequences. Shifted-layout text is reported when known, while the physical key's base-layout codepoint is reported independently when it differs from the primary codepoint.
Committed scalars without a physical-key association use key code 0 and an empty modifier field (for example, `CSI 0;;229u`); this also preserves additional scalars committed by one physical key. C0 and C1 controls are not emitted as associated text codepoints.
XTGETTCAP requests use `DCS + q <hex>[;<hex>...] ST`, with one or more semicolon-separated hex-encoded names. Dotty replies once per recognized name with `DCS 1 + r <hexName>=<hexValue> ST`; every value is hex-encoded. Recognized names are `TN`, `Co`, `RGB`, and `kitty-keyboard`. Unknown or malformed names get no reply so applications fall back instead of trusting an unimplemented claim. A string aborted by CAN or SUB is never answered.

Native GLFW key callbacks preserve physical key identity and scancode, and native action 1/2/0 drives press/repeat/release without software key-repeat timers. Characters arrive through GLFW's char-mods callback, which (unlike the plain char callback) also carries the characters of Alt chords; each delivered scalar is preserved, including supplementary scalars. IME preedit and composition use a separate native path described in [Native desktop and IME](NativeDesktopAndIme.md#native-ime-implementation). GLFW-provided Caps Lock and Num Lock state is encoded as Kitty modifier bits 64 and 128. Key bindings and Dotty UI shortcuts consume their keys before terminal encoding, including the matching release. Committed text is associated with the most recently active held physical key where possible.

## Key-text routing (Alt, AltGr, Option, Ctrl, Super)

The roles of the two Alt/Option keys come from [`keyboard.leftAlt` and `keyboard.rightAlt`](Configuration.md#keyboard) (`auto`, `meta`, `compose`):

- A **compose** Alt (AltGr, Option) is not a modifier for bindings or encoding. Its press is not consumed; the character the layout composes is typed as text (legacy), reported as associated text (Kitty flag 16), or inserted into the search query. With `auto`, Right Alt composes when the layout uses it as AltGr (X11/Wayland ISO Level 3 sets no Alt bit; Windows reports Ctrl+Alt with no physical Ctrl key, and that synthetic Ctrl is ignored) and is Meta otherwise.
- A **meta** Alt is the terminal's Meta modifier. A legacy Alt chord on a text key waits for the character the platform delivers in the same event batch and sends `ESC` + that character, so Shift is kept (Alt+Shift+, = `ESC <`, Alt+Shift+A = `ESC A`). When no character arrives by the end of the batch, or the delivered character was composed by the layout (macOS Option, AltGr layouts), the key's unshifted name is used, upper-cased for letters when Shift is held.
- Ctrl chords are encoded from the key. When the layout character has no control mapping (non-Latin layouts), the physical US-position key is used: Ctrl+С on a Russian layout sends `0x03`.
- Characters produced while Super/Command is held are not sent; Super chords are shortcuts only.

## Device Attributes

The parser distinguishes the three request identities:

- `CSI c` and `CSI ? c`: DA1; Dotty returns its conservative VT100 capability response `CSI ? 1 ; 0 c`.
- `CSI > c`: DA2; Dotty returns its configured terminal identity response.
- `CSI = c`: DA3; unsupported, so Dotty sends no identity response.

This keeps DA2 identity replies separate from Kitty keyboard capability negotiation. Device Attributes are not the keyboard-protocol support query; Kitty clients should use `CSI ? u`.

## Compatibility behavior

With no Kitty flags enabled, key presses retain xterm-compatible legacy encoding and plain committed text remains UTF-8. The F25 key maps to Kitty codepoint 57388 and legacy `CSI 46~`. Existing application cursor, keypad application, focus-reporting, and mouse modes remain independent. Applications can probe the actual running terminal with `CSI ? u` and DA requests; a probe must read replies from the PTY rather than infer support from the executable name.

Legacy encoding details (the full table is `LegacyKeyEncodingTableTests`):

- Cursor keys, Home, End, and keypad Begin send `SS3 A/B/C/D/H/F/E` in application cursor mode (DECCKM) when unmodified and `CSI x` otherwise; with modifiers they always send `CSI 1 ; m x` (m = 1 + Shift 1 + Alt 2 + Ctrl 4 + Super 8). Insert, Delete, Page Up and Page Down send `CSI n ~` / `CSI n ; m ~`.
- Keypad: with Num Lock on, keypad keys type their character, or the `SS3` keypad codes in application keypad mode (DECKPAM), which also apply when Caps Lock or Num Lock is on. With Num Lock off the digits and decimal are navigation keys (KP7 Home, KP8 Up, KP9 Page Up, KP4 Left, KP5 Begin, KP6 Right, KP1 End, KP2 Down, KP3 Page Down, KP0 Insert, decimal Delete) encoded like the editing keys. macOS has no Num Lock, so its keypad always types digits.
- Ctrl maps letters to C0 and Space/2 to NUL, 3/`[` to ESC, 4/`\` to FS, 5/`]` to GS, 6/`` ` `` to RS, 7/`-`/`/` to US, 8 to DEL (the kitty "Legacy ctrl mapping"); Ctrl+Alt prefixes the same byte with ESC. Alt+Shift+Tab sends `ESC CSI Z`.
- A key that produces input returns a scrolled pane to the live screen and clears its selection; bare modifier, lock and Menu presses do neither. Shift+arrows/Home/End are sent to the application; scrollback uses the `ScrollPageUp`/`ScrollLineUp`/… actions (`shift+pageup`, `ctrl+shift+up`, …; see [`keybindings`](Configuration.md#keybindings)).

## DECBKM (backarrow key mode, 67)

`CSI ? 67 h` makes Backspace send BS (`0x08`); `CSI ? 67 l` (the default) makes it send DEL (`0x7F`). Alt+Backspace sends the same byte prefixed with ESC (`ESC 0x08` or `ESC 0x7F`). Ctrl+Backspace (`0x17`) and Ctrl+Alt+Backspace (`ESC 0x17`) do not depend on the mode. The mode only affects legacy encoding: when Kitty flags select Backspace for CSI-u reporting (disambiguate or report-all-keys), the key is sent as `CSI 127 u` regardless of DECBKM. DECRQM `CSI ? 67 $ p` reports the live state. RIS resets it to DEL; DECSTR leaves it unchanged. The key dispatcher passes `adapter.BackarrowSendsBackspace` to the encoder on each key press.

## Mode queries and modifyOtherKeys

DECRQM (`CSI ? Ps $ p` private, `CSI Ps $ p` ANSI) answers every mode in a complete status table (`1` set, `2` reset, `3` permanently set, `4` permanently reset, `0` not recognized). Keyboard-related entries: DECCKM 1, DECNKM 66 and DECBKM 67 report their live state, and `CSI ? 2 $ p` (DECANM) reports `3`. The full table is in [Parsing](Parsing.md#decrqm-status-table). DA3 (`CSI = c`) stays intentionally silent.

DECSTR (`CSI ! p`) resets DECCKM, the keypad mode (DECKPAM/DECNKM) and the modifyOtherKeys level to 0, but not DECBKM or the Kitty flags; RIS (`ESC c`) resets all of them. See [DECSTR and RIS](Parsing.md#decstr-and-ris-reset-sets).

XTerm modifyOtherKeys is set with `CSI > 4 ; Pv m` (levels 0-2). `CSI > 4 m`, `CSI > m`, and RIS reset the level to 0. Other `CSI > Pp ; Pv m` forms are ignored and never reach SGR. Level 0 uses legacy encoding only. Level 1 reports modified ordinary keys only when they have no legacy encoding (for example, Ctrl+Comma becomes `CSI 27 ; 5 ; 44 ~`, while Ctrl+C stays `0x03`). Level 2 reports all modified ordinary keys (letters, digits, punctuation, and Space) with Ctrl and/or Alt as `CSI 27 ; mod ; code ~`, where mod = 1 + Shift(1) + Alt(2) + Ctrl(4); for example, Ctrl+C becomes `CSI 27 ; 5 ; 99 ~` and Ctrl+Shift+A becomes `CSI 27 ; 6 ; 65 ~`. Enter, Tab, Escape, Backspace, navigation/function keys, and keypad keys keep their existing encodings and never use the `CSI 27` form. When Kitty keyboard flags select a key, the Kitty encoding takes precedence.
