# Rosella architecture

Goal: a heavily opinionated desktop environment with its own compositor /
display server in C#/.NET 10, built to learn how this stack works. VM-first
testing (a VM is "not a real test" for GPU/input edge cases, but it is
something — and the DE logic is fully testable there).

## The stack, bottom to top

| Layer | Component | Who talks to it |
|---|---|---|
| Kernel | DRM/KMS (modesetting, page flips, scanout) | Rosella, via ioctl bindings |
| Kernel | evdev (input devices) | libinput |
| Userspace | Mesa: GBM (buffers), EGL/GL or Vulkan (rendering) | Rosella, via P/Invoke |
| Userspace | libinput (gesture/keyboard/mouse semantics) | Rosella, via P/Invoke |
| Protocol | Wayland wire format (client ⇄ compositor IPC) | **Rosella implements both sides** |
| Shell | taskbar, pinned launchers, system menu, windows | Rosella (managed, in-process) |

A key mental-model point: Wayland is *not* a driver API. It is the IPC between
apps (clients) and the compositor. The compositor itself talks to drivers:
DRM/KMS for putting pixels on the screen, Mesa for rendering, libinput for
input. Mutter and friends "just" implement the server side of that IPC and
render the result — which is exactly what Rosella does too, in C#.

## Wire-format facts the codec is built on

Verified against libwayland's marshalling code and the protocol docs:

- Messages are sequences of 32-bit words in **native byte order** (both peers
  are on the same machine).
- Header (8 bytes): object id, then one word with the **total message size in
  the high 16 bits** and the **opcode in the low 16 bits**. Size includes the
  header and is a multiple of 4; max message is 65535 bytes.
- `string`: 32-bit length **including the terminating NUL**, then the bytes,
  NUL, then zero-padding to a word boundary. Length 0 is the null string;
  interior NULs are illegal.
- `array`: 32-bit length (no terminator), bytes, padded to a word boundary.
- `fixed`: signed 24.8 in one word.
- `object`/`new_id`/`int`/`uint`: one word. Object id 0 is the null object;
  `new_id` is never 0.
- `fd`: no words at all — descriptors travel out-of-band via SCM_RIGHTS and
  are consumed in signature order.
- **Untyped `new_id`**: a `new_id` with no interface in the protocol XML (only
  `wl_registry.bind` in the core protocol) expands to *three* wire fields —
  interface-name string, version uint, then the new id. Hence `bind`'s wire
  signature is `"usun"` even though its XML signature is `"un"`. (This is
  poorly documented upstream; confirmed from wayland's marshalling tables.)

## Key decisions

1. **Own compositor, not a Mutter plugin.** More control, more understanding,
   no fighting someone else's policy. The managed-code seam is real: wire
   protocol and window policy are pure C#; only DRM/KMS, Mesa and libinput are
   native ABI interop (P/Invoke declarations, not a C codebase).
2. **Hand-rolled wire codec** (M1) rather than libwayland or NWayland. The
   format is small and it *is* the curriculum.
3. **Opinionated**: almost no configurable settings. Hardcoded defaults;
   no settings daemon, no config files.
4. **Strict decoding.** Malformed input is a protocol error, not something to
   quietly accept: size/signature mismatches, bad strings and missing fds all
   throw `WireProtocolException`. A compositor that shrugs at bad bytes is a
   compositor that gets confused later.
5. **Test in a VM, iterate nested.** Real seat/KMS testing happens in a VM
   (virtio-gpu, virtio-input). A nested/windowed development backend can come
   later for fast iteration; the DE logic itself is testable anywhere.
6. **The field-report policies** (all hardcoded, no settings): per-output
   integer DPI scaling chosen like mutter (`ScalePolicy`: 135 DPI target
   under 20″, 110 above; falls back to 2× for laptop-class panels whose
   firmware fabricates a 96-DPI placeholder size); every toplevel opens
   maximised to the work area, always — there is no floating; apps launch
   with `GTK_THEME=Adwaita:dark` / `ADW_DEBUG_COLOR_SCHEME=prefer-dark`;
   ARGB buffers blend source-over with premultiplied alpha (client-side
   decoration shadows render as translucent veils, not black squares); the
   pointer image is the focused client's `set_cursor` surface at
   pointer−hotspot (hidden on NULL), else the built-in arrow — always scoped
   to the owning client's pointer focus.

## Milestones

- **M1 — wire codec**: ✅ framing, signatures, all argument types, fd
  accounting, golden-bytes tests.
- **M2 — display server loop**: ✅ unix socket transport (SCM_RIGHTS via
  raw recvmsg/sendmsg), `wl_display` handshake, registry globals, per-client
  object id spaces (client < 0xFF000000, server ≥), `delete_id` lifecycle,
  `since`-version gating in both directions, stub interfaces that survive real
  clients. Exit test passed: `wayland-info` (real libwayland) lists all six
  globals and parses every event (shm formats, seat, output geometry/mode).
- **M3 — output**: ✅ DRM/KMS modesetting + scanout, run natively on mercury
  as a GDM login session (a VM remains an optional fallback). Rosella.Drm
  pins every struct layout and ioctl code against the kernel UAPI with golden
  tests; `--probe` validated the whole interop surface against real drivers
  (amdgpu + nvidia-drm — both report `CAP_DUMB_BUFFER` and allocate dumb
  buffers fine, settling the NVIDIA risk). Scanout drives the first connected
  output (on mercury: the SONY TV at 3840x2160@60) with a software-rendered
  the desktop via a page-flip loop; the previous display state is restored on
  exit. Pending: visual confirmation in the GDM session.
- **M4 — client buffers**: ✅ real `wl_shm` (client descriptors are dup'd,
  size-checked with lseek and mapped read-only), `wl_surface` state machine
  with copy-at-commit content (buffer released immediately after the copy),
  frame callbacks with protocol-correct payloads, `get_release`/`no_buffer`
  rules, pool/buffer refcounting so any destruction order is safe, and
  compositing of client windows over the background. Servers and the render
  loop share the `Scene` under a strict lock order (scene → surface state →
  session). Confirmed on screen 27/10/2026 on silver (simpledrm panel).
- **M5 — xdg-shell**: ✅ windows: the commit → configure → ack_configure →
  buffer-commit map dance with strict serial consumption (ack consumes the
  serial and all older ones), window policy placement (centre + per-window
  cascade, placed once per map and raised), double-buffered window geometry
  shifting the content origin, popups placed from a frozen positioner
  snapshot (anchor rect + offset, relative to the parent's window geometry
  in the event), wm_capabilities before the first configure, and enforced
  destroy order (wl_surface/xdg_surface/role object) with dead windows
  unmapped immediately. Reviewed once; 15 findings fixed with regression
  tests. Confirmed on screen (silver, 27/10/2026): the smoke client maps a
  real window via the full dance, with live frame callbacks.
- **M6 — input**: ✅ done (confirmed on hardware: keyboard, mouse, multitouch
  trackpad; click-to-focus, Alt+F4 close, Super+Esc quit). evdev read directly
  (no libinput): /dev/input discovery, capability bitmaps, exclusive grabs
  (EBUSY skips the device), poll() + wake-eventfd reads whose shutdown never
  closes an fd under a blocked reader. One keymap: US QWERTY generated with
  xkbcommon, a fresh memfd per wl_keyboard (SCM_RIGHTS shares the file
  description offset otherwise). Real wl_seat/wl_keyboard/wl_pointer:
  name-then-capabilities at bind, missing_capability (0) for absent
  capabilities incl. all touch, keymap + repeat_info before registration,
  enter carries the held keys and is always followed by modifiers, frame
  gated at v5, release/teardown unregister. The policy: click-to-focus +
  raise, keys to the focus, xdg activation configure rounds, Alt+F4 =
  close, Super+Esc = quit; one cursor sprite, set_cursor ignored. All seat
  sends run under the seat gate (serialised against object death: nothing
  can name an object after delete_id) and per-target try/catch so one dead
  client never poisons the fan-out. Reviewed once — 15 findings fixed
  (3 crash-grade: unhandled SocketException on input threads, send-vs-
  destroy id resurrection, unlocked xdg configure state), 10 new regression
  tests. Known accepted limits are in Open risks.
- **M7 — the shell**: ✅ done, then redesigned to Windows 11 style on the
  user's request: a bottom taskbar (apple button, task buttons with a focus
  accent, clock in AU format), the pinned app launchers centred on the bar
  (six fixed entries resolved from XDG desktop files — GLib-style Exec
  parsing, icons decoded through gdk-pixbuf into premultiplied ARGB, click
  focuses the app's newest window or launches it with the right
  WAYLAND_DISPLAY), and an apple system menu (Log Out, Restart, Shut Down —
  ISystemActions so tests can fake them). No workspaces — one desktop, by
  design. The
  background is a calm dark gradient (the milestone-3 colour bars and
  moving stripe are gone: the user found them annoying). Windows sit in
  the work area above the taskbar, clamped to stay there, and wear an
  accent border when focused. One embedded 8x16 bitmap font (Linux kernel
  font_8x16, scaled 2x); drawing and hit-testing share one cached taskbar
  layout per frame. Notifications did not make the cut and may never: an
  opinionated desktop can do without them.
- **M8 — session polish**: window states (maximise/fullscreen), lock
  screen, idle handling, session startup, whatever the DE needs to feel
  finished.

## Conventions the implementation follows

Learned from libwayland and verified against real clients:

- **Error attribution**: violations by unknown object ids are reported with
  `wl_display.error` *on the display object* ("invalid object %u"), because
  clients cannot resolve events naming ids they never created. Bad opcodes and
  version violations are reported on the offending object; `new_id` violations
  on the requesting object ("invalid new id %u"). Malformed requests are
  `invalid_method` (that is how wayland.xml defines it); `implementation` is
  reserved for server-side faults. Every error is fatal: report, then close.
- **Descriptor passing**: fds ride in SCM_RIGHTS control messages attached to
  the *first* write of a message burst (re-attaching on partial writes
  duplicates them); libwayland sends at most 28 fds per sendmsg, the kernel
  accepts at most 253 (SCM_MAX_FD); receivers must handle MSG_CTRUNC and
  close every delivered fd on failure paths.
- **Version gating**: messages carry `since`; events newer than the bound
  object's version are never sent, and requests newer than the bound version
  are rejected. Binding always honours the client's requested version.

## The control socket

The compositor serves a second socket beside the Wayland one —
`$XDG_RUNTIME_DIR/rosella-control.sock` — so outside automation (the
author's assistant over SSH) can see and drive the desktop without a
human at the keyboard. `tools/Rosella.Driver` (`rosella-driver`) speaks it:

- `frame <out.ppm>` — the composited screen, captured through the render
  loop's buffers (the same ground truth the SIGWINCH dump writes)
- `table` / `state` — surface state and a session summary
- `move` / `warp` / `button` / `key` / `click` / `tap-key` — input
  injected as evdev-shaped events through the real input router, so
  bindings, focus and hit-testing behave exactly as for a hand

Protocol: one request line (≤256 bytes) per command; answers are
`OK <n>\n` + n raw payload bytes or `ERR <message>\n`. The socket is
0600 and unauthenticated like the Wayland socket — it can drive the
seat, so it is session-trusted by design. One capture may be in flight
at a time; the server caps concurrent clients at 8 and survives wedged
or hostile clients without touching the compositor.

## Open risks

- DRM/KMS + Mesa interop was the fiddliest native surface; M3 proved the
  modesetting side against real drivers, but only one output is driven at a
  time and the exit-restore re-binds only that connector (multi-output and
  clone-aware restore land with the shell).
- Being the login session means a bug yields a blank screen, not a crash:
  recovery is a VT switch (`Ctrl+Alt+F3`) or picking GNOME at the greeter.
  The flip loop is token-interruptible, so signals cannot wedge it silently.
- Known deferred items (accepted for now, revisited with the later
  milestones): stray descriptors sent by a hostile local client accumulate in
  the message stream (fd exhaustion); a client that stops reading can stall
  event sends and briefly stall the scanout thread; a commit landing in the
  microseconds between render and `CompleteFrame` can get its frame callback
  fired one frame early; a client that truncates its pool file after mapping
  can still SIGBUS the compositor (the common lying-size case is checked up
  front); `get_xdg_surface` on a surface that already has content is not
  rejected (the spec names no error code); popup stacking rules
  (`not_the_topmost_popup`, parent-mapped-first) and positioner constraint
  adjustment (flip/slide to stay on screen) are unimplemented; `xdg_toplevel`
  state *requests* (maximise/fullscreen/minimise) are ignored — the policy is
  always-maximised, and the states we send are M8's concern; the serial match
  for `set_cursor` is per pointer device where the spec words it per client
  (GTK/Qt bind one pointer per seat); `wl_surface.offset` on a cursor surface
  is accepted but the hotspot is not decremented at commit (clients pass the
  hotspot in `set_cursor`); evicted configure serials stay ackable but never
  count as mapping consent; the 96-DPI placeholder guard cannot distinguish a
  fabricated size from a genuine 96-DPI ≥2560×1600 panel (the rare real panel
  would get 2× instead of 1×). M6 additions: seat sends hold the seat gate
  during socket writes, so one stalled client can briefly delay input
  delivery to others (accepted on a single-seat desktop); kernel autorepeat
  is dropped (clients repeat via repeat_info); no touch support; a keyboard
  bound mid-focus receives enter with the seat's last held-keys snapshot;
  input devices are polled for hot-plug once a second (a replug can lag up
  to a second, and udev permission lag is retried on later polls); the idle
  compositor composites only on scene changes and wakes at most once a
  second for the clock tick.
- The M2/M3 code has been reviewed three times at the code level; M4 adds the
  surface layer, reviewed once. Only `wayland-info` and `SmokeClient` have
  exercised the protocol as clients so far — toolkits (GTK/Qt) will stress it
  more once xdg-shell lands.
