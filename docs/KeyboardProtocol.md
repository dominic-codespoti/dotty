# Keyboard Protocol and Compatibility

## Negotiated Kitty keyboard protocol

Dotty accepts Kitty keyboard negotiation in the CSI `=`, `>`, `<`, and `?` forms. Negotiation state is scoped independently to the main and alternate screens and is reset by RIS (`ESC c`). The bounded push stack stores the current flags; a pop restores the prior setting (or flags zero when empty).

- `CSI = flags ; mode u` sets flags. Mode 1 replaces the flag set, mode 2 adds selected flags, and mode 3 removes selected flags. An omitted mode is 1; other modes leave the current setting unchanged.
- `CSI > flags u` pushes the current state and sets the supplied flags.
- `CSI < count u` pops `count` states; an omitted count is 1.
- `CSI ? u` queries the active screen and replies `CSI ? flags u`.

Supported flags are 1 (disambiguate keys), 2 (report press/repeat/release event types), 4 (report shifted/base alternate key data when available), 8 (report all keys as escape sequences), and 16 (associate committed text with encoded press/repeat events). Kitty fields are emitted in key-code, modifier/phase, then text order; associated text is not repeated on key release. Unselected ordinary text remains UTF-8 text input; unselected keys continue using legacy terminal sequences. Shifted-layout text is reported when known, while the physical key's base-layout codepoint is reported independently when it differs from the primary codepoint.
Committed scalars without a physical-key association use key code 0 and an empty modifier field (for example, `CSI 0;;229u`); this also preserves additional scalars committed by one physical key. C0 and C1 controls are not emitted as associated text codepoints.
Unsupported DCS strings are consumed through ST or canceled by CAN/SUB without being displayed; Dotty does not implement XTGETTCAP capability replies.

Native GLFW key callbacks preserve physical key identity and scancode, and native action 1/2/0 drives press/repeat/release without software key-repeat timers. Committed Unicode callbacks preserve each delivered scalar, including supplementary scalars; this does not implement full IME composition or preedit text. GLFW-provided Caps Lock and Num Lock state is encoded as Kitty modifier bits 64 and 128. Key bindings and Dotty UI shortcuts consume their keys before terminal encoding, including the matching release. Committed text is associated with the most recently active held physical key where possible.

## Device Attributes

The parser distinguishes the three request identities:

- `CSI c` and `CSI ? c`: DA1; Dotty returns its conservative VT100 capability response `CSI ? 1 ; 0 c`.
- `CSI > c`: DA2; Dotty returns its configured terminal identity response.
- `CSI = c`: DA3; unsupported, so Dotty sends no identity response.

This keeps DA2 identity replies separate from Kitty keyboard capability negotiation. Device Attributes are not the keyboard-protocol support query; Kitty clients should use `CSI ? u`.

## Compatibility behavior

With no Kitty flags enabled, key presses retain xterm-compatible legacy encoding and plain committed text remains UTF-8. The F25 key maps to Kitty codepoint 57388 and legacy `CSI 46~`. Existing application cursor, keypad application, focus-reporting, and mouse modes remain independent. Applications can probe the actual running terminal with `CSI ? u` and DA requests; a probe must read replies from the PTY rather than infer support from the executable name.
