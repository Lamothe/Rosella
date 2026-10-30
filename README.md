# Rosella

Working name — rename freely if something better comes along.

A heavily opinionated desktop environment, including its own compositor /
display server, written in C# on .NET 10. The point is to learn how desktop
environments and display servers actually work by building one, from the wire
protocol up.

Rosella is **not** a Mutter/GNOME Shell plugin and does not link libwayland.
It speaks the Wayland wire protocol itself in managed code, and talks to the
machine through the usual Linux graphics/input ABI seams:

- **DRM/KMS** (kernel) — modesetting, page flips, scanout
- **Mesa** (GBM/EGL, GL or Vulkan) — GPU buffers and rendering
- **libinput/evdev** — input devices

See `docs/architecture.md` for the layer map, the wire-format facts the codec
is built on, and the milestone plan.

## Design stance

Opinionated by design: almost no configurable settings. Defaults are chosen
and hardcoded; there is no settings surface to maintain. This is deliberate —
configuration is where desktop environments go to die.

## Status

- **M1 — Wayland wire codec** (`Rosella.Protocol`): done. Framing, signatures,
  all argument types (including untyped `new_id` expansion semantics), 24.8
  fixed-point, SCM_RIGHTS fd accounting, strict malformed-input rejection.
- **M2 — display server loop** (`Rosella.Transport`, `Rosella.Server`): done.
  Unix socket transport with SCM_RIGHTS, `wl_display` handshake, registry with
  globals, per-client object id spaces, `since`-version gating, stub interfaces
  that survive real clients (`wayland-info` exit test passed).
- **M3 — output** (`Rosella.Drm`): done, pending visual confirmation in the
  GDM session. DRM/KMS interop pinned against the kernel UAPI by golden tests,
  dumb buffers + mmap, framebuffers, legacy modeset, page-flip loop and the
  desktop on the first connected output. `--probe`
  introspects cards/connectors/modes and validates dumb buffers against the
  real driver.
- **M4 — client buffers** (`Rosella.Compositor` surface layer): done —
  confirmed on screen 27/10/2026 (silver: test-pattern background plus a
  real client window). Real `wl_shm` (client descriptors mapped, bounds
  validated), `wl_compositor`/`wl_surface` state machine (attach/damage/frame/
  get_release/commit), `wl_buffer` release, and compositing of client windows
  over the test-pattern background. Content is copied at commit (then the
  buffer is released); frame callbacks pace clients. Verified end to end over
  real sockets with descriptor passing, plus `wayland-info` and the bundled
  `tools/Rosella.SmokeClient`.
- **M5 — xdg-shell**: done and confirmed on screen (silver, 27/10/2026). Windows are real:
  `xdg_wm_base`/`xdg_surface`/`xdg_toplevel`/`xdg_popup`/`xdg_positioner`
  (v7), the full commit → configure → ack_configure → map dance with strict
  serial consumption, window policy placement (centre + small cascade,
  raised on map), double-buffered window geometry, popups positioned by a
  frozen positioner snapshot relative to the parent window, and correct
  unmap/teardown semantics (role destroy unmaps; destroy order is enforced).
  The smoke client now opens a genuine xdg-shell window.
- **M6 — input**: done (pending on-screen check). evdev devices are read
  directly (no libinput): discovery, capability probing, exclusive grabs and
  poll()-based reads that shut down cleanly. The keyboard keymap is the one
  US QWERTY map, generated with xkbcommon. `wl_seat`/`wl_keyboard`/
  `wl_pointer` implement the real protocol (keymap fd per keyboard, enter
  with the held keys, mandatory modifiers after enter, repeat_info, frame
  gated at v5). The input policy is the only policy: click-to-focus with
  raise, keys to the keyboard focus, xdg_toplevel activation configure
  rounds, and two hardcoded keybindings (Alt+F4 = close window,
  Super+Esc = quit). One built-in cursor sprite, drawn topmost;
  `set_cursor` is deliberately ignored. All seat sends are serialised under
  the seat gate against object death, so no event can name an object after
  its `delete_id`.
- **M7 — shell**: done. The Windows 11 style shell chrome: a bottom
  taskbar (apple button, pinned app launchers centred on the bar, task
  buttons with a focus accent, clock), and the apple system menu (Log Out,
  Restart, Shut Down).

## Layout

```
src/Rosella.Protocol/          Wayland wire format + protocol description (embedded wayland.xml)
src/Rosella.Transport/         unix sockets, recvmsg/sendmsg, SCM_RIGHTS descriptor passing
src/Rosella.Server/            display server: sessions, object maps, dispatch, globals
src/Rosella.Drm/               DRM/KMS interop: devices, connectors, modes, dumb buffers, flips
src/Rosella.Compositor/        entry point: scanout loop, desktop background, --probe
scripts/                       GDM session registration, session wrapper, and the `rosella` VT launcher
tests/Rosella.Protocol.Tests/  unit tests, including golden wire bytes
tests/Rosella.Server.Tests/    transport + end-to-end handshake tests
tests/Rosella.Drm.Tests/       kernel ABI golden layouts, ioctl codes, event parsing
tests/Rosella.Compositor.Tests/shell chrome, input and flip matching
docs/architecture.md           design notes and roadmap
```

## Build & test

```sh
dotnet build
dotnet test tests/Rosella.Protocol.Tests/Rosella.Protocol.Tests.csproj
dotnet test tests/Rosella.Server.Tests/Rosella.Server.Tests.csproj
dotnet test tests/Rosella.Drm.Tests/Rosella.Drm.Tests.csproj
dotnet test tests/Rosella.Input.Tests/Rosella.Input.Tests.csproj
dotnet test tests/Rosella.Control.Tests/Rosella.Control.Tests.csproj
dotnet test tests/Rosella.Compositor.Tests/Rosella.Compositor.Tests.csproj
```

Note: `dotnet test` run against the `.slnx` at the repo root currently no-ops
(SDK 10.0.112) — invoke the test project directly as shown above.

Golden-byte tests assume a little-endian host (the wire format is native
byte order; Rosella targets x86_64/ARM64).

## Running Rosella

Two entry points, both running the same build.

### As a login session (the normal way)

`scripts/install-session.sh` publishes Rosella and registers it with GDM, so
"Rosella" appears in the greeter's session chooser alongside GNOME. It needs
sudo once; day-to-day updates do not (`scripts/deploy.sh`, then restart).

To use it: log out of GNOME, click the session chooser (the gear icon at the
greeter), pick **Rosella**, and log in.

### From a VT, alongside GNOME (the dev way)

GDM permits one session per user, so you cannot start Rosella from the
greeter while GNOME is running. To try the desktop without disturbing your
session, switch to a spare VT (`Ctrl+Alt+F3`), log in, and run:

```sh
rosella
```

That is `scripts/rosella-dev.sh`, installed to `~/.local/bin` by
`scripts/deploy.sh`. It stops any previous instance first — two compositors
on one seat steal each other's input devices and the keyboard dies — then
runs the freshly published build, logging to `/tmp/rosella.log`.

### What you get

The first connected output is driven at its preferred mode with the Windows
11 style desktop: a bottom taskbar (apple button, pinned app launchers
centred on the bar, task buttons, clock), an apple system menu (Log Out,
Restart, Shut Down), and a calm dark gradient background — no workspaces, by
design. The Super key is unbound; Alt+F4 closes a window and Super+Esc quits.

Recovery if anything goes wrong: `Ctrl+Alt+F3`, log in,
`pkill -f Rosella.Compositor`, or simply pick GNOME at the next greeter — it
is always offered.

## Probe

```sh
dotnet run --project src/Rosella.Compositor -- --probe
```

Prints each card's driver, capabilities, connectors, encoders and modes, and
tests dumb-buffer allocation. Read-only apart from that allocation round trip
— safe to run inside a live session.

## Real-client check

Run the compositor and point any Wayland client at it (the compositor prints
its socket name — it takes the first free `wayland-N`, since another session's
compositor may already own `wayland-0`):

```sh
dotnet run --project src/Rosella.Compositor -- wayland-1
WAYLAND_DISPLAY=wayland-1 wayland-info
```

(`wayland-info` comes from the `wayland-utils` package.)

## Smoke client

`tools/Rosella.SmokeClient` is a dependency-free Wayland client that draws an
animated window (gradient + moving bar) into an shm buffer, paced by frame
callbacks. Point it at a running Rosella to put a real client window on the
scanout:

```sh
dotnet run --project tools/Rosella.SmokeClient -- wayland-1
```

