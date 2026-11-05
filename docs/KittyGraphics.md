# Kitty graphics protocol

Dotty implements the [Kitty graphics protocol](https://sw.kovidgoyal.net/kitty/graphics-protocol/)
(`ESC _ G <control> ; <base64 payload> ESC \`): programs can transmit images,
place them at the cursor, scroll and delete them, animate them, and draw them
through Unicode placeholders.

> **Verification scope (2026-11-05).** Linux smoke checks exercised `kitten icat`
> PNG display and scrolling on native Wayland, and Unicode placeholders and
> ordinary image deletion on X11. Implementation also includes animation, but
> runtime verification of animation, remaining checklist cases, macOS, and
> Windows is still outstanding. No graphics-specific performance claim is made.

## Enabling it (default off)

The protocol is **off by default** (`graphics.kittyProtocol` = `false`,
`DottyDefaults.KittyGraphicsEnabled`). It stays off until a build has been run
through the [manual smoke checklist](#manual-smoke-checklist) on real displays.
While it is off the parser discards `ESC _ G ...` strings without any reply and
without printing anything (the same path PM, SOS and other APC strings take), so
a client that probes for support sees no answer.

Enable it in `config.json`:

```json
{ "graphics": { "kittyProtocol": true } }
```

or in Lua (`config.lua`):

```lua
dotty.config.graphics.kitty_protocol = true
```

All seven `graphics` keys, their defaults, and their reload semantics are in the
[configuration reference](Configuration.md#graphics).

## Supported and unsupported

### Actions (`a=`)

| `a` | Meaning | Keys the code reads |
|---|---|---|
| `t` (default) | Transmit | `f t o s v S O m i I p q N` |
| `T` | Transmit and place at the cursor | transmit keys plus the placement keys of `p` (taken from the first chunk) |
| `q` | Query: runs the whole validation and decode path, never stores, never touches an existing image | transmit keys; **requires `i`**, otherwise the command is silently ignored |
| `p` | Place a stored image at the cursor | `i I p x y w h X Y c r z C U P Q H V q` |
| `d` | Delete | `d i I p x y z` (see [delete matrix](#delete-matrix)) |
| `f` | Load or edit an animation frame (chunked like a transmit) | transmit keys plus `x y c r z X Y` (frame meaning, below) |
| `a` | Animation control | `i I s v r c z` |
| `c` | Compose a rectangle of one frame onto another | `i I r c x y X Y w h C` |

Any other `a` value answers `EINVAL:Unknown action` (when an id is known).
`i` together with `I` answers `EINVAL:Cannot specify both image id and image number`
for transmit, put and the frame commands.

Control data parsing: `key=value` pairs separated by commas; empty segments are
skipped; unknown or multi-letter keys are ignored; a duplicate key keeps the last
value; every byte of the control data must be printable ASCII (`0x21`-`0x7E`).
A known key with an empty, overflowing or malformed value makes the command
invalid (`EINVAL:Invalid key value` when an id was present). Unsigned keys are
32-bit, `z H V` are signed 32-bit, `q` is clamped to 0-2, `m C U` saturate at 255.

### Transmission keys

| Key | Behavior |
|---|---|
| `f` | `24` (RGB), `32` (RGBA, default), `100` (PNG). Anything else, including indexed formats, answers `EINVAL:Unknown image format`. `100` without a PNG decoder also answers it. |
| `s`, `v` | Pixel size of `f=24/32` data. Both must be 1 or more and at most the maximum dimension (`EFBIG:Image too large`). Ignored for PNG; the PNG header supplies the size. |
| `o` | `z` = zlib (RFC 1950) compressed payload. Any other value answers `EINVAL:Invalid key value`. For `f=100,o=z` the inflated size **must** be given in `S` and a medium other than `d` is refused (`EINVAL`). |
| `t` | `d` direct, `f` file, `t` temporary file, `s` shared memory. See [media](#transmission-media). |
| `S`, `O` | Size and offset of the bytes to read for `t=f/t/s`; `S` is also the inflated size for `f=100,o=z`. |
| `m` | `1` = more chunks follow. See [chunking](#chunking). |
| `i`, `I` | Image id (client chosen) and image number (terminal assigns an id). See [ids](#image-ids-and-numbers). |
| `p` | Placement id (honoured only for an image that has an id). |
| `q` | Quiet level. See [replies](#replies-and-quiet-modes). |
| `N` | Usage hint; bit 0 marks the image *transient* (evicted before others). |

A transmit that never finishes (disconnect, a non-chunk command, alternate screen
switch, `CAN`/`SUB`) is discarded; its buffers are released.

### Chunking

The first chunk carries all keys; continuation chunks carry only `m` (and may
carry `q`, which then overrides the first chunk's). A frame load may repeat `a=f`
on its continuation chunks. A command whose only keys are `m` and/or `q` with no
transfer open is dropped silently, which is also how the rest of a failed load is
swallowed (one error reply, not one per chunk). A new transmit, query or frame load aborts
the open load as soon as its control data arrives; a place, delete, animate or
compose command aborts it only when it completes, and a cancelled one leaves it
alone. Switching between the main and alternate
screen aborts it. Errors report the ids of the first chunk. An `OK` is sent only
when image data was actually loaded, never for an `m=1` chunk.

Base64 is decoded as a stream (a 3-character carry survives slices and chunks);
`=` padding is only valid at the very end of the last chunk; a non-alphabet byte
answers `EINVAL:Invalid base64 data`.

### Transmission media

| `t` | Support | Notes |
|---|---|---|
| `d` | always | Payload in the escape sequence. Cannot be disabled. |
| `f` | on by default (`graphics.allowFileTransfer`) | Regular file named by an absolute UTF-8 path. |
| `t` | on by default (`graphics.allowTempFileTransfer`) | Temporary file; deleted **only after a successful read**. |
| `s` | on by default (`graphics.allowSharedMemory`), Linux and Windows only | POSIX shared memory (Linux) or a named file mapping (Windows). **macOS (and any other OS) does not support `t=s`** and answers `EINVAL:Unsupported transmission type` so clients fall back to direct. |

For `t=f/t/s` the base64 payload is the *path or name* (at most 2048 decoded
bytes; strict UTF-8, no NUL/CR/LF). The file is read with the buffer lock
released. Every denial or read failure answers the single error
`EBADF:Failed to read image file`; the reason goes to the debug log only. A
medium that is disabled by configuration, unavailable on the platform, or shut
off after repeated hangs answers `EINVAL:Unsupported transmission type`.

Path policy (`KittyPathPolicy`):

- Relative paths, trailing separators, UNC paths and Windows device paths
  (`\\.\`, `\\?\`, `\??\`) are denied; a leading `\\` is denied on every OS.
- `/proc`, `/sys` and `/dev` (case-insensitive) are denied, also after symbolic
  links are resolved (every link, also in intermediate directories, is
  resolved; more than 40 hops is denied). `/dev/shm` is exempt for `t=t` and
  `t=s` only.
- On Linux the opened handle is checked again through `/proc/self/fd/N`; a pipe,
  socket or anonymous inode is refused.
- `t=t` additionally requires the resolved path to contain
  `tty-graphics-protocol` and to lie strictly inside a temp root (`Path.GetTempPath()`,
  `$TMPDIR`, and on non-Windows `/tmp` and `/dev/shm`).
- The file must be seekable and non-empty; `O` must lie inside it; a non-zero `S`
  larger than the remaining bytes fails. `S=0` reads to the end.
- A final-component swap between check and open is not defended against (the
  threat model is a program that can already write the files it names).
- An open that takes longer than 2 s is abandoned; after 2 abandoned opens in a
  session file, temp-file and POSIX shared-memory media answer unsupported until
  the session restarts.
- `t=s` on Linux maps the name to `/dev/shm/<name>` (one optional leading `/`,
  at most 255 bytes, no further `/`), verifies it resolves inside `/dev/shm`, and
  unlinks it after a successful read. On Windows it opens an existing named
  mapping (Windows reports page-rounded sizes, so clients should send `S`); the
  mapping is not removed.

`needs runtime verification`: every medium on every OS; in particular Windows named
mappings and the Linux handle check.

### Placement keys

| Key | Behavior |
|---|---|
| `x y w h` | Source rectangle in image pixels, clamped to the image (`w`/`h` 0 = to the edge). Cropping. |
| `X Y` | Pixel offset of the image inside its first cell, clamped to cell size - 1. |
| `c r` | Cells to fit. Both: **stretched** to the `c x r` box (no letterboxing). One: the other is derived from the source aspect ratio. Neither: native pixel size, `ceil((px + offset) / cell)` cells. Capped at 65535 cells. |
| `z` | Z-index (signed). See [layers](#z-index-and-layers). |
| `C` | `1` = do not move the cursor. |
| `U` | `1` = virtual placement for [Unicode placeholders](#unicode-placeholders). |
| `P Q H V` | [Relative placement](#relative-placements). |
| `p` | Placement id. Placing again with the same `(image, p)` replaces that placement. |

A placement is anchored at the cursor cell (`AnchorRow = ViewTop + cursor row`),
so it scrolls with the text, survives scrolling into history, and is deleted when
it scrolls past the history capacity. Unless `C=1`, `U=1`, or relative, the cursor
then moves like this: `col += cols`, `row += rows - 1`; a column at or past the
right edge wraps to column 0 of the next row; a row past the bottom margin scrolls
the region up by the overshoot and stays on the bottom margin (a 100-row image on
a 6-row screen scrolls 94 rows). A cursor already below the scroll region never
scrolls it; it clamps to the last row. `a=p` without `i` or `I` is silent. An
unknown image answers `ENOENT:Image not found`.

When the cell size changes (font zoom, DPI), placements sized by the cell
(native size, one dimension, in-cell offsets) are re-resolved; a cropped source
rectangle survives. A resize with reflow moves placements with their text;
placements whose line was trimmed or that land outside the retained rows are
deleted.

### Image ids and numbers

`i` is chosen by the client. `I` asks the terminal to pick: the terminal
allocates a free id from `0x80000000`-`0xFFFFFFFF` (walking upward, skipping used
ids) and the reply carries both `i` and `I`. The newest image with a given `I`
wins for later `a=p`/`a=d`/frame commands. An image with neither `i` nor `I` is
anonymous: a plain `a=t` with neither is discarded (nothing could address it), an
anonymous image that has no placement is garbage and removed, and no reply is
ever sent for it. Retransmitting an `i` replaces the old image and deletes all its
placements; the old one is deleted when the new transmit **starts**, so a failed
or interrupted retransmit leaves no stale image.

### Replies and quiet modes

Reply format: `ESC _ G i=<id>[,I=<number>][,p=<placement>] ; <message> ESC \`
(keys only when non-zero, in that order). A reply is sent only when an image id
or number is known, is queued when the APC terminator arrives (never for a
cancelled APC), and precedes anything the application sends after the APC.
`q=0` replies to everything, `q=1` suppresses `OK` (errors only), `q=2` suppresses
everything. `a=d` never replies. A command that raises an internal exception is
answered `EINVAL:Invalid key value` when an id is known.

| Reply message | Meaning in this implementation |
|---|---|
| `OK` | Image data was loaded, or the placement was made |
| `EINVAL:Invalid key value` | Malformed key; bad `o`; PNG with `o=z` but no `S` or with a medium; internal failure |
| `EINVAL:Unknown action` | `a` not one of `t T q p d f a c` |
| `EINVAL:Unknown image format` | `f` not 24/32/100, or no PNG decoder |
| `EINVAL:Invalid base64 data` | Bad alphabet, misplaced padding |
| `EINVAL:Cannot specify both image id and image number` | `i` and `I` together |
| `EINVAL:Image data size does not match declared size` | Compressed data inflates to the wrong size, or an implausible inflate ratio |
| `EINVAL:Unsupported transmission type` | `t` unknown, disabled by config, or unavailable on the platform |
| `EINVAL:Placement cannot be its own parent` | `P`/`Q` name the placement being created |
| `EINVAL:Virtual placement cannot be relative` | `U=1` together with `P`/`Q` |
| `EINVAL:Rectangle out of bounds` | A placement whose source rectangle starts outside the image (empty after clamping), or frame data / compose rectangle outside the image. (The message `EINVAL:Invalid placement geometry` exists in the table but nothing emits it.) |
| `EINVAL:Source and destination rectangles overlap` | `a=c` on the same frame with overlapping rectangles |
| `ENOENT:Image not found` / `Frame not found` / `Placement not found` | Unknown target |
| `ENOSPC:Storage quota exceeded` | The image (or frame canvas) does not fit the per-screen quota, or the frame cap was hit, or the whole `I=` id range is taken |
| `ENOSPC:Too many placements` | At the placement cap with no placement wholly in the history to evict |
| `ENODATA:Insufficient image data` | Raw data shorter than `s*v*bpp` |
| `EFBIG:Image too large` | Dimension above the maximum, or more than 2 GiB of pixels |
| `EFBIG:Payload too large` | Payload above the cap, or surplus data |
| `EBADF:Failed to read image file` | Any file/shared-memory denial or read failure |
| `EILSEQ:Failed to decode image data` | PNG decoder failure |
| `EILSEQ:Failed to inflate image data` | Corrupt zlib stream |
| `EBADPNG:Invalid PNG data` | Bad signature/IHDR, or a size implausible for the file length |
| `ETOODEEP:Relative placement chain too deep` | More than 8 links |
| `ECYCLE:Relative placement cycle` | The parent descends from the placement being replaced |
| `ENOPARENT:Parent placement not found` | Unknown parent image or placement |

`a=q` feature queries with `e=` are **not implemented**: the key parses and is
ignored. Whether replies of every error match kitty's recorded replies byte for
byte is `needs runtime verification` (the secondary oracle in the plan is to feed
the same corpus to Ghostty 1.4 or later and diff).

### Z-index and layers

Image quads are drawn in three layers around the cell pipeline:

| Layer | `z` | Draw order |
|---|---|---|
| BelowBackground | `z < -1073741824` (`-2^30`) | before cell backgrounds |
| BelowText | `-2^30 <= z < 0` | after backgrounds, before glyphs |
| AboveText | `z >= 0` | after glyphs |

Scrollbar and menu chrome draw after the AboveText layer. Within a layer quads
sort by `(z, image id, placement id)` and then input order. Placeholder quads use
`z = 0`.

### Delete matrix

`a=d` with `d=` (default `a`). Lowercase deletes placements only; uppercase also
frees the image data, but only for an image that matched and ended with no
placements. Deleting a placement that does not exist matches nothing and frees
nothing. A completed delete ends an open chunked load; a cancelled one does not.
`x`/`y` are 1-based (0 matches nothing). Positional selections (`a c p q x y z`)
only look at normal placements; virtual placements are reachable through
`i I r R n N` only.

| `d` | Selects |
|---|---|
| `a` / `A` | Placements that intersect the visible screen |
| `i` / `I` | Image `i` (and only placement `p` when given) |
| `n` / `N` | Newest image with number `I` (and only placement `p` when given) |
| `c` / `C` | Placements covering the cursor cell |
| `p` / `P` | Placements covering cell (`x`, `y`) |
| `q` / `Q` | As `p` and with z-index `z` |
| `x` / `X` | Placements covering column `x` |
| `y` / `Y` | Placements covering visible row `y` |
| `z` / `Z` | Placements with z-index `z` |
| `r` / `R` | Images whose id lies in `[x, y]` (inclusive; `x > y` selects nothing) |
| `f` / `F` | **Animation frame `r`** of image `i` or `I`; see below |
| anything else | Ignored |

Frame delete decision: `d=f`/`d=F` deletes frame `r` (1-based) of the image given
by `i` or `I`. **`r` is required**; without it nothing is deleted (a divergence:
kitty's frame delete without a frame number is not reproduced). Later frames shift
down (deleting frame 1 promotes frame 2 to root), each frame keeps its gap, the
displayed frame is kept if it survives, otherwise the frame that took its place is
shown. The frame's pixels are freed in both letter cases. Deleting the only frame
frees the whole image and its placements: an image cannot have zero frames.

### Relative placements

`P` (parent image id), `Q` (parent placement id; 0 = the parent image's oldest
placement), `H`/`V` (signed column/row offset from the parent's top-left cell).
A relative placement never moves the cursor, stores no anchor of its own (it
follows its parent through scroll, reflow and replacement), is deleted with its
parent, and may be nested at most 8 links deep. A chain rooted at a virtual
placement is positioned by the placeholder resolver. Errors: `ENOPARENT`,
`ECYCLE`, `ETOODEEP`, `EINVAL` (self parent, virtual relative).

### Unicode placeholders

A placement with `U=1` is virtual: it occupies a `c x r` grid and has no screen
anchor. An application draws `U+10EEEE` cells, each followed by up to three
combining diacritics, in cells it controls. The image id is the cell's foreground
colour (palette index, or 24-bit RGB) with the high byte from the third
diacritic; the placement id is the underline colour (none = 0, which selects the
virtual placement of that image with the lowest placement id). Diacritics encode
row, column and the id high byte from the 297-mark table
(`Diacritics.cs`, generated by `scripts/kitty-graphics/gen_diacritics.py`;
`needs runtime verification` against `kitten icat`). The three inheritance
rules for omitted diacritics (same row / column + 1 / same high byte as the cell
to the left, with equal colours) are implemented.

Each cell shows the part of the image that lies in that cell when the source
rectangle is **fitted into the grid with its aspect ratio kept and centred**
(letterbox/pillarbox, may upscale); horizontally adjacent cells of a grid row
merge into one quad. A placeholder cell is always one cell wide, never enters the
grapheme store, and its `U+10EEEE` glyph is **never drawn**, even when no image
exists. The resolver runs on the snapshot copy outside the buffer lock and does
nothing when the snapshot holds no virtual placement.

### Animation

`a=f` loads a frame; `a=a` controls playback; `a=c` composes. Frame keys:
`x y` (data rectangle origin), `s v` (data size), `c` (base frame, 0 = none),
`r` (frame to edit; 0 or absent = new frame), `z` (gap in ms), `X=1` (overwrite
instead of alpha-blend), `Y` (background colour `0xRRGGBBAA` for a new frame
without a base).

- **Gap rules.** `z > 0` = that many ms, `z < 0` = gapless (0), absent or 0 =
  default 40 ms for a new frame and "unchanged" for an edit. **The root frame has
  no gap until one is set** with `a=a,r=1,z=<ms>`; a frame without a gap is not
  held: playback steps past it at once on the first pass.
- **Control (`a=a`).** `s`: 1 stop, 2 loading (run to the last frame and wait for
  more frames), 3 run; other values are invalid. `v` = loop count with
  `max_loops = v - 1`, so `v=1` is infinite and `v=0` leaves it unchanged. Setting
  `v` restarts the loop counter; stopping resets it; starting from the stopped
  state (which includes an animation that ran into its loop limit) resets it;
  switching between loading and running keeps it. `r` with non-zero `z` sets that
  frame's gap. `c` selects the displayed frame. Commands are validated first, so a
  failing one changes nothing.
- **Compose (`a=c`).** Source frame `r`, destination `c`, destination offset
  `x y`, source rectangle `X Y w h`, `C=1` = overwrite. Both rectangles must lie
  inside the image (`EINVAL:Rectangle out of bounds`) and must not overlap on the
  same frame.
- **Storage is eager.** Every loaded or composed frame is stored as a full
  premultiplied-RGBA canvas of the image's size, so each displayed frame is its own
  `ImagePixels` (own GPU texture) and long animations reach the quota sooner than
  a delta-based store would. At most 4096 frames per image.
- **Clock.** Playback is pull-driven: it advances when a snapshot of a visible
  placement is captured, at most one frame per image per capture (no catch-up
  burst after a stall), and reports the earliest next deadline, which wakes the
  idle loop. There are no timers or threads. An image that is not captured (hidden
  tab, scrolled away) neither advances nor asks for a wake-up.

### Limits and defaults

| Limit | Value | Where |
|---|---|---|
| Storage quota | 320 MiB per **screen** (main and alternate each); `graphics.imageStorageLimitMiB`, at most 4096 MiB | `KittyImageStore` |
| Payload cap | 65 MiB; `graphics.maxPayloadMiB`, at most 1024 MiB | `KittyTransfer` |
| Maximum image dimension | 10000 px per side (options hard maximum 100000; not a JSON key) | `KittyGraphicsOptions` |
| Maximum placements | 8192 per screen (not a JSON key) | `KittyImageStore` |
| Maximum images | 4096 per screen | `KittyImageStore.DefaultMaxImages` |
| Maximum frames | 4096 per image | `KittyAnimation.MaxFramesPerImage` |
| Relative chain depth | 8 links | `KittyImageStore.MaxRelativeDepth` |
| Control data | 512 bytes (longer is discarded without a reply) | `BasicAnsiParser` |
| File path / shm name | 2048 decoded bytes | `KittyTransfer.MaxPathBytes` |
| Cells per placement | 65535 per axis | `PlacementGeometry.MaxCells` |
| Heavy-work threshold | 256 KiB decoded: inflate, PNG decode and premultiply run with the buffer lock released | `KittyTransfer` |

When the quota or the image cap is exceeded, eviction runs after every add and
when the limit shrinks: anonymous images without placements always go first; then,
while still over, images are removed in the order unreferenced, transient, least
recently used, lowest internal id. The newly added image is never evicted by its
own add. An image larger than the quota is refused (`ENOSPC`). When the placement
cap is reached, the oldest placement that lies wholly in the history is deleted;
without one the placement fails (`ENOSPC:Too many placements`).
Lowering a limit by configuration evicts immediately. RIS keeps the configured
limits.

Pixel memory is reserved only as data arrives. A header promising hundreds of MiB
followed by a few bytes reserves nothing (raw formats grow a staging block with
the data actually received; compressed and PNG payloads accumulate in a pooled
buffer capped at the payload cap and are given a destination only when the size is
plausible for that much compressed data).

## Screen semantics

- Each screen (main, alternate) has its **own** image store (ids, numbers, quota).
  Mode 47 retains the alternate store across visits. Mode 1047 clears it before
  returning to main; mode 1049 clears it on entry and exit.
- RIS deletes every image of both screens and aborts an open transfer.
- Ordinary ED 2 deletes every placement and discards history, even when visible
  ISO-protected text survives; selective ED 2 leaves placements and history alone.
  CSI 3 J deletes only placements wholly in history. Images with ids stay stored.
  DECCOLM clears visible placements unless VT5 no-clear mode 95 is enabled;
  history placements remain.
- Placements whose rows leave the retained history are deleted.
- A full-width margin scroll (DECSTBM region) moves ordinary placements wholly
  within its vertical bounds. A partial-width margin scroll also requires the
  placement's entire horizontal extent to fit within the left/right margins.
  Moving placements are cropped or deleted at the vertical boundary; placements
  already outside or straddling a boundary stay anchored (see divergences).
- IL/DL use the same vertical containment and crop/delete rules, from the cursor
  row through the bottom margin. They never rotate image `ViewTop` or enter history;
  a cursor outside either active margin axis leaves text and placements unchanged.
- DECIC/DECDC move ordinary placements wholly inside the rectangle from the cursor
  column through the right margin, across all scrolling rows. Placements left of
  that edit start remain anchored even if inside the broader margin region.
  DECBI/DECFI edge shifts use the full margin rectangle. These operations crop
  the source proportionally at horizontal boundaries, delete fully displaced
  placements and never regrow lost pixels when direction reverses. Relative
  children follow their parent; virtual placeholders move with their cells.
  Ordinary ICH/DCH behavior is unchanged.
- `ESC D` (IND) and `ESC E` (NEL) are parsed (`OnIndex`/`OnNextLine`): IND is a
  line feed without carriage return that scrolls at the bottom margin only when
  the cursor is horizontally eligible, holding there otherwise. Away from that
  boundary it moves down toward the physical last row. NEL is IND then
  margin-aware CR. Placements follow the same
  scroll hooks as for LF.
- Hidden tabs are not captured, so their animations pause; their images stay in
  the CPU store.

## Memory model

Two separate budgets:

1. **CPU pixels in the terminal core.** Each session (tab or pane) owns one
   `KittyImageStore` per screen, bounded by the per-screen storage quota, plus one
   in-flight transfer at a time (its staging bytes are counted as
   `pendingTransferBytes`). With several panes and two screens the worst case is
   the quota times the number of stores; there is no cross-session cap.
   `ImagePixels` is a reference-counted native buffer: a snapshot lease keeps a
   deleted image's pixels alive until the frame that uses it is done.
2. **GPU textures** (`SilkImageTextureCache`, GL thread only, keyed by the pixel
   buffer's serial): uploads are limited to 16 MiB per frame (at least one is
   always admitted, so an image larger than the budget still uploads; anything
   deferred raises `WindowFrameReason.ImageUpload` and another frame); resident
   textures are limited to an estimated 256 MiB (`4/3` factor for mip chains,
   mipmaps when both sides are 256 px or more); textures unused for more than 300
   frames are evicted regardless; the oldest-used, then largest, go first when
   over budget; textures used by the current frame are never evicted; images
   larger than `GL_MAX_TEXTURE_SIZE` are box-downscaled for upload. A freed
   `ImagePixels` queues its serial so the texture is deleted promptly. A renderer
   rebuild (render recovery) starts with an empty cache; textures re-upload on
   demand.

Image memory is **outside** `scrollback.limitBytes` (that limit counts the
12-byte-per-cell ring only). The `MEMORY` control command reports
`imagePixelBytes`, `imageCount`, `placementCount` and `pendingTransferBytes`
(process-wide ownership counters) and `imageTextureBytesEstimate` (renderer
estimate, mip chains included). None of them is RSS or driver memory.

## Threading model

- The PTY consumer thread parses and runs the controller under the buffer lock
  (`SyncRoot`). For large work (decoded size or payload of 256 KiB or more, and
  every `t=f/t/s` file read) the transfer **releases `SyncRoot`** through
  `TerminalBuffer.ReleaseSyncRootForBlockingWork` and re-acquires it afterwards,
  so the render thread keeps capturing snapshots while a big PNG inflates. This
  only releases one recursion level; when the lock is held recursively or not at
  all it is not released. After re-acquiring, nothing read before is trusted:
  the transfer checks cancellation (`Cancel`, disabling the protocol, disposal)
  and the live quota, and commits to the store of the screen that is active
  **now**. While the lock is released, `ConfigureGraphics`, `ClearImages` and
  `Dispose` can run safely (`KittyTransfer.Busy` stops them freeing buffers in use).
- Snapshot capture happens under the lock and is cheap when there are no
  placements: playing animations advance first, then visible placements are
  copied into a pooled `KittyRenderSnapshot` and each takes a **lease** on its
  `ImagePixels`. The snapshot releases the leases when it is disposed.
- The render thread then runs the placeholder resolver and quad builder on the
  snapshot copy **outside the lock**, and the GL thread uploads textures. The
  composer drops the previous frame's leases before taking new ones.

`needs runtime verification`: that a 64 MiB transfer does not make the renderer
skip frames beyond the existing 4 ms policy (`KittyLockHoldProbe` is the
experiment for it, see [Performance](Performance.md)).

## tmux

Dotty does not interpret `DCS tmux ;` passthrough wrappers; the DCS handler
accepts only `DCS + q` (XTGETTCAP). tmux unwraps its own passthrough
(`set -g allow-passthrough on`) and forwards the raw APC to Dotty. Unicode
placeholders are the supported way to show an image inside a tmux pane: the
placeholder cells are ordinary text that tmux stores, scrolls and redraws itself,
so the cells themselves need no passthrough. Transmitting the image data still
requires that the APC reaches Dotty (passthrough, or a transmit from outside
tmux). `needs runtime verification` with `allow-passthrough on` and off.

## Windows

ConPTY drops APC strings by default [INFERENCE: its VT renderer re-emits only
sequences it understands]. `graphics.windowsPassthrough` (default `false`,
experimental) asks `CreatePseudoConsole` for `PSEUDOCONSOLE_PASSTHROUGH_MODE`
(`0x8`) so APC is forwarded. If the host rejects the flag, Dotty retries once
without it (`WindowsPty.GraphicsPassthroughActive` reports the outcome). A build
that silently ignores the flag still reports it active, so whether APC really
arrives is only observable at runtime. Passthrough may change how ConPTY forwards
other VT output; verify on Windows before relying on it. The setting is read when
a PTY is created, so it applies to new tabs and panes only. Pixel sizes are not
available on ConPTY: the initial and resize pixel arguments are ignored by the
Windows backend (there is no `TIOCGWINSZ`); `CSI 14 t`/`16 t`/`18 t` replies are
built by the adapter from host-supplied sizes. `t=s` on Windows uses named file mappings. See
[Windows ConPTY](WindowsConPty.md).

## Divergences from kitty

- **ED 2 / CSI 3 J.** Ordinary ED 2 discards history and deletes every placement;
  visible ISO-protected text can survive its text erasure. Selective ED 2 does not
  discard history or placements. CSI 3 J deletes only placements in history and
  leaves visible ones (and visible text), where kitty deletes all placements on ED 3.
- **Margin scrolling.** "Only placements entirely inside the margins move, are
  cropped when they cross a margin and deleted when fully outside" is implemented
  as a sequence: placements wholly inside the region move; one that leaves the
  region is cropped at the crossed margin (source rectangle shrinks
  proportionally, the cropped part never grows back) and deleted when nothing
  stays inside. Partial-width vertical scrolling and DEC horizontal column/index
  scrolling require initial containment on both axes; full-width vertical
  scrolling retains vertical-only eligibility, including images overhanging the
  screen's horizontal edges. Horizontal movement/cropping was verified in Dotty's
  actual X11 renderer with pixel bounds and retained source colors in G08.
  Placements that already straddle a margin (including a tall image placed at the
  bottom margin) are left alone while the text under them scrolls. This is a
  reading of the behavior, `needs runtime verification` against kitty.
- **Eager animation storage.** Full canvases per frame, not deltas.
- **Stretch.** With both `c` and `r` the image is stretched to the box (kitty
  draws it that way although the spec text says letterbox). Only placeholder grids
  letterbox.
- **`d=f` requires `r`.**
- **`e=` feature queries** are not implemented; indexed pixel formats are not
  implemented.
- Image number allocation, eviction order and the transient hint follow the
  published protocol text and observed behavior; they are not byte-identical to
  any reference implementation.

## Licensing and clean room

Kitty is GPL-3 and Dotty is MIT. The implementation was written from the
published protocol specification, behavior notes, and black-box expectations;
no kitty code or test code was copied. Ghostty (MIT) was consulted for behavior
with attribution in the plan. The diacritics table is generated by
`scripts/kitty-graphics/gen_diacritics.py` from Unicode ranges and structurally
checked by the script.

## Troubleshooting

- Check that `graphics.kittyProtocol` is `true` and that the tab was not started
  with an old config you did not reload. `graphics.*` reload behavior is in the
  [reference](Configuration.md#graphics).
- `kitten icat --detect-support` probes the terminal. Dotty sets
  `TERM=xterm-256color` by default (or `xterm-dotty`), not `xterm-kitty`, so clients must rely on the query
  rather than the terminfo name; pass `--transfer-mode=stream` to avoid file and
  shared-memory media if they are disabled.
- Quick query by hand (the reply is `OK` and DA1 follows; nothing must be
  printed):

  ```sh
  printf '\e_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\e\\\e[c'
  ```

  Expected with the protocol on: `ESC _ G i=31;OK ESC \` then the DA1 reply.
  With it off: only the DA1 reply. `needs runtime verification`.
- An image that vanishes after `clear` or `reset` is expected (see [screen
  semantics](#screen-semantics)).
- `ENOSPC` means the per-screen quota (`graphics.imageStorageLimitMiB`) or the
  placement cap; `EFBIG` means the dimension or payload cap.
- Over SSH or tmux, use `--transfer-mode=stream` (direct); files named by a
  remote program do not exist on the local machine.
- `MEMORY` (control interface) shows the image counters.

## Manual smoke checklist

Run on real displays (Linux X11 and Wayland, macOS, Windows) with the flag on.
This is the gate for turning the default on; none of it has been run.

1. `kitten icat` a PNG, a JPEG and an animated GIF (animation plays, stops when
   its tab or pane is hidden).
2. `kitten icat --place 20x10@5x3 image.png`.
3. `kitten icat --unicode-placeholder image.png`; no `U+10EEEE` glyph visible.
4. `timg -p kitty image.png`, `chafa -f kitty image.png`.
5. yazi previews; nvim with `image.nvim`.
6. tmux with `allow-passthrough on`, then with placeholders.
7. Split panes: images are clipped to their pane.
8. Scroll the image into scrollback and back; scroll with text.
9. Resize (reflow) and change font zoom and DPI while an image is shown.
10. `clear`, `reset`, `printf '\ec'`: images disappear as documented.
11. Alternate screen (`less`, `nvim`): the main-screen image is hidden and
    returns. Mode 1049 leaves no alternate images; mode 47 retains and redraws
    them on the next visit, while mode 1047 clears them on exit.
12. A large (about 60 MiB) transfer keeps the window responsive.
13. `t=f`, `t=t`, `t=s` via `kitten icat --transfer-mode=file|memory` (Linux,
    Windows) and macOS fallback to direct.
14. Windows: with and without `graphics.windowsPassthrough`.

## Source map

| Area | Files |
|---|---|
| Options and defaults | `Dotty.Abstractions/Graphics/KittyGraphicsOptions.cs`, `Config/DottyDefaults.cs`, `Dotty.Runtime/Config/DottyUserConfig.cs` |
| APC framing | `Dotty.Terminal/Parser/BasicAnsiParser.cs` |
| Command, control parser, replies | `Graphics/KittyGraphicsCommand.cs`, `KittyControlParser.cs`, `KittyResponse.cs` |
| Controller and transfer | `Graphics/KittyGraphicsController*.cs`, `KittyTransfer.cs`, `KittyBase64Decoder.cs`, `BoundedInflate.cs`, `PngHeader.cs` |
| Media | `Graphics/IKittyMediumReader.cs`, `KittyFileMedium.cs`, `KittyPathPolicy.cs`, `KittySharedMemory.cs` |
| Store, placements, delete, animation | `Graphics/KittyImageStore*.cs`, `KittyAnimation.cs`, `PlacementGeometry.cs`, `KittyCursorPolicy.cs` |
| Placeholders | `Graphics/PlaceholderResolver.cs`, `PlaceholderTable.cs`, `Diacritics.cs` |
| Snapshot | `Graphics/KittyRenderSnapshot.cs`, `Adapter/Buffer/TerminalBuffer.Graphics.cs`, `RenderSnapshot.cs` |
| Rendering | `Dotty.Rendering.Gpu/ImageQuadBuilder.cs`, `ImageDrawList.cs`, `ImageUploadPlanner.cs`, `SkiaImageDecoder.cs`; `Dotty/SilkImageRenderer.cs`, `SilkImageTextureCache.cs`, `Rendering/TerminalSceneComposer.cs` |

## Changelog

| Date | Change |
|---|---|
| 2026-10-28 | First version, written from the source without building or running anything; protocol default off pending the manual smoke. |
