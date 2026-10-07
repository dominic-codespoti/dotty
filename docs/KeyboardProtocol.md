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

Native GLFW key callbacks preserve physical key identity and scancode, and native action 1/2/0 drives press/repeat/release without software key-repeat timers. Committed Unicode callbacks preserve each delivered scalar, including supplementary scalars; this does not implement full IME composition or preedit text. GLFW-provided Caps Lock and Num Lock state is encoded as Kitty modifier bits 64 and 128. Key bindings and Dotty UI shortcuts consume their keys before terminal encoding, including the matching release. Committed text is associated with the most recently active held physical key where possible.

## Device Attributes

The parser distinguishes the three request identities:

- `CSI c` and `CSI ? c`: DA1; Dotty returns its conservative VT100 capability response `CSI ? 1 ; 0 c`.
- `CSI > c`: DA2; Dotty returns its configured terminal identity response.
- `CSI = c`: DA3; unsupported, so Dotty sends no identity response.

This keeps DA2 identity replies separate from Kitty keyboard capability negotiation. Device Attributes are not the keyboard-protocol support query; Kitty clients should use `CSI ? u`.

## Compatibility behavior

With no Kitty flags enabled, key presses retain xterm-compatible legacy encoding and plain committed text remains UTF-8. The F25 key maps to Kitty codepoint 57388 and legacy `CSI 46~`. Existing application cursor, keypad application, focus-reporting, and mouse modes remain independent. Applications can probe the actual running terminal with `CSI ? u` and DA requests; a probe must read replies from the PTY rather than infer support from the executable name.

## Mode queries and modifyOtherKeys

DECRQM (`CSI ? Ps $ p` private, `CSI Ps $ p` ANSI) reports `1` (set) or `2` (reset) for modes Dotty tracks — DECCKM 1, DECOM 6, private DECAWM `CSI ? 7 $ p`, DECTCEM 25, mouse 1000/1002/1003/1006, focus 1004, alt-screen 1049, bracketed paste 2004, synchronized update 2026 — and `0` (not recognized) for everything else. ANSI `CSI 7 $ p` is not recognized and reports `0`. DA3 (`CSI = c`) stays intentionally silent.

XTerm modifyOtherKeys is set with `CSI > 4 ; Pv m` (levels 0-2). `CSI > 4 m`, `CSI > m`, and RIS reset the level to 0. Other `CSI > Pp ; Pv m` forms are ignored and never reach SGR. Level 0 uses legacy encoding only. Level 1 reports modified ordinary keys only when they have no legacy encoding (for example, Ctrl+Comma becomes `CSI 27 ; 5 ; 44 ~`, while Ctrl+C stays `0x03`). Level 2 reports all modified ordinary keys (letters, digits, punctuation, and Space) with Ctrl and/or Alt as `CSI 27 ; mod ; code ~`, where mod = 1 + Shift(1) + Alt(2) + Ctrl(4); for example, Ctrl+C becomes `CSI 27 ; 5 ; 99 ~` and Ctrl+Shift+A becomes `CSI 27 ; 6 ; 65 ~`. Enter, Tab, Escape, Backspace, navigation/function keys, and keypad keys keep their existing encodings and never use the `CSI 27` form. When Kitty keyboard flags select a key, the Kitty encoding takes precedence.
